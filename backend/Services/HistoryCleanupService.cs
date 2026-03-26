using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Services;

public class HistoryCleanupService(IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

                var cleanupItem = await dbContext.HistoryCleanupItems
                    .FirstOrDefaultAsync(stoppingToken)
                    .ConfigureAwait(false);

                if (cleanupItem == null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Collect paths before bulk operations for VFS cache invalidation.
                var affectedPaths = await dbContext.Items
                    .Where(x => x.HistoryItemId == cleanupItem.Id)
                    .Select(x => x.Path)
                    .ToListAsync(stoppingToken)
                    .ConfigureAwait(false);

                if (cleanupItem.DeleteMountedFiles)
                {
                    // Also collect paths from the mount folder tree for VFS cache invalidation
                    if (cleanupItem.DownloadDirId.HasValue)
                    {
                        var descendantPaths = await dbContext.Database
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
                                cleanupItem.DownloadDirId.Value.ToString().ToUpper())
                            .ToListAsync(stoppingToken)
                            .ConfigureAwait(false);

                        affectedPaths = affectedPaths.Union(descendantPaths).ToList();
                    }

                    // 1. Delete DavItems still linked by HistoryItemId (existing behavior)
                    var deletedByHistoryId = await dbContext.Items
                        .Where(x => x.HistoryItemId == cleanupItem.Id)
                        .ExecuteDeleteAsync(stoppingToken)
                        .ConfigureAwait(false);

                    Log.Information("[HistoryCleanup] Deleted {Count} DavItems by HistoryItemId for {Id}",
                        deletedByHistoryId, cleanupItem.Id);

                    // 2. Delete mount directory and all descendants via recursive CTE
                    //    This catches DavItems that were previously unlinked (HistoryItemId set to null)
                    if (cleanupItem.DownloadDirId.HasValue)
                    {
                        var deletedByTree = await dbContext.Database
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
                                [cleanupItem.DownloadDirId.Value.ToString().ToUpper()],
                                stoppingToken)
                            .ConfigureAwait(false);

                        Log.Information("[HistoryCleanup] Deleted {Count} DavItems by mount folder tree for {Id} (DownloadDirId={DirId})",
                            deletedByTree, cleanupItem.Id, cleanupItem.DownloadDirId.Value);
                    }
                }
                else
                {
                    var updated = await dbContext.Items
                        .Where(x => x.HistoryItemId == cleanupItem.Id)
                        .ExecuteUpdateAsync(
                            x => x.SetProperty(p => p.HistoryItemId, (Guid?)null),
                            stoppingToken
                        ).ConfigureAwait(false);

                    Log.Debug("[HistoryCleanup] Unlinked {Count} DavItems from history item {Id}",
                        updated, cleanupItem.Id);
                }

                // Trigger vfs/forget for affected directories
                var dirsToForget = affectedPaths
                    .Select(p => Path.GetDirectoryName(p)?.Replace('\\', '/'))
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Distinct()
                    .ToArray();
                DavDatabaseContext.TriggerVfsForget(dirsToForget!);

                dbContext.HistoryCleanupItems.Remove(cleanupItem);
                await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Log.Error(e, "[HistoryCleanup] Error processing cleanup queue: {Message}", e.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
