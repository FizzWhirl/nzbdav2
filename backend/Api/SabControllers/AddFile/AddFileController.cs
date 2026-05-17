using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.SabControllers.GetQueue;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue;
using NzbWebDAV.Websocket;
using Usenet.Nzb;

namespace NzbWebDAV.Api.SabControllers.AddFile;

public class AddFileController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager,
    WebsocketManager websocketManager
) : SabApiController.BaseController(httpContext, configManager)
{
    private const long DefaultMaxNzbFiles = 5000;
    private const long DefaultMaxNzbSegmentsPerFile = 250000;
    private const long DefaultMaxNzbTotalSegments = 1000000;
    private const long DefaultMaxNzbPathLength = 1024;
    private const long DefaultMaxNzbMessageIdLength = 512;

    public async Task<AddFileResponse> AddFileAsync(AddFileRequest request)
    {
        // load the document
        var nzbFileContents = NormalizeNzbContents(request.NzbFileContents);
        var documentBytes = Encoding.UTF8.GetBytes(nzbFileContents);
        using var memoryStream = new MemoryStream(documentBytes);
        var document = await NzbDocument.LoadAsync(memoryStream).ConfigureAwait(false);
        ValidateNzbDocumentShape(document);

        // add the queueItem to the database
        var queueItem = new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.Now,
            FileName = request.FileName,
            JobName = FilenameUtil.GetJobName(request.FileName),
            NzbFileSize = documentBytes.Length,
            TotalSegmentBytes = document.Files.SelectMany(x => x.Segments).Select(x => x.Size).Sum(),
            Category = request.Category,
            Priority = request.Priority,
            PostProcessing = request.PostProcessing,
            PauseUntil = request.PauseUntil
        };
        var queueNzbContents = new QueueNzbContents()
        {
            Id = queueItem.Id,
            NzbContents = nzbFileContents,
        };
        dbClient.Ctx.QueueItems.Add(queueItem);
        dbClient.Ctx.QueueNzbContents.Add(queueNzbContents);
        await dbClient.Ctx.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);
        var message = GetQueueResponse.QueueSlot.FromQueueItem(queueItem).ToJson();
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemAdded, message);

        // awaken the queue if it is sleeping
        queueManager.AwakenQueue();

        // return response
        return new AddFileResponse()
        {
            Status = true,
            NzoIds = [queueItem.Id.ToString()],
        };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await AddFileRequest.New(httpContext, configManager).ConfigureAwait(false);
        return Ok(await AddFileAsync(request).ConfigureAwait(false));
    }

    private static string NormalizeNzbContents(string nzbContents)
    {
        return nzbContents
            .Replace("https://www.newzbin.com/DTD/2003/nzb", "http://www.newzbin.com/DTD/2003/nzb")
            .Replace("date=\"\"", "date=\"0\"");
    }

    private static void ValidateNzbDocumentShape(NzbDocument document)
    {
        var maxFiles = GetNzbShapeLimit("MAX_NZB_FILES", DefaultMaxNzbFiles);
        var maxSegmentsPerFile = GetNzbShapeLimit("MAX_NZB_SEGMENTS_PER_FILE", DefaultMaxNzbSegmentsPerFile);
        var maxTotalSegments = GetNzbShapeLimit("MAX_NZB_TOTAL_SEGMENTS", DefaultMaxNzbTotalSegments);
        var maxPathLength = GetNzbShapeLimit("MAX_NZB_PATH_LENGTH", DefaultMaxNzbPathLength);
        var maxMessageIdLength = GetNzbShapeLimit("MAX_NZB_MESSAGE_ID_LENGTH", DefaultMaxNzbMessageIdLength);

        if (document.Files.Count == 0) throw new BadHttpRequestException("NZB contains no files");
        if (document.Files.Count > maxFiles) throw new BadHttpRequestException($"NZB contains too many files ({document.Files.Count} > {maxFiles})");

        long totalSegments = 0;
        foreach (var file in document.Files)
        {
            if (file.FileName.Length > maxPathLength)
                throw new BadHttpRequestException($"NZB file path exceeds maximum length ({file.FileName.Length} > {maxPathLength})");

            if (file.Segments.Count > maxSegmentsPerFile)
                throw new BadHttpRequestException($"NZB file contains too many segments ({file.Segments.Count} > {maxSegmentsPerFile})");

            foreach (var segment in file.Segments)
            {
                var messageId = segment.MessageId?.Value ?? string.Empty;
                if (messageId.Length > maxMessageIdLength)
                    throw new BadHttpRequestException($"NZB segment message ID exceeds maximum length ({messageId.Length} > {maxMessageIdLength})");
            }

            totalSegments += file.Segments.Count;
            if (totalSegments > maxTotalSegments)
                throw new BadHttpRequestException($"NZB contains too many total segments ({totalSegments} > {maxTotalSegments})");
        }
    }

    private static long GetNzbShapeLimit(string name, long defaultValue)
    {
        var value = EnvironmentUtil.GetLongVariable(name);
        return value is > 0 ? value.Value : defaultValue;
    }
}