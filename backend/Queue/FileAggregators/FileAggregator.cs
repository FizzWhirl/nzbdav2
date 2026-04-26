using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Queue.FileAggregators;

public class FileAggregator(DavDatabaseClient dbClient, DavItem mountDirectory, bool checkedFullHealth) : BaseAggregator
{
    protected override DavDatabaseClient DBClient => dbClient;
    protected override DavItem MountDirectory => mountDirectory;

    public override void UpdateDatabase(List<BaseProcessor.Result> processorResults)
    {
        foreach (var processorResult in processorResults)
        {
            if (processorResult is not FileProcessor.Result result) continue;
            if (result.FileName == "") continue; // skip files whose name we can't determine

            var itemId = GuidUtil.CreateDeterministic(mountDirectory.Id, result.FileName);
            if (IsAlreadyTracked(itemId))
            {
                Log.Warning("[FileAggregator] Skipping duplicate DavItem for file '{FileName}' (ID {Id} already tracked)",
                    result.FileName, itemId);
                continue;
            }

            var davItem = DavItem.New(
                id: itemId,
                parent: mountDirectory,
                name: result.FileName,
                fileSize: result.FileSize,
                type: DavItem.ItemType.NzbFile,
                releaseDate: result.ReleaseDate,
                lastHealthCheck: checkedFullHealth ? DateTimeOffset.UtcNow : null,
                historyItemId: mountDirectory.HistoryItemId
            );

            var (primaryIds, fallbacks) = result.NzbFile.GetSegmentIdsWithFallbacks();
            var davNzbFile = new DavNzbFile()
            {
                Id = davItem.Id,
                SegmentIds = primaryIds,
                SegmentFallbacks = fallbacks,
            };

            if (result.SegmentSizes is { Length: > 0 } segmentSizes && segmentSizes.Length == primaryIds.Length)
            {
                davNzbFile.SetSegmentSizes(segmentSizes);
            }

            dbClient.Ctx.Items.Add(davItem);
            dbClient.Ctx.NzbFiles.Add(davNzbFile);
        }
    }
}