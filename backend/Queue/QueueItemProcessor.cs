using System.Collections.Concurrent;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Queue.DeobfuscationSteps._2.GetPar2FileDescriptors;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileAggregators;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;
using Usenet.Nzb;

namespace NzbWebDAV.Queue;

public class QueueItemProcessor(
    QueueItem queueItem,
    QueueNzbContents queueNzbContents,
    IServiceScopeFactory scopeFactory,
    UsenetStreamingClient usenetClient,
    ConfigManager configManager,
    WebsocketManager websocketManager,
    HealthCheckService healthCheckService,
    RcloneRcService rcloneRcService,
    IProgress<int> progress,
    CancellationToken ct
)
{
    public async Task ProcessAsync()
    {
        Log.Information("[QueueItemProcessor] Starting processing for {JobName} ({Id})", queueItem.JobName, queueItem.Id);
        // initialize
        var startTime = DateTime.Now;
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{queueItem.Id}|Downloading");

        // process the job
        try
        {
            Log.Debug("[QueueItemProcessor] Calling ProcessQueueItemAsync for {JobName}", queueItem.JobName);
            await ProcessQueueItemAsync(startTime).ConfigureAwait(false);
            Log.Information("[QueueItemProcessor] Successfully completed processing for {JobName}", queueItem.JobName);
        }

        // When a queue-item is removed while processing,
        // then we need to clear any db changes and finish early.
        catch (Exception e) when (e.GetBaseException() is OperationCanceledException or TaskCanceledException)
        {
            try
            {
                Log.Warning("[QueueItemProcessor] Processing of queue item {JobName} ({Id}) was cancelled. Exception: {Exception}",
                    queueItem.JobName, queueItem.Id, e.GetBaseException().Message);
                using var scope = scopeFactory.CreateScope();
                var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
                Log.Debug("[QueueItemProcessor] Marking cancelled item {JobName} as completed in history", queueItem.JobName);
                await MarkQueueItemCompleted(dbClient, startTime, error: "Processing was cancelled (timeout or manual cancellation)", failureReason: GetFailureReason(e)).ConfigureAwait(false);
                Log.Information("[QueueItemProcessor] Successfully moved cancelled item {JobName} to history", queueItem.JobName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[QueueItemProcessor] Failed to mark cancelled queue item {JobName} as completed: {Error}",
                    queueItem.JobName, ex.Message);
            }
        }

        // when a retryable error is encountered
        // let's not remove the item from the queue
        // to give it a chance to retry. Simply
        // log the error and retry in a minute.
        catch (Exception e) when (e.IsRetryableDownloadException())
        {
            try
            {
                Log.Warning("[QueueItemProcessor] Retryable error processing job {JobName} ({Id}): {Message}. Will retry in 1 minute.",
                    queueItem.JobName, queueItem.Id, e.Message);
                using var scope = scopeFactory.CreateScope();
                var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
                
                // We need to attach queueItem to the new context because it was tracked by the old (now gone) context?
                // Actually queueItem object comes from QueueManager loop context which is disposed?
                // No, QueueManager loop keeps context alive.
                // BUT QueueItemProcessor now doesn't have that context.
                // So we must attach it or fetch it again.
                
                // Fetching fresh is safer.
                var item = await dbClient.Ctx.QueueItems.FirstOrDefaultAsync(x => x.Id == queueItem.Id, ct);
                if (item != null)
                {
                    item.PauseUntil = DateTime.Now.AddMinutes(1);
                    await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                    Log.Debug("[QueueItemProcessor] Set PauseUntil to {PauseUntil} for retryable error", item.PauseUntil);
                }
                else
                {
                    Log.Warning("[QueueItemProcessor] Could not find queue item {Id} to set PauseUntil", queueItem.Id);
                }
                _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{queueItem.Id}|Queued");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[QueueItemProcessor] Error handling retryable exception: {Error}", ex.Message);
            }
        }

        // when any other error is encountered,
        // we must still remove the queue-item and add
        // it to the history as a failed job.
        catch (Exception e)
        {
            try
            {
                Log.Error(e, "[QueueItemProcessor] Fatal error processing job {JobName} ({Id}): {Message}. Moving to history as failed.",
                    queueItem.JobName, queueItem.Id, e.Message);
                using var scope = scopeFactory.CreateScope();
                var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
                Log.Debug("[QueueItemProcessor] Marking failed item {JobName} as completed in history", queueItem.JobName);
                await MarkQueueItemCompleted(dbClient, startTime, error: e.Message, failureReason: GetFailureReason(e)).ConfigureAwait(false);
                Log.Information("[QueueItemProcessor] Successfully moved failed item {JobName} to history", queueItem.JobName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[QueueItemProcessor] Failed to mark queue item {JobName} as completed: {Error}",
                    queueItem.JobName, ex.Message);
            }
        }
    }

    private async Task ProcessQueueItemAsync(DateTime startTime)
    {
        DavItem? existingMountFolder = null;
        string duplicateNzbBehavior = "ignore"; // default

        // Scope 1: Initial DB checks
        using (var scope = scopeFactory.CreateScope())
        {
            var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
            existingMountFolder = await GetMountFolder(dbClient).ConfigureAwait(false);
            duplicateNzbBehavior = configManager.GetDuplicateNzbBehavior();

            // if the mount folder already exists and setting is `marked-failed`
            // then immediately mark the job as failed.
            var isDuplicateNzb = existingMountFolder is not null;
            if (isDuplicateNzb && duplicateNzbBehavior == "mark-failed")
            {
                const string error = "Duplicate nzb: the download folder for this nzb already exists.";
                await MarkQueueItemCompleted(dbClient, startTime, error, "Duplicate NZB", () => Task.FromResult(existingMountFolder)).ConfigureAwait(false);
                return;
            }
        }

        // GlobalOperationLimiter now handles all connection limits - no need for reserved connections
        var providerConfig = configManager.GetUsenetProviderConfig();
        var concurrency = configManager.GetMaxDownloadConnections() + 5;
        Log.Information("[Queue] Processing '{JobName}': TotalConnections={TotalConnections}, DownloadConcurrency={Concurrency}", queueItem.JobName, providerConfig.TotalPooledConnections, concurrency);
        
        // Create a linked token for context propagation (more robust than setting on existing token)
        using var queueCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var _1 = queueCts.Token.SetScopedContext(new ConnectionUsageContext(ConnectionUsageType.Queue, queueItem.JobName));
        var queueCt = queueCts.Token;

        // read the nzb document
        Log.Debug("[QueueItemProcessor] Parsing NZB document for {JobName}. NZB size: {NzbSizeBytes} bytes",
            queueItem.JobName, queueNzbContents.NzbContents.Length);
        var parseStartTime = DateTime.UtcNow;
        var documentBytes = Encoding.UTF8.GetBytes(queueNzbContents.NzbContents);
        using var stream = new MemoryStream(documentBytes);
        var nzb = await NzbDocument.LoadAsync(stream).ConfigureAwait(false);
        // Check filename for password first (e.g., "Movie.Name{{password}}.nzb" or "Movie.Name password=secret.nzb")
        // Fall back to NZB metadata if not found in filename
        var archivePassword = FilenameUtil.GetNzbPassword(queueItem.FileName)
            ?? nzb.MetaData.GetValueOrDefault("password")?.FirstOrDefault();
        var nzbFiles = nzb.Files.Where(x => x.Segments.Count > 0).ToList();
        var parseElapsed = DateTime.UtcNow - parseStartTime;
        Log.Information("[QueueItemProcessor] Successfully parsed NZB for {JobName}. Files: {FileCount}, Total segments: {SegmentCount}, Elapsed: {ElapsedMs}ms",
            queueItem.JobName, nzbFiles.Count, nzbFiles.Sum(f => f.Segments.Count), parseElapsed.TotalMilliseconds);

        if (archivePassword != null)
        {
            Log.Information("[QueueItemProcessor] Archive password detected for {JobName}", queueItem.JobName);
        }

        // step 0 -- perform article existence pre-check against cache
        // https://github.com/nzbdav-dev/nzbdav/issues/101
        Log.Debug("[QueueItemProcessor] Step 0: Pre-checking article existence against cache for {JobName}...", queueItem.JobName);
        var articlesToPrecheck = nzbFiles.SelectMany(x => x.Segments).Select(x => x.MessageId.Value);
        healthCheckService.CheckCachedMissingSegmentIds(articlesToPrecheck);
        Log.Debug("[QueueItemProcessor] Step 0 complete: Pre-checked {ArticleCount} articles for {JobName}", articlesToPrecheck.Count(), queueItem.JobName);

        // step 1 -- get name and size of each nzb file
        Log.Information("[QueueItemProcessor] Step 1: Starting deobfuscation for {JobName}. Processing {FileCount} files (progress 0-50%)...",
            queueItem.JobName, nzbFiles.Count);
        var step1StartTime = DateTime.UtcNow;
        var part1Progress = progress
            .Scale(50, 100)
            .ToPercentage(nzbFiles.Count);

        Log.Debug("[QueueItemProcessor] Step 1a: Fetching first segments for {FileCount} files in {JobName}...", nzbFiles.Count, queueItem.JobName);
        var segments = await FetchFirstSegmentsStep.FetchFirstSegments(
            nzbFiles, usenetClient, configManager, queueCt, part1Progress).ConfigureAwait(false);
        Log.Information("[QueueItemProcessor] Step 1a complete: Fetched {SegmentCount} first segments for {JobName}",
            segments.Count, queueItem.JobName);

        // If we failed to fetch first segments for every file, treat this as transient infrastructure failure
        // (e.g. provider outage / OOM / network) rather than a permanent bad NZB.
        if (nzbFiles.Count > 0 && segments.Count == 0)
        {
            throw new RetryableDownloadException(
                $"Could not fetch first segments for any NZB file in {queueItem.JobName}. Will retry.");
        }

        Log.Debug("[QueueItemProcessor] Step 1b: Extracting Par2 file descriptors for {JobName}...", queueItem.JobName);
        var par2FileDescriptors = await GetPar2FileDescriptorsStep.GetPar2FileDescriptors(
            segments, usenetClient, queueCt).ConfigureAwait(false);
        Log.Information("[QueueItemProcessor] Step 1b complete: Found {Par2Count} Par2 file descriptors for {JobName}",
            par2FileDescriptors.Count, queueItem.JobName);

        Log.Debug("[QueueItemProcessor] Step 1c: Building file info objects for {JobName}...", queueItem.JobName);
        var fileInfos = GetFileInfosStep.GetFileInfos(
            segments, par2FileDescriptors);
        var step1Elapsed = DateTime.UtcNow - step1StartTime;
        Log.Information("[QueueItemProcessor] Step 1 complete: Deobfuscation finished for {JobName}. FileInfos: {FileInfoCount}, Elapsed: {ElapsedSeconds}s",
            queueItem.JobName, fileInfos.Count, step1Elapsed.TotalSeconds);

        // step 1b -- batch fetch file sizes for files without Par2 descriptors
        var filesWithoutSize = fileInfos.Where(f => f.FileSize == null).Select(f => f.NzbFile).ToList();
        if (filesWithoutSize.Count > 0)
        {
            Log.Debug("[QueueItemProcessor] Step 1d: Fetching file sizes for {FileCount} files without Par2 descriptors in {JobName}...",
                filesWithoutSize.Count, queueItem.JobName);
            var fileSizeStartTime = DateTime.UtcNow;
            // Use capped QueueAnalysis context for file size analysis to limit connection consumption
            using var analysisCts = CancellationTokenSource.CreateLinkedTokenSource(queueCt);
            using var _analysisCtx = analysisCts.Token.SetScopedContext(new ConnectionUsageContext(ConnectionUsageType.QueueAnalysis, queueItem.JobName));
            var fileSizes = await usenetClient.GetFileSizesBatchAsync(filesWithoutSize, concurrency, analysisCts.Token).ConfigureAwait(false);
            var fileSizeElapsed = DateTime.UtcNow - fileSizeStartTime;
            Log.Information("[QueueItemProcessor] Step 1d complete: Fetched {FileCount} file sizes for {JobName}. Elapsed: {ElapsedSeconds}s",
                fileSizes.Count, queueItem.JobName, fileSizeElapsed.TotalSeconds);
            foreach (var fileInfo in fileInfos.Where(f => f.FileSize == null))
            {
                if (fileSizes.TryGetValue(fileInfo.NzbFile, out var size))
                {
                    fileInfo.FileSize = size;
                }
            }
        }

        // step 2 -- perform file processing
        Log.Information("[QueueItemProcessor] Step 2: Creating file processors for {JobName}. FileInfos: {FileInfoCount}",
            queueItem.JobName, fileInfos.Count);
        var fileProcessors = GetFileProcessors(fileInfos, archivePassword, queueCt).ToList();
        Log.Information("[QueueItemProcessor] Step 2: Created {ProcessorCount} file processors for {JobName} (progress 50-100%)",
            fileProcessors.Count, queueItem.JobName);

        var fileConcurrency = configManager.GetMaxDownloadConnections() + 5;
        Log.Debug("[QueueItemProcessor] Step 2: File processing concurrency: {FileConcurrency} for {JobName}", fileConcurrency, queueItem.JobName);

        var part2Progress = progress
            .Offset(50)
            .Scale(50, 100)
            .ToPercentage(fileProcessors.Count);

        Log.Information("[QueueItemProcessor] Step 2: Starting file processing for {ProcessorCount} processors in {JobName}...",
            fileProcessors.Count, queueItem.JobName);
        var step2StartTime = DateTime.UtcNow;
        var fileProcessingResultsAll = await fileProcessors
            .Select(x => x!.ProcessAsync())
            .WithConcurrencyAsync(fileConcurrency)
            .GetAllAsync(queueCt, part2Progress).ConfigureAwait(false);
        var fileProcessingResults = fileProcessingResultsAll
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();
        var step2Elapsed = DateTime.UtcNow - step2StartTime;
        Log.Information("[QueueItemProcessor] Step 2 complete: File processing finished for {JobName}. Results: {ResultCount}, Elapsed: {ElapsedSeconds}s",
            queueItem.JobName, fileProcessingResults.Count, step2Elapsed.TotalSeconds);

        // step 3 -- Per-file smart article existence probe (checks ~3 segments per file).
        //           Running per-file ensures the uniformity check works (segment sizes are uniform within a file).
        //           Detects DMCA/takedown fast. For partial missing articles, ffprobe in Step 5 is the definitive check.
        var checkedFullHealth = false;
        var triggerMediaAnalysis = false;
        var probePassedNames = new ConcurrentBag<string>();
        var probeFailedNames = new ConcurrentBag<string>();
        var dmcaFileNames = new ConcurrentBag<string>();
        var probeTimedOut = false;
        if (configManager.IsEnsureArticleExistenceEnabled())
        {
            var step3StartTime = DateTime.UtcNow;
            var fileSegments = fileProcessingResults
                .Select(GetResultSegmentIdsAndName)
                .Where(x => x.SegmentIds.Length > 0)
                .ToList();

            var totalSegmentCount = fileSegments.Sum(x => x.SegmentIds.Length);

            Log.Information("[QueueItemProcessor] Step 3: Starting per-file smart article probe for {JobName}. Files: {FileCount}, Articles: {ArticleCount} (progress 100+)",
                queueItem.JobName, fileSegments.Count, totalSegmentCount);

            var part3Progress = progress
                .Offset(100)
                .ToPercentage(totalSegmentCount);

            var processedSegments = 0;
            var probeFailures = 0;
            var dmcaCount = 0;

            // 180s overall backstop, 15s per-file timeout, 8 concurrent probes
            using var overallProbeCts = CancellationTokenSource.CreateLinkedTokenSource(queueCt);
            overallProbeCts.CancelAfter(TimeSpan.FromSeconds(180));
            // Propagate QueueAnalysis context for article probes (lighter limits than full Queue)
            var probeContext = new ConnectionUsageContext(ConnectionUsageType.QueueAnalysis, queueItem.JobName);
            using var _probeCtx = overallProbeCts.Token.SetScopedContext(probeContext);

            try
            {
                await Parallel.ForEachAsync(
                    fileSegments,
                    new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = overallProbeCts.Token },
                    async (file, ct) =>
                    {
                        // Retry once on transient failures (connection errors trigger full-scan fallback which may timeout)
                        for (var attempt = 0; attempt < 2; attempt++)
                        {
                            using var fileCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            fileCts.CancelAfter(TimeSpan.FromSeconds(15));
                            using var _fileCtx = fileCts.Token.SetScopedContext(probeContext);

                            try
                            {
                                await usenetClient.AnalyzeNzbAsync(file.SegmentIds, concurrency, null, fileCts.Token, useSmartAnalysis: true).ConfigureAwait(false);
                                probePassedNames.Add(file.FileName);
                                break; // Success — exit retry loop
                            }
                            catch (UsenetArticleNotFoundException)
                            {
                                Interlocked.Increment(ref probeFailures);
                                probeFailedNames.Add(file.FileName);
                                break; // Definitive — no retry
                            }
                            catch (NonRetryableDownloadException)
                            {
                                Interlocked.Increment(ref dmcaCount);
                                dmcaFileNames.Add(file.FileName);
                                break; // Definitive DMCA — no retry, but continue probing other files
                            }
                            catch (OperationCanceledException) when (!queueCt.IsCancellationRequested)
                            {
                                if (attempt == 0)
                                {
                                    Log.Debug("[QueueItemProcessor] Step 3: Retrying probe for {FileName} after timeout", file.FileName);
                                    continue; // Retry once
                                }
                                Interlocked.Increment(ref probeFailures);
                            }
                            catch (Exception ex)
                            {
                                if (attempt == 0)
                                {
                                    Log.Debug("[QueueItemProcessor] Step 3: Retrying probe for {FileName} after error: {Error}", file.FileName, ex.Message);
                                    continue; // Retry once
                                }
                                Log.Warning(ex, "[QueueItemProcessor] Step 3: Probe failed for {FileName} after retry", file.FileName);
                                Interlocked.Increment(ref probeFailures);
                            }
                        }

                        var processed = Interlocked.Add(ref processedSegments, file.SegmentIds.Length);
                        ((IProgress<int>?)part3Progress)?.Report(processed);
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!queueCt.IsCancellationRequested)
            {
                Log.Warning("[QueueItemProcessor] Step 3: Overall probe timeout (180s) for {JobName}. Will use ffprobe to verify file integrity.",
                    queueItem.JobName);
                triggerMediaAnalysis = true;
                probeTimedOut = true;
            }

            var dmcaCountFinal = Volatile.Read(ref dmcaCount);
            if (dmcaCountFinal > 0)
            {
                if (dmcaCountFinal >= fileSegments.Count)
                {
                    Log.Error("[QueueItemProcessor] Step 3: All {DmcaCount} files confirmed DMCA/takedown for {JobName}",
                        dmcaCountFinal, queueItem.JobName);
                    throw new NonRetryableDownloadException($"DMCA/takedown pattern detected — all {dmcaCountFinal} files are taken down for {queueItem.JobName}");
                }

                Log.Warning("[QueueItemProcessor] Step 3: {DmcaCount}/{FileCount} files confirmed DMCA/takedown for {JobName}. Continuing with remaining files.",
                    dmcaCountFinal, fileSegments.Count, queueItem.JobName);
                triggerMediaAnalysis = true; // Enter Step 5 to clean up DMCA'd files from DB
            }

            if (probeFailures > 0)
            {
                Log.Warning("[QueueItemProcessor] Step 3: {FailCount}/{FileCount} files had probe issues for {JobName}. Will run ffprobe to verify integrity.",
                    probeFailures, fileSegments.Count, queueItem.JobName);
                triggerMediaAnalysis = true;
            }
            else if (!triggerMediaAnalysis)
            {
                checkedFullHealth = true;
            }

            // If no files passed the probe and it wasn't just a timeout, the content is unavailable
            if (!probeTimedOut && probePassedNames.IsEmpty && fileSegments.Count > 0)
            {
                var failedCount = probeFailedNames.Count;
                var reason = dmcaCountFinal > 0
                    ? $"{dmcaCountFinal}/{fileSegments.Count} files DMCA/taken down, {failedCount} others failed"
                    : $"All {failedCount}/{fileSegments.Count} files failed article probe";
                Log.Error("[QueueItemProcessor] Step 3: No files passed probe for {JobName}. Marking as failed. Reason: {Reason}",
                    queueItem.JobName, reason);
                throw new NonRetryableDownloadException($"No files are available: {reason}");
            }

            var step3Elapsed = DateTime.UtcNow - step3StartTime;
            Log.Information("[QueueItemProcessor] Step 3 complete: Per-file article probe finished for {JobName}. FullHealth: {FullHealth}, TriggerAnalysis: {TriggerAnalysis}, Failures: {Failures}/{Total}, DMCA: {DmcaCount}, Elapsed: {Elapsed}s",
                queueItem.JobName, checkedFullHealth, triggerMediaAnalysis, probeFailures, fileSegments.Count, dmcaCountFinal, step3Elapsed.TotalSeconds);
        }
        else
        {
            Log.Debug("[QueueItemProcessor] Step 3: Skipping article existence check (disabled in config) for {JobName}", queueItem.JobName);
        }

        // Scope 2: Final DB update — create filesystem entries but keep queue item alive until Step 6
        Log.Information("[QueueItemProcessor] Step 4: Starting database update for {JobName}...", queueItem.JobName);
        var step4StartTime = DateTime.UtcNow;
        Guid? mountFolderId = null;
        DavItem? mountFolder = null;
        using (var scope = scopeFactory.CreateScope())
        {
            var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
            dbClient.Ctx.ChangeTracker.Clear();

            Log.Debug("[QueueItemProcessor] Step 4a: Creating category and mount folders for {JobName}...", queueItem.JobName);
            var categoryFolder = await GetOrCreateCategoryFolder(dbClient).ConfigureAwait(false);
            mountFolder = await CreateMountFolder(dbClient, categoryFolder, existingMountFolder, duplicateNzbBehavior).ConfigureAwait(false);
            mountFolderId = mountFolder.Id;

            Log.Debug("[QueueItemProcessor] Step 4b: Running aggregators for {JobName}...", queueItem.JobName);
            new RarAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);
            new FileAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);
            new SevenZipAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);
            new MultipartMkvAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);

            Log.Debug("[QueueItemProcessor] Step 4c: Running post-processors for {JobName}...", queueItem.JobName);
            // post-processing
            new RenameDuplicatesPostProcessor(dbClient).RenameDuplicates();
            new BlacklistedExtensionPostProcessor(configManager, dbClient).RemoveBlacklistedExtensions();

            // validate media files found (video or audio)
            if (configManager.IsEnsureImportableMediaEnabled())
            {
                Log.Debug("[QueueItemProcessor] Step 4d: Validating importable media for {JobName}...", queueItem.JobName);
                new EnsureImportableMediaValidator(dbClient).ThrowIfValidationFails();
            }

            // create strm files, if necessary
            if (configManager.GetImportStrategy() == "strm")
            {
                Log.Debug("[QueueItemProcessor] Step 4e: Creating STRM files for {JobName}...", queueItem.JobName);
                await new CreateStrmFilesPostProcessor(configManager, dbClient).CreateStrmFilesAsync().ConfigureAwait(false);
            }

            Log.Debug("[QueueItemProcessor] Step 4f: All database operations complete for {JobName}", queueItem.JobName);
            await dbClient.Ctx.SaveChangesAsync(queueCt).ConfigureAwait(false);
        }
        var step4Elapsed = DateTime.UtcNow - step4StartTime;
        Log.Information("[QueueItemProcessor] Step 4 complete: Database update finished for {JobName}. Elapsed: {ElapsedSeconds}s",
            queueItem.JobName, step4Elapsed.TotalSeconds);

        // step 5 -- Run ffprobe + decode check on all media files to verify integrity.
        //           Corrupt files are removed from the database so Radarr/Sonarr won't import them.
        //           Always runs: Step 3 spot-checks catch missing articles, but only decode catches corrupt content.
        {
            Log.Information("[QueueItemProcessor] Step 5: Running media analysis (ffprobe) for files in {JobName} to verify playability...",
                queueItem.JobName);

            using var analysisScope = scopeFactory.CreateScope();
            var dbContext = analysisScope.ServiceProvider.GetRequiredService<DavDatabaseContext>();
            var mediaAnalysis = analysisScope.ServiceProvider.GetRequiredService<MediaAnalysisService>();

            var allItems = await dbContext.Items
                .Where(i => i.ParentId == mountFolderId && i.Type != DavItem.ItemType.Directory)
                .Select(i => new { i.Id, i.Name })
                .ToListAsync().ConfigureAwait(false);

            // Only run ffprobe on media files not confirmed DMCA'd or probe-failed
            var dmcaNameSet = new HashSet<string>(dmcaFileNames, StringComparer.OrdinalIgnoreCase);
            var failedProbeSet = new HashSet<string>(probeFailedNames, StringComparer.OrdinalIgnoreCase);
            var mediaFiles = allItems.Where(i => FilenameUtil.IsMediaFile(i.Name)).ToList();

            // Remove DMCA'd files from the database — they're confirmed dead
            if (dmcaNameSet.Count > 0)
            {
                var dmcaItemIds = allItems.Where(i => dmcaNameSet.Contains(i.Name)).Select(i => i.Id).ToList();
                if (dmcaItemIds.Count > 0)
                {
                    var dmcaDeleted = await dbContext.Items
                        .Where(i => dmcaItemIds.Contains(i.Id))
                        .ExecuteDeleteAsync().ConfigureAwait(false);
                    Log.Information("[QueueItemProcessor] Step 5: Removed {Deleted} DMCA/takedown files from {JobName}",
                        dmcaDeleted, queueItem.JobName);
                }
            }

            // Remove probe-failed files (missing articles) directly — no need for ffprobe
            if (failedProbeSet.Count > 0)
            {
                var failedItemIds = mediaFiles.Where(i => failedProbeSet.Contains(i.Name)).Select(i => i.Id).ToList();
                if (failedItemIds.Count > 0)
                {
                    var probeFailedDeleted = await dbContext.Items
                        .Where(i => failedItemIds.Contains(i.Id))
                        .ExecuteDeleteAsync().ConfigureAwait(false);
                    Log.Information("[QueueItemProcessor] Step 5: Removed {Deleted} probe-failed files (missing articles) from {JobName}",
                        probeFailedDeleted, queueItem.JobName);
                }
            }

            var filesToCheck = mediaFiles.Where(i => !dmcaNameSet.Contains(i.Name) && !failedProbeSet.Contains(i.Name)).ToList();

            Log.Information("[QueueItemProcessor] Step 5: Checking {CheckCount}/{MediaCount} media files with ffprobe + decode check (skipping {DmcaCount} DMCA, {FailedCount} probe-failed)",
                filesToCheck.Count, mediaFiles.Count, dmcaNameSet.Count, failedProbeSet.Count);

            if (filesToCheck.Count == 0)
            {
                Log.Information("[QueueItemProcessor] Step 5: No media files to check for {JobName}", queueItem.JobName);
            }
            else
            {
                var corruptIds = new ConcurrentBag<Guid>();
                var analysisHistoryRows = new ConcurrentBag<AnalysisHistoryItem>();

                await Parallel.ForEachAsync(filesToCheck, new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = queueCt }, async (item, ct) =>
                {
                    Log.Debug("[QueueItemProcessor] Step 5: Analyzing {FileName}...", item.Name);

                    // Show in active analyses UI via websocket
                    websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress,
                        $"{item.Id}|start|{item.Name}|{queueItem.JobName}");

                    // Suppress NzbAnalysisService from being triggered by the WebDAV GET that ffprobe makes.
                    using var suppression = NzbAnalysisService.SuppressAnalysisFor(item.Id);
                    var result = await mediaAnalysis.AnalyzeMediaAsync(item.Id, ct).ConfigureAwait(false);

                    // Report result to active analyses UI
                    string historyResult;
                    string historyDetails;
                    switch (result)
                    {
                        case MediaAnalysisResult.Failed:
                            Log.Warning("[QueueItemProcessor] Step 5: {FileName} failed media analysis — marking for removal", item.Name);
                            corruptIds.Add(item.Id);
                            websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{item.Id}|error");
                            historyResult = "Failed";
                            historyDetails = "Media integrity check failed — file corrupt or unplayable";
                            break;
                        case MediaAnalysisResult.Timeout:
                            Log.Warning("[QueueItemProcessor] Step 5: {FileName} timed out during analysis — keeping file", item.Name);
                            websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{item.Id}|done");
                            historyResult = "Pending";
                            historyDetails = "Media integrity check timed out — file kept for retry";
                            break;
                        default:
                            Log.Information("[QueueItemProcessor] Step 5: {FileName} passed media analysis", item.Name);
                            websocketManager.SendMessage(WebsocketTopic.AnalysisItemProgress, $"{item.Id}|done");
                            historyResult = "Success";
                            historyDetails = "Media integrity check passed";
                            break;
                    }

                    // Save to analysis history (use lock for EF Core DbContext thread safety)
                    analysisHistoryRows.Add(new AnalysisHistoryItem
                    {
                        DavItemId = item.Id,
                        FileName = item.Name,
                        JobName = queueItem.JobName,
                        Result = historyResult,
                        Details = historyDetails,
                        CreatedAt = DateTimeOffset.UtcNow
                    });
                }).ConfigureAwait(false);

                if (!analysisHistoryRows.IsEmpty)
                {
                    dbContext.AnalysisHistoryItems.AddRange(analysisHistoryRows);
                    await dbContext.SaveChangesAsync(queueCt).ConfigureAwait(false);
                }

                if (!corruptIds.IsEmpty)
                {
                    var corruptIdList = corruptIds.ToList();
                    var deleted = await dbContext.Items
                        .Where(i => corruptIdList.Contains(i.Id))
                        .ExecuteDeleteAsync().ConfigureAwait(false);
                    Log.Information("[QueueItemProcessor] Step 5 complete: Removed {Deleted} corrupt files from {JobName}. {Healthy}/{Total} media files remain.",
                        deleted, queueItem.JobName, filesToCheck.Count - corruptIdList.Count, filesToCheck.Count);
                }
                else
                {
                    Log.Information("[QueueItemProcessor] Step 5 complete: All {Count} checked media files passed analysis for {JobName}",
                        filesToCheck.Count, queueItem.JobName);
                }
            }

            // After Step 5 verification, mark all surviving items as health-checked.
            // This prevents them from appearing as "pending" in the health queue,
            // since Step 5's ffprobe + decode is more thorough than a STAT health check.
            var utcNow = DateTimeOffset.UtcNow;
            var survivingItems = await dbContext.Items
                .Where(i => i.ParentId == mountFolderId && i.Type != DavItem.ItemType.Directory)
                .ToListAsync(queueCt).ConfigureAwait(false);

            foreach (var item in survivingItems)
            {
                item.LastHealthCheck = utcNow;
                if (item.ReleaseDate != null)
                {
                    item.NextHealthCheck = item.ReleaseDate.Value + 2 * (utcNow - item.ReleaseDate.Value);
                }
            }

            if (survivingItems.Count > 0)
            {
                await dbContext.SaveChangesAsync(queueCt).ConfigureAwait(false);
                Log.Information("[QueueItemProcessor] Step 5: Updated health check timestamps on {Count} surviving items for {JobName}",
                    survivingItems.Count, queueItem.JobName);
            }

            // If we had media files but none survived Step 5, the NZB has no playable content — fail it
            if (mediaFiles.Count > 0)
            {
                var survivingMediaCount = survivingItems.Count(i => FilenameUtil.IsMediaFile(i.Name));
                if (survivingMediaCount == 0)
                {
                    Log.Error("[QueueItemProcessor] Step 5: All {MediaCount} media files were removed for {JobName}. Marking as failed.",
                        mediaFiles.Count, queueItem.JobName);
                    throw new NonRetryableDownloadException($"No playable media files remain after analysis — all {mediaFiles.Count} media file(s) were corrupt or unavailable");
                }
            }
        }

        // Step 6: Move queue item to history now that ffprobe analysis is complete
        Log.Information("[QueueItemProcessor] Step 6: Moving queue item to history for {JobName}...", queueItem.JobName);
        using (var scope = scopeFactory.CreateScope())
        {
            var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
            await MarkQueueItemCompleted(dbClient, startTime, error: null, failureReason: null, mountFolder: mountFolder).ConfigureAwait(false);
        }
        Log.Information("[QueueItemProcessor] Step 6 complete: {JobName} moved to history", queueItem.JobName);
    }

    private IEnumerable<BaseProcessor> GetFileProcessors
    (
        List<GetFileInfosStep.FileInfo> fileInfos,
        string? archivePassword,
        CancellationToken ct
    )
    {
        Log.Debug("[GetFileProcessors] Processing {FileInfoCount} file infos", fileInfos.Count);
        var maxConnections = configManager.GetMaxQueueConnections();
        
        // Smart Grouping: Group by base name first to keep multi-part files together
        var baseGroups = fileInfos
            .GroupBy(x => FilenameUtil.GetMultipartBaseName(x.FileName))
            .ToList();

        Log.Information("[GetFileProcessors] Identified {GroupCount} base file groups", baseGroups.Count);

        // Determine group type for each base group
        var finalGroups = new List<(string Type, List<GetFileInfosStep.FileInfo> Files)>();
        foreach (var baseGroup in baseGroups)
        {
            var files = baseGroup.ToList();
            var groupType = "other";

            // If ANY file in the group has RAR magic or extension, the whole group is RAR
            if (files.Any(x => x.IsRar || FilenameUtil.IsRarFile(x.FileName)))
            {
                groupType = "rar";
            }
            else if (files.Any(x => x.IsSevenZip || FilenameUtil.Is7zFile(x.FileName)))
            {
                groupType = "7z";
            }
            else if (files.Any(x => FilenameUtil.IsMultipartMkv(x.FileName)))
            {
                groupType = "multipart-mkv";
            }

            finalGroups.Add((groupType, files));
        }

        Log.Information("[GetFileProcessors] Classified groups: {GroupSummary}",
            string.Join(", ", finalGroups.GroupBy(g => g.Type).Select(g => $"{g.Key}={g.Count()}")));

        // Calculate adaptive concurrency per RAR to avoid connection pool exhaustion
        var rarGroupCount = finalGroups.Count(g => g.Type == "rar");
        var connectionsPerRar = rarGroupCount > 0
            ? Math.Max(1, Math.Min(5, maxConnections / Math.Max(1, rarGroupCount / 3)))
            : 1;

        foreach (var group in finalGroups)
        {
            Log.Debug("[GetFileProcessors] Processing group type '{GroupType}' with {FileCount} files. Base name: {BaseName}",
                group.Type, group.Files.Count, FilenameUtil.GetMultipartBaseName(group.Files.First().FileName));

            if (group.Type == "7z")
            {
                Log.Debug("[GetFileProcessors] Creating SevenZipProcessor for {FileCount} files", group.Files.Count);
                yield return new SevenZipProcessor(group.Files, usenetClient, archivePassword, ct);
            }

            else if (group.Type == "rar")
            {
                var rarFiles = group.Files;
                Log.Debug("[GetFileProcessors] Creating RarProcessor for group: {BaseName} ({Count} parts)", 
                    FilenameUtil.GetMultipartBaseName(rarFiles.First().FileName), rarFiles.Count);
                yield return new RarProcessor(rarFiles, usenetClient, archivePassword, ct, connectionsPerRar);
            }

            else if (group.Type == "multipart-mkv")
            {
                Log.Debug("[GetFileProcessors] Creating MultipartMkvProcessor for {FileCount} files", group.Files.Count);
                yield return new MultipartMkvProcessor(group.Files, usenetClient, ct);
            }

            else
            {
                Log.Debug("[GetFileProcessors] Creating {ProcessorCount} FileProcessors", group.Files.Count);
                foreach (var fileInfo in group.Files)
                {
                    yield return new FileProcessor(fileInfo, usenetClient, ct);
                }
            }
        }
    }

    private async Task<DavItem?> GetMountFolder(DavDatabaseClient dbClient)
    {
        var query = from mountFolder in dbClient.Ctx.Items
            join categoryFolder in dbClient.Ctx.Items on mountFolder.ParentId equals categoryFolder.Id
            where mountFolder.Name == queueItem.JobName
                  && mountFolder.ParentId != null
                  && categoryFolder.Name == queueItem.Category
                  && categoryFolder.ParentId == DavItem.ContentFolder.Id
            select mountFolder;

        return await query.FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    private async Task<DavItem> GetOrCreateCategoryFolder(DavDatabaseClient dbClient)
    {
        // if the category item already exists, return it
        var categoryFolder = await dbClient.GetDirectoryChildAsync(
            DavItem.ContentFolder.Id, queueItem.Category, ct).ConfigureAwait(false);
        if (categoryFolder is not null)
            return categoryFolder;

        // otherwise, create it
        categoryFolder = DavItem.New(
            id: GuidUtil.CreateDeterministic(DavItem.ContentFolder.Id, queueItem.Category),
            parent: DavItem.ContentFolder,
            name: queueItem.Category,
            fileSize: null,
            type: DavItem.ItemType.Directory,
            releaseDate: null,
            lastHealthCheck: null
        );
        dbClient.Ctx.Items.Add(categoryFolder);
        return categoryFolder;
    }

    private Task<DavItem> CreateMountFolder
    (
        DavDatabaseClient dbClient,
        DavItem categoryFolder,
        DavItem? existingMountFolder,
        string duplicateNzbBehavior
    )
    {
        if (existingMountFolder is not null && duplicateNzbBehavior == "increment")
            return IncrementMountFolder(dbClient, categoryFolder);

        var mountFolder = DavItem.New(
            id: GuidUtil.CreateDeterministic(categoryFolder.Id, queueItem.JobName),
            parent: categoryFolder,
            name: queueItem.JobName,
            fileSize: null,
            type: DavItem.ItemType.Directory,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: queueItem.Id
        );
        dbClient.Ctx.Items.Add(mountFolder);
        return Task.FromResult(mountFolder);
    }

    private async Task<DavItem> IncrementMountFolder(DavDatabaseClient dbClient, DavItem categoryFolder)
    {
        for (var i = 2; i < 100; i++)
        {
            var name = $"{queueItem.JobName} ({i})";
            var existingMountFolder = await dbClient.GetDirectoryChildAsync(categoryFolder.Id, name, ct).ConfigureAwait(false);
            if (existingMountFolder is not null) continue;

            var mountFolder = DavItem.New(
                id: GuidUtil.CreateDeterministic(categoryFolder.Id, name),
                parent: categoryFolder,
                name: name,
                fileSize: null,
                type: DavItem.ItemType.Directory,
                releaseDate: null,
                lastHealthCheck: null,
                historyItemId: queueItem.Id
            );
            dbClient.Ctx.Items.Add(mountFolder);
            return mountFolder;
        }

        throw new Exception("Duplicate nzb with more than 100 existing copies.");
    }

    private HistoryItem CreateHistoryItem(DavItem? mountFolder, DateTime jobStartTime, string? errorMessage = null, string? failureReason = null)
    {
        var now = DateTime.Now;
        return new HistoryItem()
        {
            Id = queueItem.Id,
            CreatedAt = now,
            CompletedAt = now,
            FileName = queueItem.FileName,
            JobName = queueItem.JobName,
            Category = queueItem.Category,
            DownloadStatus = errorMessage == null
                ? HistoryItem.DownloadStatusOption.Completed
                : HistoryItem.DownloadStatusOption.Failed,
            TotalSegmentBytes = queueItem.TotalSegmentBytes,
            DownloadTimeSeconds = (int)(DateTime.Now - jobStartTime).TotalSeconds,
            FailMessage = errorMessage,
            DownloadDirId = mountFolder?.Id,
            NzbContents = queueNzbContents.NzbContents,
            FailureReason = failureReason,
        };
    }

    private static string GetFailureReason(Exception exception)
    {
        var baseException = exception.GetBaseException();
        return baseException switch
        {
            UsenetArticleNotFoundException => "Missing Articles",
            OperationCanceledException or TaskCanceledException => "Timeout/Cancelled",
            CouldNotConnectToUsenetException or CouldNotLoginToUsenetException => "Connection Error",
            PasswordProtectedRarException or PasswordProtected7zException => "Password Protected",
            UnsupportedRarCompressionMethodException or Unsupported7zCompressionMethodException => "Unsupported Format",
            NoVideoFilesFoundException => "No Video Files",
            _ => "Unknown Error"
        };
    }

    private async Task MarkQueueItemCompleted
    (
        DavDatabaseClient dbClient,
        DateTime startTime,
        string? error = null,
        string? failureReason = null,
        Func<Task<DavItem?>>? databaseOperations = null,
        DavItem? mountFolder = null
    )
    {
        Log.Information("[QueueItemProcessor] MarkQueueItemCompleted called for {JobName} ({Id}). Error: {Error}",
            queueItem.JobName, queueItem.Id, error ?? "None");

        dbClient.Ctx.ChangeTracker.Clear();
        if (databaseOperations != null)
            mountFolder = await databaseOperations.Invoke().ConfigureAwait(false);
        var historyItem = CreateHistoryItem(mountFolder, startTime, error, failureReason);
        var historySlot = GetHistoryResponse.HistorySlot.FromHistoryItem(historyItem, mountFolder, configManager);

        Log.Debug("[QueueItemProcessor] Removing queue item {Id} from QueueItems table", queueItem.Id);
        // Ensure queueItem is attached to this context before removing
        dbClient.Ctx.QueueItems.Entry(queueItem).State = EntityState.Deleted;

        Log.Debug("[QueueItemProcessor] Adding history item {Id} to HistoryItems table. Status: {Status}",
            historyItem.Id, historyItem.DownloadStatus);
        dbClient.Ctx.HistoryItems.Add(historyItem);

        Log.Debug("[QueueItemProcessor] Saving changes to database...");
        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        Log.Information("[QueueItemProcessor] Successfully moved queue item {JobName} ({Id}) to history. Status: {Status}, CompletedAt: {CompletedAt}",
            queueItem.JobName, historyItem.Id, historyItem.DownloadStatus, historyItem.CompletedAt);

        // Notify Rclone to refresh the cache for the new item's path (parent directory)
        if (mountFolder != null)
        {
            try
            {
                var path = $"{queueItem.Category}/{queueItem.JobName}";
                Log.Debug("[QueueItemProcessor] Triggering Rclone VFS refresh for {Path}", path);
                await rcloneRcService.RefreshAsync(path).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[QueueItemProcessor] Failed to trigger Rclone refresh");
            }
        }

        // Forget /nzbs since queue item was consumed
        DavDatabaseContext.TriggerVfsForget("/nzbs");

        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemRemoved, queueItem.Id.ToString());
        _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemAdded, historySlot.ToJson());
        _ = RefreshMonitoredDownloads();

        // All history items (including failed) are now retained for 1 hour via ArrMonitoringService cleanup
    }

    private async Task RemoveFailedHistoryItemAfterDelay(Guid id, TimeSpan delay)
    {
        try
        {
            Log.Information("[QueueItemProcessor] Scheduling auto-removal of failed item {Id} in {Minutes} minutes", id, delay.TotalMinutes);
            await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);

            using var scope = scopeFactory.CreateScope();
            var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
            Log.Information("[QueueItemProcessor] Auto-removing failed item {Id}", id);
            
            // Remove the item
            await dbClient.RemoveHistoryItemsAsync([id], true, CancellationToken.None).ConfigureAwait(false);
            await dbClient.SaveChanges(CancellationToken.None).ConfigureAwait(false);
            
            // Notify frontend
            _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, id.ToString());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[QueueItemProcessor] Failed to auto-remove history item {Id}", id);
        }
    }

    private async Task RefreshMonitoredDownloads()
    {
        var tasks = configManager
            .GetArrConfig()
            .GetArrClients()
            .Select(RefreshMonitoredDownloads);
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task RefreshMonitoredDownloads(ArrClient arrClient)
    {
        try
        {
            var downloadClients = await arrClient.GetDownloadClientsAsync().ConfigureAwait(false);
            if (downloadClients.All(x => x.Category != queueItem.Category)) return;
            var queueCount = await arrClient.GetQueueCountAsync().ConfigureAwait(false);
            if (queueCount < 300) await arrClient.RefreshMonitoredDownloads().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Debug("Could not refresh monitored downloads for Arr instance: {ArrHost}. {Message}", arrClient.Host, e.Message);
        }
    }

    private static (string[] SegmentIds, string FileName) GetResultSegmentIdsAndName(BaseProcessor.Result result) => result switch
    {
        FileProcessor.Result fp => (fp.NzbFile.GetSegmentIds(), fp.FileName),
        RarProcessor.Result rp => (rp.StoredFileSegments.SelectMany(s => s.NzbFile.GetSegmentIds()).ToArray(),
            rp.StoredFileSegments.FirstOrDefault()?.ArchiveName ?? "unknown.rar"),
        SevenZipProcessor.Result sz => (sz.SevenZipFiles.SelectMany(f => f.DavMultipartFileMeta.FileParts.SelectMany(p => p.SegmentIds)).ToArray(),
            sz.SevenZipFiles.FirstOrDefault()?.PathWithinArchive ?? "unknown.7z"),
        MultipartMkvProcessor.Result mkv => (mkv.Parts.SelectMany(p => p.SegmentIds).ToArray(), mkv.Filename),
        _ => ([], "unknown")
    };
}