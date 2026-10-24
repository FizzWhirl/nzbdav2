using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.ExportInfiniDysk;

/// <summary>
/// Starts an InfiniDysk migration export and reports its progress.
///
/// POST is deliberately *not* the thing that performs the export. An export streams the whole
/// database and can run for minutes, so doing the work inline would tie the job to the browser
/// connection: closing or reloading the tab aborts the request, and the work would be thrown
/// away. Instead POST validates the path, hands the job to <see cref="InfiniDyskExportRunner"/>,
/// and returns straight away. Progress then arrives over the
/// <see cref="NzbWebDAV.Websocket.WebsocketTopic.InfiniDyskExportProgress"/> websocket topic,
/// which caches and replays its last message, so a reloaded page still shows the outcome.
/// </summary>
[ApiController]
[Route("api/export-infini-dysk")]
public class ExportInfiniDyskController(InfiniDyskExportRunner runner) : BaseApiController
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task<IActionResult> HandleRequest()
    {
        if (HttpMethods.IsGet(Request.Method))
        {
            // `path` lets the UI recover status after an application restart, when the runner
            // has no in-memory record of the previous run.
            var status = runner.GetStatus(Request.Query["path"].FirstOrDefault());
            return Ok(ToResponse(status));
        }

        var request = await ReadRequestAsync().ConfigureAwait(false);

        if (!runner.TryStart(request?.Path))
        {
            return Conflict(new ExportInfiniDyskResponse
            {
                Status = false,
                Error = "An export is already running. Wait for it to finish before starting another.",
                State = "running",
                Message = runner.Status.Message,
                OutputDirectory = runner.Status.OutputDirectory,
            });
        }

        return Accepted(ToResponse(runner.Status));
    }

    private static ExportInfiniDyskResponse ToResponse(InfiniDyskExportStatus status) => new()
    {
        State = status.State,
        Message = status.Message,
        OutputDirectory = status.OutputDirectory,
        CompletedAtUtc = status.CompletedAtUtc,
        Bytes = status.Bytes,
        TableRows = status.TableRows?.ToDictionary(x => x.Key, x => x.Value),
        NzbBlobCount = status.NzbBlobCount,
        FileBlobCount = status.FileBlobCount,
        Inspectable = status.Inspectable,
    };

    private async Task<ExportInfiniDyskRequest?> ReadRequestAsync()
    {
        // The action is reachable through both verbs, so treat an empty body as "no path"
        // and let the exporter return a clear 400 instead of surfacing a JSON parser error.
        if (Request.ContentLength is null or 0 && !Request.Headers.ContainsKey("Transfer-Encoding"))
            return null;

        try
        {
            return await JsonSerializer
                .DeserializeAsync<ExportInfiniDyskRequest>(Request.Body, JsonOptions, HttpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new BadHttpRequestException(
                "Request body must be JSON with a `path` property, e.g. {\"path\":\"/config/infinidysk-export\"}.");
        }
    }
}

public class ExportInfiniDyskRequest
{
    /// <summary>Absolute directory (inside the container) that receives <c>db.sql</c>.</summary>
    public string? Path { get; init; }
}

public class ExportInfiniDyskResponse : BaseApiResponse
{
    /// <summary>One of <c>idle</c>, <c>running</c>, <c>completed</c>, <c>failed</c>.</summary>
    public string State { get; init; } = "idle";
    public string Message { get; init; } = string.Empty;
    public string? OutputDirectory { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public long? Bytes { get; init; }
    public Dictionary<string, int>? TableRows { get; init; }
    public int? NzbBlobCount { get; init; }
    public int? FileBlobCount { get; init; }
    public bool Inspectable { get; init; }
}
