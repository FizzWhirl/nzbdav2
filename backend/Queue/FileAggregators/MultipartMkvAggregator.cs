using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Queue.FileAggregators;

public class MultipartMkvAggregator
(
    DavDatabaseClient dbClient,
    DavItem mountDirectory,
    bool checkedFullHealth
) : BaseAggregator
{
    protected override DavDatabaseClient DBClient => dbClient;
    protected override DavItem MountDirectory => mountDirectory;

    public override void UpdateDatabase(List<BaseProcessor.Result> processorResults)
    {
        var multipartMkvFiles = processorResults
            .OfType<MultipartMkvProcessor.Result>()
            .ToList();

        ProcessMultipartMkvFiles(multipartMkvFiles);
    }

    private void ProcessMultipartMkvFiles(List<MultipartMkvProcessor.Result> multipartMkvFiles)
    {
        foreach (var multipartMkvFile in multipartMkvFiles)
        {
            var fileParts = multipartMkvFile.Parts;
            var parentDirectory = MountDirectory;
            var name = multipartMkvFile.Filename;

            var itemId = GuidUtil.CreateDeterministic(parentDirectory.Id, name);
            if (IsAlreadyTracked(itemId))
            {
                Log.Warning("[MultipartMkvAggregator] Skipping duplicate DavItem for file '{FileName}' (ID {Id} already tracked)",
                    name, itemId);
                continue;
            }

            var davItem = DavItem.New(
                id: itemId,
                parent: parentDirectory,
                name: name,
                fileSize: fileParts.Sum(x => x.FilePartByteRange.Count),
                type: DavItem.ItemType.MultipartFile,
                releaseDate: multipartMkvFile.ReleaseDate,
                lastHealthCheck: checkedFullHealth ? DateTimeOffset.UtcNow : null,
                historyItemId: MountDirectory.HistoryItemId
            );

            var davMultipartFile = new DavMultipartFile()
            {
                Id = davItem.Id,
                Metadata = new DavMultipartFile.Meta()
                {
                    FileParts = fileParts.ToArray()
                }
            };

            dbClient.Ctx.Items.Add(davItem);
            dbClient.Ctx.MultipartFiles.Add(davMultipartFile);
        }
    }
}