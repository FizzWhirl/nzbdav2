using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.ResetHealthStatus;

[ApiController]
[Route("api/health/reset")]
public class ResetHealthStatusController(DavDatabaseClient dbClient, ProviderErrorService providerErrorService) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = await JsonSerializer.DeserializeAsync<ResetHealthStatusRequest>(HttpContext.Request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }).ConfigureAwait(false);
        if (request == null || request.DavItemIds.Count == 0)
        {
            return BadRequest(new BaseApiResponse { Status = false, Error = "No IDs provided" });
        }

        var guids = request.DavItemIds.Select(Guid.Parse).ToList();
        const int batchSize = 500;

        // 1. Get paths for ProviderErrorService cleanup
        var itemPaths = new List<string>();
        foreach (var batch in guids.ToBatches(batchSize))
        {
            var paths = await dbClient.Ctx.Items
                .Where(x => batch.Contains(x.Id))
                .Select(x => x.Path)
                .ToListAsync().ConfigureAwait(false);
            itemPaths.AddRange(paths);
        }

        // 2. Reset DavItems status
        // We set NextHealthCheck to null to trigger a fresh check ASAP
        var resetCount = 0;
        foreach (var batch in guids.ToBatches(batchSize))
        {
            resetCount += await dbClient.Ctx.Items
                .Where(x => batch.Contains(x.Id))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.IsCorrupted, false)
                    .SetProperty(p => p.CorruptionReason, (string?)null)
                    .SetProperty(p => p.LastHealthCheck, (DateTimeOffset?)null)
                    .SetProperty(p => p.NextHealthCheck, (DateTimeOffset?)null))
                .ConfigureAwait(false);
        }

        // 3. Clear HealthCheckResults
        foreach (var batch in guids.ToBatches(batchSize))
        {
            await dbClient.Ctx.HealthCheckResults
                .Where(x => batch.Contains(x.DavItemId))
                .ExecuteDeleteAsync().ConfigureAwait(false);
        }

        // 4. Clear ProviderErrorService summaries/events
        foreach (var path in itemPaths)
        {
            await providerErrorService.ClearErrorsForFile(path).ConfigureAwait(false);
        }

        return Ok(new ResetHealthStatusResponse { Status = true, ResetCount = resetCount });
    }
}
