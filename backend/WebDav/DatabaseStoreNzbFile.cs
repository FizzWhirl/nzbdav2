using Microsoft.AspNetCore.Http;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav.Base;

using NzbWebDAV.Services;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace NzbWebDAV.WebDav;

public class DatabaseStoreNzbFile(
    DavItem davNzbFile,
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    UsenetStreamingClient usenetClient,
    ConfigManager configManager,
    NzbAnalysisService nzbAnalysisService
) : BaseStoreStreamFile
{
    public DavItem DavItem => davNzbFile;
    public override string Name => davNzbFile.Name;
    public override string UniqueKey => davNzbFile.Id.ToString();
    public override long FileSize => davNzbFile.FileSize!.Value;
    public override DateTime CreatedAt => davNzbFile.CreatedAt;

    public override async Task<Stream> GetStreamAsync(CancellationToken cancellationToken)
    {
        // store the DavItem being accessed in the http context
        httpContext.Items["DavItem"] = davNzbFile;

        // create streaming usage context with normalized AffinityKey
        var rawAffinityKey = Path.GetFileName(Path.GetDirectoryName(davNzbFile.Path));
        var normalizedAffinityKey = FilenameNormalizer.NormalizeName(rawAffinityKey);

        Serilog.Log.Debug("[DatabaseStoreNzbFile] AffinityKey: Raw='{Raw}' Normalized='{Normalized}' for file '{File}'",
            rawAffinityKey, normalizedAffinityKey, davNzbFile.Name);

        var usageContext = new ConnectionUsageContext(
            ConnectionUsageType.Streaming,
            new ConnectionUsageDetails
            {
                Text = davNzbFile.Path,
                JobName = davNzbFile.Name,
                AffinityKey = normalizedAffinityKey,
                DavItemId = davNzbFile.Id,
                FileDate = davNzbFile.ReleaseDate
            }
        );

        // return the stream with usage context and buffering options
        var id = davNzbFile.Id;
        var file = await dbClient.GetNzbFileAsync(id, cancellationToken).ConfigureAwait(false);
        if (file is null) throw new FileNotFoundException($"Could not find nzb file with id: {id}");

        // Trigger background analysis if cache is missing
        if (file.SegmentSizes == null)
        {
            nzbAnalysisService.TriggerAnalysisInBackground(file.Id, file.SegmentIds);
        }

        // Check if this is a lightweight analysis request (from ffprobe via MediaAnalysisService)
        var isAnalysisMode = httpContext.Request.Headers.ContainsKey("X-Analysis-Mode");
        var isPreviewMode = httpContext.Items.ContainsKey("PreviewMode");
        var isPreviewHlsMode = httpContext.Items.ContainsKey("PreviewHlsMode");
        var concurrentConnections = isAnalysisMode ? 4 : configManager.GetTotalStreamingConnections();
        var bufferSize = isAnalysisMode ? 4 : configManager.GetStreamBufferSize();
        var useBufferedStreaming = configManager.UseBufferedStreaming();

        // HLS preview benefits from buffered/shared streaming because adjacent segment fetches are
        // sequential and should reuse the warm article pipeline rather than reopening cold streams.
        if (isPreviewHlsMode && !isAnalysisMode)
        {
            concurrentConnections = Math.Clamp(Math.Min(configManager.GetTotalStreamingConnections(), 12), 6, 12);
            bufferSize = Math.Clamp(Math.Max(bufferSize, concurrentConnections * 6), 48, 96);
            useBufferedStreaming = true;

            // HLS segment requests should keep buffered article fetching, but they must not
            // attach to an older shared stream at a stale byte position when ffmpeg issues a
            // fresh HTTP seek for another segment.
            usageContext = new ConnectionUsageContext(
                ConnectionUsageType.Streaming,
                new ConnectionUsageDetails
                {
                    Text = davNzbFile.Path,
                    JobName = davNzbFile.Name,
                    AffinityKey = normalizedAffinityKey,
                    FileDate = davNzbFile.ReleaseDate
                }
            );
        }
        // Non-HLS preview seeks are latency-sensitive and can quickly saturate the global stream permit pool.
        // Use a small independent stream footprint and bypass buffered/shared streams so each
        // seek opens a fresh stream instead of attaching to an older shared pump position.
        else if (isPreviewMode && !isAnalysisMode)
        {
            concurrentConnections = 2;
            bufferSize = Math.Clamp(bufferSize / 2, 8, 20);
            useBufferedStreaming = false;
        }

        if (isAnalysisMode)
        {
            // Use queue analysis connections for media integrity checks during queue processing
            usageContext = new ConnectionUsageContext(
                ConnectionUsageType.QueueAnalysis,
                new ConnectionUsageDetails
                {
                    Text = davNzbFile.Path,
                    JobName = davNzbFile.Name,
                    DavItemId = davNzbFile.Id
                }
            );
            Serilog.Log.Debug("[DatabaseStoreNzbFile] Analysis mode: Opening lightweight stream for {FileName} ({Id}) (workers={Workers}, buffer={Buffer})",
                Name, id, concurrentConnections, bufferSize);
        }
        else if (isPreviewHlsMode)
        {
            Serilog.Log.Debug("[DatabaseStoreNzbFile] HLS preview mode: Opening buffered stream for {FileName} ({Id}) (workers={Workers}, buffer={Buffer}, buffered={Buffered})",
                Name, id, concurrentConnections, bufferSize, useBufferedStreaming);
        }
        else if (isPreviewMode)
        {
            Serilog.Log.Debug("[DatabaseStoreNzbFile] Preview mode: Opening independent stream for {FileName} ({Id}) (workers={Workers}, buffer={Buffer}, buffered={Buffered})",
                Name, id, concurrentConnections, bufferSize, useBufferedStreaming);
        }
        else
        {
            var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var userAgent = httpContext.Request.Headers.UserAgent.ToString();
            var rangeHeader = httpContext.Request.Headers.Range.ToString();
            Serilog.Log.Information("[DatabaseStoreNzbFile] Opening stream for {FileName} ({Id}) — Client: {ClientIp}, UA: {UserAgent}, Range: {Range}",
                Name, id, clientIp, userAgent, rangeHeader);
        }

        // Honor the consumer's HTTP Range end byte (set by GetAndHeadHandlerPatch when the
        // request specifies bytes=X-Y). The stream prefetch will be bounded to that segment
        // (plus a small overshoot), preventing ~40 MB over-fetch per ranged read for clients
        // like rclone vfs-cache and ffprobe seeks.
        long? requestedEndByte = null;
        if (!isAnalysisMode && !isPreviewMode && !isPreviewHlsMode &&
            httpContext.Items.TryGetValue("RequestedRangeEnd", out var endObj) &&
            endObj is long endByte)
        {
            requestedEndByte = endByte;
        }

        // Non-HLS preview seeks use zero grace period so SharedStreamManager immediately releases
        // permits when the browser jumps to a different position. HLS preview keeps the normal
        // grace period so the next segment can reuse the warmed shared stream.
        var stream = usenetClient.GetFileStream(
            file.SegmentIds,
            FileSize,
            concurrentConnections,
            usageContext,
            useBufferedStreaming,
            bufferSize,
            file.GetSegmentSizes(),
            file.SegmentFallbacks,
            sharedStreamGracePeriod: isPreviewHlsMode ? null : isPreviewMode ? 0 : null,
            requestedEndByte: requestedEndByte
        );

        return stream;
    }
}
