using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Database.Export;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>Snapshot of the export job's state, returned by <c>GET /api/export-infini-dysk</c>.</summary>
/// <param name="State">One of <c>idle</c>, <c>running</c>, <c>completed</c>, <c>failed</c>.</param>
/// <param name="Message">Last human-readable stage or outcome.</param>
/// <param name="OutputDirectory">Directory the current/last run targeted.</param>
/// <param name="CompletedAtUtc">When the last successful run finished, if any.</param>
/// <param name="Bytes">Size of the produced <c>db.sql</c>, if known.</param>
/// <param name="TableRows">Per-table row counts from the last successful run.</param>
/// <param name="NzbBlobCount">NZB blob files written by the last successful run.</param>
/// <param name="FileBlobCount">Metadata blob files written by the last successful run.</param>
/// <param name="Inspectable">True when the reported state came from disk rather than memory.</param>
/// <param name="Warning">Set when the produced dump cannot be imported through InfiniDysk's upload.</param>
public sealed record InfiniDyskExportStatus(
    string State,
    string Message,
    string? OutputDirectory = null,
    DateTimeOffset? CompletedAtUtc = null,
    long? Bytes = null,
    IReadOnlyDictionary<string, int>? TableRows = null,
    int? NzbBlobCount = null,
    int? FileBlobCount = null,
    bool Inspectable = false,
    string? Warning = null);

/// <summary>
/// Owns the InfiniDysk export as a background job rather than as part of an HTTP request.
///
/// The request that starts an export returns immediately; the job then runs on a token that is
/// independent of the caller. That matters because an export streams the whole database and can
/// take minutes, and the browser going away (tab closed, reload, laptop asleep) must not throw
/// the work away — which is exactly what would happen if the job used
/// <c>HttpContext.RequestAborted</c>.
///
/// Progress is published on <see cref="WebsocketTopic.InfiniDyskExportProgress"/>. Because that
/// topic is a cached State topic, a client that connects after the fact is replayed the latest
/// stage, so the UI can report the outcome even after a reload or a restart of the page.
/// </summary>
public sealed class InfiniDyskExportRunner(WebsocketManager websocketManager)
{
    /// <summary>
    /// camelCase so the websocket payload matches the REST response shape — the UI reads the
    /// same fields from both, and PascalCase here silently yields an undefined `state`.
    /// </summary>
    private static readonly JsonSerializerOptions StatusJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private Task? _running;
    private CancellationTokenSource? _cts;
    private InfiniDyskExportStatus _last = new("idle", "No export has run yet.");

    /// <summary>True while an export is in flight.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate) return _running is { IsCompleted: false };
        }
    }

    /// <summary>Current status, from memory only (may be <c>idle</c> after a restart).</summary>
    public InfiniDyskExportStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _running is { IsCompleted: false }
                    ? _last with { State = "running" }
                    : _last;
            }
        }
    }

    /// <summary>
    /// Starts an export in the background.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the job was started; <c>false</c> when one is already running (the
    /// caller should surface that as a conflict rather than starting a second writer).
    /// </returns>
    /// <exception cref="BadHttpRequestException">The directory argument is invalid.</exception>
    public bool TryStart(string? targetDirectory, bool compact = false)
    {
        // Validate before touching any state so a bad path is still an immediate 400.
        var directory = InfiniDyskExporter.ValidateTargetDirectory(targetDirectory);

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_running is { IsCompleted: false }) return false;

            cts = new CancellationTokenSource();
            _cts = cts;
            _last = new InfiniDyskExportStatus(
                "running", compact ? "Starting compact export..." : "Starting export...", directory);
            _running = Task.Run(() => RunAsync(directory, compact, cts));
        }

        Publish(_last);
        return true;
    }

    /// <summary>
    /// Best available status: the in-memory record of this process if it has one (it carries
    /// row/blob counts that cannot be read back off disk), otherwise an inspection of the
    /// folder so the answer survives an application restart.
    /// </summary>
    public InfiniDyskExportStatus GetStatus(string? targetDirectory)
    {
        if (IsRunning) return Status;

        var last = Status;
        if (last.State is "completed" or "failed") return last;

        return Inspect(targetDirectory);
    }

    /// <summary>
    /// Inspects a directory on disk so status survives an application restart. A <c>db.sql</c>
    /// that exists is complete by construction: it is renamed into place atomically as the last
    /// data step, and the README is written immediately after.
    /// </summary>
    public InfiniDyskExportStatus Inspect(string? targetDirectory)
    {
        if (IsRunning) return Status;
        if (string.IsNullOrWhiteSpace(targetDirectory)) return Status;

        string directory;
        try
        {
            directory = InfiniDyskExporter.ValidateTargetDirectory(targetDirectory);
        }
        catch (Exception e) when (e is BadHttpRequestException or ArgumentException or IOException)
        {
            return Status;
        }

        var sqlPath = Path.Join(directory, InfiniDyskExporter.SqlFileName);
        var readmePath = Path.Join(directory, InfiniDyskExporter.ReadmeFileName);
        try
        {
            if (!File.Exists(sqlPath))
            {
                return new InfiniDyskExportStatus(
                    "idle",
                    File.Exists(readmePath) ? "A previous export is incomplete." : "No export found in this folder.",
                    directory);
            }

            var length = new FileInfo(sqlPath).Length;
            var finished = File.Exists(readmePath);
            return new InfiniDyskExportStatus(
                "completed",
                finished
                    ? "Found a completed export on disk."
                    : "Found db.sql, but the run did not finish writing its instructions file.",
                directory,
                File.GetLastWriteTimeUtc(finished ? readmePath : sqlPath),
                length,
                Inspectable: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Debug("[InfiniDyskExport] Could not inspect {Directory}: {Error}", directory, e.Message);
            return Status;
        }
    }

    private async Task RunAsync(string directory, bool compact, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            var result = await InfiniDyskExporter
                .ExportAsync(directory, compact, message => PublishRunning(directory, message), token)
                .ConfigureAwait(false);

            lock (_gate)
            {
                _last = new InfiniDyskExportStatus(
                    "completed",
                    $"Export complete — {result.SqlFilePath}",
                    directory,
                    DateTimeOffset.UtcNow,
                    result.Bytes,
                    result.TableRows,
                    result.NzbBlobCount,
                    result.FileBlobCount,
                    Warning: result.Warning);
            }

            Publish(_last);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                _last = new InfiniDyskExportStatus(
                    "failed", "Export was cancelled before it finished.", directory);
            }

            Publish(_last);
        }
        catch (Exception e)
        {
            Log.Error(e, "[InfiniDyskExport] Export to {Directory} failed.", directory);
            lock (_gate)
            {
                _last = new InfiniDyskExportStatus("failed", $"Export failed: {e.Message}", directory);
            }

            Publish(_last);
        }
        finally
        {
            lock (_gate) _cts = null;
            cts.Dispose();
        }
    }

    private void PublishRunning(string directory, string message)
    {
        var status = new InfiniDyskExportStatus("running", message, directory);
        // Only keep the in-memory snapshot for the run that is actually current.
        lock (_gate)
        {
            if (_last.State == "running") _last = status;
        }

        Publish(status);
    }

    private void Publish(InfiniDyskExportStatus status) =>
        // Fire-and-forget: a websocket problem must never fail an export.
        _ = websocketManager.SendMessage(
            WebsocketTopic.InfiniDyskExportProgress,
            JsonSerializer.Serialize(status, StatusJsonOptions));
}
