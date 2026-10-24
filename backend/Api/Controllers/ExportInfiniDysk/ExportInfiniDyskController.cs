using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Database.Export;

namespace NzbWebDAV.Api.Controllers.ExportInfiniDysk;

[ApiController]
[Route("api/export-infini-dysk")]
public class ExportInfiniDyskController : BaseApiController
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = await ReadRequestAsync().ConfigureAwait(false);

        var result = await InfiniDyskExporter
            .ExportAsync(request?.Path ?? string.Empty, HttpContext.RequestAborted)
            .ConfigureAwait(false);

        return Ok(new ExportInfiniDyskResponse
        {
            OutputDirectory = result.OutputDirectory,
            SqlFilePath = result.SqlFilePath,
            Bytes = result.Bytes,
            TableRows = result.TableRows.ToDictionary(x => x.Key, x => x.Value),
            NzbBlobCount = result.NzbBlobCount,
            FileBlobCount = result.FileBlobCount,
        });
    }

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
    public string OutputDirectory { get; init; } = string.Empty;
    public string SqlFilePath { get; init; } = string.Empty;
    public long Bytes { get; init; }
    public Dictionary<string, int> TableRows { get; init; } = [];
    public int NzbBlobCount { get; init; }
    public int FileBlobCount { get; init; }
}
