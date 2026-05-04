using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

public class AnalysisInfo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string JobName { get; set; } = string.Empty;
    public int Progress { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class NzbAnalysisService(
    IServiceScopeFactory scopeFactory,
    UsenetStreamingClient usenetClient,
    WebsocketManager websocketManager,
    ConfigManager configManager,
    MediaAnalysisService mediaAnalysisService,
    BackgroundTaskQueue backgroundTaskQueue
)
{
    private static readonly ConcurrentDictionary<Guid, AnalysisInfo> _activeAnalyses = new();
    private static readonly ConcurrentDictionary<Guid, byte> _queuedAnalyses = new();
    private static readonly ConcurrentDictionary<Guid, int> _ffprobeRetryAttempts = new();
    private static readonly ConcurrentDictionary<Guid, byte> _suppressedFileIds = new();
    private readonly SemaphoreSlim _concurrencyLimiter = new(configManager.GetMaxConcurrentAnalyses(), configManager.GetMaxConcurrentAnalyses());

    public IEnumerable<AnalysisInfo> GetActiveAnalyses() => _activeAnalyses.Values;

    /// <summary>
    /// Suppresses NzbAnalysisService from being triggered for the given file ID.
    /// Used by Step 5 (QueueItemProcessor) to prevent the WebDAV GET handler from
    /// spawning duplicate background analyses when ffprobe reads files via HTTP.
    /// </summary>
    public static IDisposable SuppressAnalysisFor(Guid fileId)
    {
        _suppressedFileIds.TryAdd(fileId, 0);
        return new SuppressToken(fileId);
    }

    public void TriggerAnalysisInBackground(Guid fileId, string[]? segmentIds, bool force = false)
    {
        if (!force && !configManager.IsAnalysisEnabled()) return;
        if (_activeAnalyses.ContainsKey(fileId)) return;
        if (_suppressedFileIds.ContainsKey(fileId)) return;
        if (!_queuedAnalyses.TryAdd(fileId, 0)) return;

        var queued = backgroundTaskQueue.TryQueue($"NZB analysis for {fileId}", async ct =>
        {
            try
            {
                await PerformAnalysis(fileId, segmentIds, force, ct).ConfigureAwait(false);
            }
            finally
            {
                _queuedAnalyses.TryRemove(fileId, out _);
            }
        });

        if (!queued)
        {
            _queuedAnalyses.TryRemove(fileId, out _);
            Log.Warning("[NzbAnalysisService] Failed to queue background analysis for {Id}", fileId);
            websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|error");
        }
    }

    private async Task PerformAnalysis(Guid fileId, string[]? segmentIds, bool force, CancellationToken queueCancellationToken)
    {
        var info = new AnalysisInfo { Id = fileId, Name = "Queued" };
        if (!_activeAnalyses.TryAdd(fileId, info)) return;
        var acquiredAnalysisSlot = false;

        try
        {
            // Wait for available analysis slot
            await _concurrencyLimiter.WaitAsync(queueCancellationToken).ConfigureAwait(false);
            acquiredAnalysisSlot = true;

            Log.Debug("[NzbAnalysisService] Starting background analysis for file {Id} (Force={Force})", fileId, force);

            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

            var davItem = await dbContext.Items.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fileId).ConfigureAwait(false);
            if (davItem == null) return;

            var nzbFile = await dbContext.NzbFiles.FirstOrDefaultAsync(f => f.Id == fileId).ConfigureAwait(false);

            // Update name in tracking info
            info.Name = davItem.Name;
            
            // Try to extract Job Name (Parent Directory Name)
            if (davItem.Path != null)
            {
                // Path format: /.../Category/JobName/Filename.ext
                var directoryName = JobNameUtil.FromDavPath(davItem.Path);
                if (!string.IsNullOrEmpty(directoryName))
                {
                    info.JobName = directoryName;
                }
            }
            
            Log.Information("[NzbAnalysisService] Starting analysis for file: {FileName} ({Id})", info.Name, fileId);
            
            // Broadcast initial state with JobName
            websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|start|{info.Name}|{info.JobName}");

            var segmentAnalysisComplete = nzbFile == null || nzbFile.SegmentSizes != null;
            var mediaAnalysisComplete = davItem.MediaInfo != null;

            if (!force && segmentAnalysisComplete && mediaAnalysisComplete)
            {
                Log.Information("[NzbAnalysisService] Analysis already complete for file: {FileName} ({Id})", info.Name, fileId);
                websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|100");
                websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|done");
                return;
            }

            // Create cancellation token with usage context so analysis operations show up in stats
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(queueCancellationToken);
            // Normalize AffinityKey from parent directory (matches WebDav file patterns)
            var rawAffinityKey = Path.GetFileName(Path.GetDirectoryName(davItem.Path));
            var normalizedAffinityKey = FilenameNormalizer.NormalizeName(rawAffinityKey);
            var usageContext = new ConnectionUsageContext(
                ConnectionUsageType.Analysis,
                new ConnectionUsageDetails { Text = davItem.Path, JobName = davItem.Name, AffinityKey = normalizedAffinityKey, DavItemId = davItem.Id }
            );
            using var _ = cts.Token.SetScopedContext(usageContext);

            if (nzbFile != null && (force || nzbFile.SegmentSizes == null) && segmentIds != null)
            {
                var progressHook = new Progress<int>();
                var debounce = DebounceUtil.CreateDebounce(TimeSpan.FromMilliseconds(500));
                progressHook.ProgressChanged += (_, count) =>
                {
                    // Scale NZB analysis to 90%
                    var percentage = (int)((double)count / segmentIds.Length * 90);
                    if (percentage > info.Progress)
                    {
                        info.Progress = percentage;
                        debounce(() => websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|{percentage}"));
                    }
                };

                var sizes = await usenetClient.AnalyzeNzbAsync(segmentIds, 10, progressHook, cts.Token).ConfigureAwait(false);

                nzbFile.SetSegmentSizes(sizes);
                dbContext.NzbFiles.Update(nzbFile);
                await dbContext.SaveChangesAsync().ConfigureAwait(false);
            }

            // Media Analysis (ffprobe)
            var mediaResult = MediaAnalysisResult.Success;
            if (force || !mediaAnalysisComplete)
            {
                info.Progress = 90;
                websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|90");
                mediaResult = await mediaAnalysisService.AnalyzeMediaAsync(fileId, cts.Token).ConfigureAwait(false);
            }

            // Handle ffprobe timeout - schedule retry if we haven't already retried
            if (mediaResult == MediaAnalysisResult.Timeout)
            {
                var retryCount = _ffprobeRetryAttempts.GetOrAdd(fileId, 0);
                if (retryCount < 1)
                {
                    _ffprobeRetryAttempts[fileId] = retryCount + 1;
                    Log.Warning("[NzbAnalysisService] ffprobe timed out for {FileName}. Scheduling retry in 1 hour (attempt {Attempt}/1)", info.Name, retryCount + 1);

                    var retryQueued = backgroundTaskQueue.TryQueueDelayed($"ffprobe retry for {fileId}", TimeSpan.FromHours(1), ct =>
                    {
                        Log.Information("[NzbAnalysisService] Executing scheduled ffprobe retry for {FileName} ({Id})", info.Name, fileId);
                        TriggerAnalysisInBackground(fileId, segmentIds, force: true);
                        return Task.CompletedTask;
                    });

                    if (!retryQueued)
                    {
                        websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|error");
                        await SaveAnalysisHistoryAsync(fileId, info.Name, info.JobName, "Failed", "Media analysis failed: ffprobe timed out and the retry could not be queued.").ConfigureAwait(false);
                        return;
                    }

                    websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|90");
                    websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|pending");
                    await SaveAnalysisHistoryAsync(fileId, info.Name, info.JobName, "Pending", "Media analysis pending: ffprobe timed out; retry scheduled in 1 hour.").ConfigureAwait(false);
                    return;
                }
                else
                {
                    // Already retried once, mark as failed
                    Log.Warning("[NzbAnalysisService] ffprobe timed out again for {FileName}. Max retries reached.", info.Name);
                    _ffprobeRetryAttempts.TryRemove(fileId, out var unused1);
                    websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|error");
                    await SaveAnalysisHistoryAsync(fileId, info.Name, info.JobName, "Failed", "Media analysis failed: ffprobe timed out again after retry.").ConfigureAwait(false);
                    return;
                }
            }

            if (mediaResult == MediaAnalysisResult.Removed)
            {
                Log.Information("[NzbAnalysisService] Analysis skipped for {FileName} ({Id}) because the item was removed during analysis.", info.Name, fileId);
                _ffprobeRetryAttempts.TryRemove(fileId, out var unusedRemovedRetry);
                websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|done");
                await SaveAnalysisHistoryAsync(fileId, info.Name, info.JobName, "Skipped", "Analysis skipped: the file was removed during health repair while analysis was running.").ConfigureAwait(false);
                return;
            }

            if (mediaResult == MediaAnalysisResult.Failed)
            {
                Log.Warning("[NzbAnalysisService] Media analysis failed for {FileName} ({Id}).", info.Name, fileId);
                _ffprobeRetryAttempts.TryRemove(fileId, out var unusedFailedRetry);
                websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|error");
                await SaveAnalysisHistoryAsync(fileId, info.Name, info.JobName, "Failed", "Media analysis failed: ffprobe could not read valid media metadata. The file may be corrupt, incomplete, or unavailable.").ConfigureAwait(false);
                return;
            }

            // Clear retry counter on success
            _ffprobeRetryAttempts.TryRemove(fileId, out var unused2);

            websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|100");
            websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|done");
            Log.Information("[NzbAnalysisService] Finished analysis for file: {FileName} ({Id})", info.Name, fileId);

            await SaveAnalysisHistoryAsync(fileId, info.Name, info.JobName, "Success", "Analysis completed: segment-size cache and ffprobe media metadata are up to date.").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (queueCancellationToken.IsCancellationRequested)
        {
            Log.Information("[NzbAnalysisService] Analysis cancelled during shutdown for file {Id}", fileId);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Log.Information(ex, "[NzbAnalysisService] Analysis skipped for file {Id} because the database row was removed while analysis was running.", fileId);
            websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|done");
            await SaveAnalysisHistoryAsync(fileId, info.Name, info.JobName, "Skipped", "Analysis skipped: the file was removed during health repair while analysis was running.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NzbAnalysisService] Failed to analyze file {Id}", fileId);
            websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{fileId}|error");
            await SaveAnalysisHistoryAsync(fileId, info.Name, info.JobName, "Failed", $"Analysis failed: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            _activeAnalyses.TryRemove(fileId, out _);
            if (acquiredAnalysisSlot)
            {
                _concurrencyLimiter.Release();
            }
        }
    }

    private async Task SaveAnalysisHistoryAsync(Guid davItemId, string fileName, string jobName, string result, string details)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
            var itemPath = await db.Items
                .AsNoTracking()
                .Where(i => i.Id == davItemId)
                .Select(i => i.Path)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);
            jobName = JobNameUtil.PreferJobName(jobName, fileName, itemPath);
            
            var item = new AnalysisHistoryItem
            {
                DavItemId = davItemId,
                FileName = fileName,
                JobName = jobName,
                Result = result,
                Details = details,
                CreatedAt = DateTimeOffset.UtcNow
            };
            
            db.AnalysisHistoryItems.Add(item);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[NzbAnalysisService] Failed to save analysis history for {FileName}", fileName);
        }
    }

    private sealed class SuppressToken(Guid fileId) : IDisposable
    {
        public void Dispose() => _suppressedFileIds.TryRemove(fileId, out _);
    }
}