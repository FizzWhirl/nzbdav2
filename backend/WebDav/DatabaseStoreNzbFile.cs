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
        var concurrentConnections = isAnalysisMode ? 4 : configManager.GetTotalStreamingConnections();
        var bufferSize = isAnalysisMode ? 4 : configManager.GetStreamBufferSize();

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
        else if (isPreviewMode)
        {
            Serilog.Log.Debug("[DatabaseStoreNzbFile] Preview mode: Opening stream with zero grace period for {FileName} ({Id})",
                Name, id);
        }
        else
        {
            var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var userAgent = httpContext.Request.Headers.UserAgent.ToString();
            var rangeHeader = httpContext.Request.Headers.Range.ToString();
            Serilog.Log.Information("[DatabaseStoreNzbFile] Opening stream for {FileName} ({Id}) — Client: {ClientIp}, UA: {UserAgent}, Range: {Range}",
                Name, id, clientIp, userAgent, rangeHeader);
        }

        // Preview mode: use zero grace period so SharedStreamManager immediately releases
        // permits when the browser seeks (aborts old request), instead of holding them for 10s
        return usenetClient.GetFileStream(
            file.SegmentIds,
            FileSize,
            concurrentConnections,
            usageContext,
            configManager.UseBufferedStreaming(),
            bufferSize,
            file.GetSegmentSizes(),
            file.SegmentFallbacks,
            sharedStreamGracePeriod: isPreviewMode ? 0 : null
        );
    }
}
