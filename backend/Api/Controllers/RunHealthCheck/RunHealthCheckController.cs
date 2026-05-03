using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Api.Controllers;

namespace NzbWebDAV.Api.Controllers.RunHealthCheck;

[ApiController]
[Route("api/health/check/{id}")]
public class RunHealthCheckController(DavDatabaseClient dbClient) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (!Guid.TryParse((string?)RouteData.Values["id"], out var id))
        {
            return BadRequest("Invalid ID format");
        }

        var item = await dbClient.Ctx.Items.FirstOrDefaultAsync(x => x.Id == id).ConfigureAwait(false);
        if (item == null)
        {
            return NotFound("Item not found");
        }

        var requestedHead = string.Equals(Request.Query["head"].ToString(), "true", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(Request.Query["mode"].ToString(), "head", StringComparison.OrdinalIgnoreCase);

        // Explicit HEAD checks are deep/slow and use MinValue priority. Default manual checks
        // should be quick STAT checks so the play action does not accidentally start a long scan.
        item.NextHealthCheck = requestedHead
            ? DateTimeOffset.MinValue
            : DateTimeOffset.UtcNow.AddSeconds(-1);
        await dbClient.Ctx.SaveChangesAsync().ConfigureAwait(false);

        return Ok(new
        {
            Message = requestedHead ? "HEAD health check scheduled successfully" : "Quick health check scheduled successfully",
            Operation = requestedHead ? "HEAD" : "STAT"
        });
    }
}
