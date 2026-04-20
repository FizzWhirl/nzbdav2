using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public enum MediaAnalysisResult
{
    Success,
    Failed,
    Timeout
}

public class MediaAnalysisService(
    IServiceScopeFactory scopeFactory
)
{
    public async Task<MediaAnalysisResult> AnalyzeMediaAsync(Guid davItemId, CancellationToken ct = default)
    {
        // 1. Get Item
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
        var item = await dbContext.Items.FindAsync([davItemId], ct).ConfigureAwait(false);
        if (item == null) return MediaAnalysisResult.Failed;

        // 2. Construct URL for ffprobe (still needed for metadata)
        var encodedPath = string.Join("/", item.Path.Split('/').Select(Uri.EscapeDataString));
        var webDavUrl = $"http://localhost:8080{encodedPath}";

        // 3. Run ffprobe with analysis mode header (retry once on failure for transient issues)
        Log.Information("[MediaAnalysis] Running ffprobe on {Name} ({Id}) via HTTP with analysis mode", item.Name, davItemId);
        var (result, timedOut) = await RunFfprobeAsync(webDavUrl, ct).ConfigureAwait(false);

        if (!timedOut && string.IsNullOrWhiteSpace(result))
        {
            Log.Warning("[MediaAnalysis] ffprobe failed for {Name} — retrying in 3s (transient provider issue?)", item.Name);
            await Task.Delay(3000, ct).ConfigureAwait(false);
            (result, timedOut) = await RunFfprobeAsync(webDavUrl, ct).ConfigureAwait(false);
        }

        // 4. Update DB
        MediaAnalysisResult analysisResult;
        if (timedOut)
        {
             Log.Warning("[MediaAnalysis] ffprobe timed out for {Name}", item.Name);
             analysisResult = MediaAnalysisResult.Timeout;
        }
        else if (string.IsNullOrWhiteSpace(result))
        {
             Log.Warning("[MediaAnalysis] ffprobe failed or returned empty result for {Name}", item.Name);
             item.MediaInfo = "{\"error\": \"ffprobe failed (file may be corrupt or incomplete)\", \"streams\": []}";
             item.IsCorrupted = true;
             item.CorruptionReason = "Media analysis (ffprobe) failed - possible corrupt file.";
             analysisResult = MediaAnalysisResult.Failed;
        }
        else if (result.Contains("\"error\":"))
        {
             Log.Warning("[MediaAnalysis] ffprobe reported error for {Name}: {Result}", item.Name, result);
             item.MediaInfo = result;
             item.IsCorrupted = true;
             item.CorruptionReason = "Media analysis reported stream errors.";
             analysisResult = MediaAnalysisResult.Failed;
        }
        else
        {
             item.MediaInfo = result;

             // Metadata probe passed — now run decode checks at 75% and 90% to verify integrity
             var integrityErrors = await RunDecodeCheckAsync(webDavUrl, result, item.Name, ct).ConfigureAwait(false);

             // Retry decode check once on failure — transient provider issues can cause false positives
             if (integrityErrors != null)
             {
                 Log.Warning("[MediaAnalysis] Decode check failed for {Name}: {Errors} — retrying in 3s", item.Name, integrityErrors);
                 await Task.Delay(3000, ct).ConfigureAwait(false);
                 integrityErrors = await RunDecodeCheckAsync(webDavUrl, result, item.Name, ct).ConfigureAwait(false);
             }

             if (integrityErrors != null)
             {
                 Log.Warning("[MediaAnalysis] Decode check failed on retry for {Name}: {Errors}", item.Name, integrityErrors);
                 item.IsCorrupted = true;
                 item.CorruptionReason = $"Decode integrity check failed: {integrityErrors}";
                 analysisResult = MediaAnalysisResult.Failed;
             }
             else
             {
                 item.IsCorrupted = false;
                 item.CorruptionReason = null;
                 analysisResult = MediaAnalysisResult.Success;
             }
        }

        if (analysisResult != MediaAnalysisResult.Timeout)
        {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        Log.Information("[MediaAnalysis] Media analysis complete for {Name}. Result: {Result}", item.Name, analysisResult);
        return analysisResult;
    }

    /// <summary>
    /// Runs ffprobe on the given WebDAV URL with X-Analysis-Mode header to limit resource usage.
    /// </summary>
    private async Task<(string? output, bool timedOut)> RunFfprobeAsync(string url, CancellationToken ct)
    {
        try
        {
            var headers = BuildAnalysisHeaders();
            var start = Stopwatch.StartNew();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ResolveExecutablePath("FFPROBE_PATH", "ffprobe"),
                    Arguments = $"-headers \"{headers}\" -v error -print_format json -show_format -show_streams -probesize 5000000 -analyzeduration 5000000 \"{url}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            // Read output
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);

            // Wait for exit with timeout (90 seconds)
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(90));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log.Warning("[MediaAnalysis] ffprobe timed out after 90 seconds for {Url}", url);
                try { process.Kill(); } catch { /* already exited */ }
                return (null, timedOut: true);
            }

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            start.Stop();
            Log.Debug("[MediaAnalysis] ffprobe took {Duration}ms", start.ElapsedMilliseconds);

            if (process.ExitCode != 0)
            {
                Log.Warning("[MediaAnalysis] ffprobe exited with code {Code}: {Error}", process.ExitCode, error);
                return (null, timedOut: false);
            }

            // ffprobe can exit 0 while printing real errors to stderr (e.g. 401 Unauthorized, corrupt streams)
            if (!string.IsNullOrWhiteSpace(error))
            {
                Log.Warning("[MediaAnalysis] ffprobe exited 0 but had stderr: {Error}", error.Trim());
                // Still return the output if we got valid JSON — stderr warnings don't always mean failure
                // But if output is empty/invalid, treat as failure
                if (string.IsNullOrWhiteSpace(output) || !output.TrimStart().StartsWith("{"))
                {
                    return (null, timedOut: false);
                }
            }

            return (output, timedOut: false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MediaAnalysis] Failed to run ffprobe");
            return (null, timedOut: false);
        }
    }

    /// <summary>
    /// Decodes 5s at 75% and 90% of the file to verify data integrity.
    /// Two check points near the end catch truncated files where data is only partially available.
    /// </summary>
    private async Task<string?> RunDecodeCheckAsync(string url, string ffprobeJson, string fileName, CancellationToken ct)
    {
        double duration;
        try
        {
            using var doc = JsonDocument.Parse(ffprobeJson);
            var durationStr = doc.RootElement
                .GetProperty("format")
                .GetProperty("duration")
                .GetString();
            if (!double.TryParse(durationStr, System.Globalization.CultureInfo.InvariantCulture, out duration) || duration < 30)
            {
                Log.Debug("[MediaAnalysis] Skipping decode check — duration too short or unknown ({Duration}s)", durationStr);
                return null;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[MediaAnalysis] Could not parse duration from ffprobe output — skipping decode check");
            return null;
        }

        // Check at 75% and 90% — catches files truncated at different points
        double[] checkPoints = [0.75, 0.90];
        foreach (var pct in checkPoints)
        {
            var seekPosition = (duration * pct).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            var label = $"{(int)(pct * 100)}%";
            Log.Information("[MediaAnalysis] Decode check ({Label}): -ss {SeekPos} -t 5 for {Name}", label, seekPosition, fileName);

            var result = await RunSingleDecodeAsync(url, seekPosition, label, fileName, ct).ConfigureAwait(false);
            if (result != null)
                return result;
        }

        return null;
    }

    private async Task<string?> RunSingleDecodeAsync(string url, string seekPosition, string label, string fileName, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var headers = BuildAnalysisHeaders();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ResolveExecutablePath("FFMPEG_PATH", "ffmpeg"),
                    Arguments = $"-headers \"{headers}\" -ss {seekPosition} -i \"{url}\" -t 5 -v error -f null -",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            var errorTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(90));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(); } catch { /* already exited */ }
                sw.Stop();
                Log.Warning("[MediaAnalysis] Decode check ({Label}) timed out (90s, {Elapsed}ms) for {Name}", label, sw.ElapsedMilliseconds, fileName);
                return $"[timeout] decode timed out at {label} (90s)";
            }

            var error = await errorTask.ConfigureAwait(false);
            sw.Stop();

            // Check both exit code AND stderr — ffmpeg can exit 0 while reporting real errors
            // (e.g., "Stream ends prematurely", "partial file") for truncated files
            if (process.ExitCode == 0 && string.IsNullOrWhiteSpace(error))
            {
                Log.Information("[MediaAnalysis] Decode check ({Label}) passed ({Elapsed}ms) for {Name}", label, sw.ElapsedMilliseconds, fileName);
                return null;
            }

            var errorDetail = string.IsNullOrWhiteSpace(error) ? $"ffmpeg exit code {process.ExitCode}" : error.Trim();
            if (errorDetail.Length > 200) errorDetail = errorDetail[..200];
            Log.Warning("[MediaAnalysis] Decode check ({Label}) failed for {Name}: {Error}", label, fileName, errorDetail);
            return errorDetail;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return $"[error] {ex.Message}";
        }
    }

    private static string BuildAnalysisHeaders()
    {
        var token = EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY");
        return $"X-Analysis-Mode: true\r\nX-Internal-Analysis-Auth: {token}\r\n";
    }

    private static string ResolveExecutablePath(string envName, string defaultCommand)
    {
        var configured = Environment.GetEnvironmentVariable(envName);
        return string.IsNullOrWhiteSpace(configured) ? defaultCommand : configured;
    }
}
