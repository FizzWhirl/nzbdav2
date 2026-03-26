using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Api.Controllers.DavItems;

[ApiController]
[Route("api/dav-items/{id}")]
public class DeleteDavItemController(DavDatabaseContext dbContext) : BaseApiController
{
    [HttpDelete]
    public new async Task<IActionResult> HandleApiRequest()
    {
        return await base.HandleApiRequest().ConfigureAwait(false);
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        if (!Guid.TryParse((string?)RouteData.Values["id"], out var id))
            return BadRequest(new BaseApiResponse { Status = false, Error = "Invalid ID format" });

        var item = await dbContext.Items
            .FirstOrDefaultAsync(x => x.Id == id, HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (item == null)
            return NotFound(new BaseApiResponse { Status = false, Error = "Item not found" });

        if (item.IsProtected())
            return StatusCode(403, new BaseApiResponse { Status = false, Error = "Cannot delete protected system directory" });

        // Collect paths before deletion for VFS cache invalidation
        List<string> affectedPaths;

        if (item.Type == DavItem.ItemType.Directory)
        {
            // Collect all descendant paths via recursive CTE
            affectedPaths = await dbContext.Database
                .SqlQueryRaw<string>(
                    """
                    WITH RECURSIVE descendants AS (
                        SELECT Id, Path FROM DavItems WHERE Id = {0}
                        UNION ALL
                        SELECT d.Id, d.Path FROM DavItems d
                        INNER JOIN descendants a ON d.ParentId = a.Id
                    )
                    SELECT Path AS Value FROM descendants WHERE Path IS NOT NULL
                    """,
                    id.ToString().ToUpper())
                .ToListAsync(HttpContext.RequestAborted)
                .ConfigureAwait(false);

            // Delete directory and all descendants via recursive CTE
            var deleted = await dbContext.Database
                .ExecuteSqlRawAsync(
                    """
                    WITH RECURSIVE descendants AS (
                        SELECT Id FROM DavItems WHERE Id = {0}
                        UNION ALL
                        SELECT d.Id FROM DavItems d
                        INNER JOIN descendants a ON d.ParentId = a.Id
                    )
                    DELETE FROM DavItems WHERE Id IN (SELECT Id FROM descendants)
                    """,
                    [id.ToString().ToUpper()],
                    HttpContext.RequestAborted)
                .ConfigureAwait(false);

            Log.Information("[DavExplore] Deleted directory {Name} and {Count} items (Id={Id})",
                item.Name, deleted, id);
        }
        else
        {
            affectedPaths = item.Path != null ? [item.Path] : [];
            dbContext.Items.Remove(item);
            await dbContext.SaveChangesAsync(HttpContext.RequestAborted).ConfigureAwait(false);

            Log.Information("[DavExplore] Deleted file {Name} (Id={Id})", item.Name, id);
        }

        // Trigger VFS cache invalidation
        var dirsToForget = affectedPaths
            .Select(p => Path.GetDirectoryName(p)?.Replace('\\', '/'))
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct()
            .ToArray();
        DavDatabaseContext.TriggerVfsForget(dirsToForget!);

        return Ok(new BaseApiResponse { Status = true });
    }
}
