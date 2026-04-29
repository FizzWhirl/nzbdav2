using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Queue;

namespace NzbWebDAV.Api.SabControllers.GetQueue;

public class GetQueueController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    private async Task<GetQueueResponse> GetQueueAsync(GetQueueRequest request)
    {
        // get in progress item
        var (inProgressQueueItem, progressPercentage) = queueManager.GetInProgressQueueItem();

        // get total count
        var ct = request.CancellationToken;
        var totalCount = await dbClient.GetQueueItemsCount(request.Category, request.Search, ct).ConfigureAwait(false);

        // The in-progress item is pinned to the top of page 1 only. On subsequent
        // pages we return items in their natural order and do not re-inject the
        // in-progress row, otherwise it would appear at the top of every page.
        var isFirstPage = request.Start <= 0;

        // get queued items
        var queueItemsQuery = await dbClient.GetQueueItems(request.Category, request.Start, request.Limit, request.Search, ct).ConfigureAwait(false);
        var queueItems = isFirstPage
            ? queueItemsQuery.Where(x => x.Id != inProgressQueueItem?.Id).ToArray()
            : queueItemsQuery.ToArray();

        // get slots
        var orderedItems = isFirstPage && inProgressQueueItem != null
            ? queueItems.Prepend(inProgressQueueItem)
            : queueItems.AsEnumerable();

        var slots = orderedItems
            .Select((queueItem, index) =>
            {
                var percentage = (queueItem == inProgressQueueItem ? progressPercentage : 0)!.Value;
                var status = queueItem == inProgressQueueItem ? "Downloading" : "Queued";
                return GetQueueResponse.QueueSlot.FromQueueItem(queueItem, index, percentage, status);
            })
            .ToList();

        // return response
        return new GetQueueResponse()
        {
            Queue = new GetQueueResponse.QueueObject()
            {
                Paused = false,
                Slots = slots,
                TotalCount = totalCount,
            }
        };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = new GetQueueRequest(httpContext);
        return Ok(await GetQueueAsync(request).ConfigureAwait(false));
    }
}