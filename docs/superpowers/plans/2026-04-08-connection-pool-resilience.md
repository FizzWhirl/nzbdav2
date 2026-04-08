# Connection Pool Resilience Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent NNTP connection pool death spirals caused by DMCA'd content retry loops across nzbdav2 and UsenetStreamer.

**Architecture:** Five independent improvements — two in nzbdav2 (C#/.NET) adding a capped analysis connection type and DMCA fast-fail, three in UsenetStreamer (Node.js) adding per-episode attempt limits, NZB submission rate-limiting, and ECONNRESET backoff in triage. Each improvement can be deployed independently and rolled back via env vars.

**Tech Stack:** C# .NET 10 (nzbdav2), Node.js (UsenetStreamer), SQLite, NNTP

**Spec:** `docs/superpowers/specs/2026-04-08-connection-pool-resilience-design.md`

---

## File Map

### nzbdav2 (C# backend)

| Action | File | Responsibility |
|--------|------|----------------|
| Modify | `backend/Clients/Usenet/Connections/ConnectionUsageContext.cs:139-148` | Add `QueueAnalysis = 7` to enum |
| Modify | `backend/Clients/Usenet/Connections/GlobalOperationLimiter.cs` | Add `_queueAnalysisSemaphore`, route `QueueAnalysis` |
| Modify | `backend/Queue/QueueItemProcessor.cs:164-234` | Switch to `QueueAnalysis` context during analysis |
| Modify | `backend/Clients/Usenet/UsenetStreamingClient.cs:69-151` | DMCA confirmation check in `AnalyzeNzbAsync` |
| Modify | `backend/Streams/NzbFileStream.cs:262-263` | Exclude `QueueAnalysis` from BufferedStreaming |
| Modify | `backend/Services/NzbProviderAffinityService.cs:236-242` | Add `QueueAnalysis` to `ShouldDeferToStreaming` |

### UsenetStreamer (Node.js)

| Action | File | Responsibility |
|--------|------|----------------|
| Modify | `/Users/dgherman/Documents/projects/UsenetStreamer/src/cache/nzbdavCache.js` | Episode attempt counter |
| Modify | `/Users/dgherman/Documents/projects/UsenetStreamer/src/services/nzbdav.js` | Submission semaphore + episode limit integration |
| Modify | `/Users/dgherman/Documents/projects/UsenetStreamer/src/services/triage/index.js` | ECONNRESET backoff in `runWithClient` |
| Modify | `/Users/dgherman/Documents/projects/UsenetStreamer/server.js:4005-4126` | Pass episode key, reset counter on success |

---

## Task 1: Add `QueueAnalysis` Connection Type (nzbdav2)

**Files:**
- Modify: `backend/Clients/Usenet/Connections/ConnectionUsageContext.cs:139-148`
- Modify: `backend/Clients/Usenet/Connections/GlobalOperationLimiter.cs:17-58,184-195,225-254,257-261`
- Modify: `backend/Streams/NzbFileStream.cs:262-263`
- Modify: `backend/Services/NzbProviderAffinityService.cs:236-242`

- [ ] **Step 1: Add `QueueAnalysis` to the `ConnectionUsageType` enum**

In `backend/Clients/Usenet/Connections/ConnectionUsageContext.cs`, add the new enum value:

```csharp
public enum ConnectionUsageType
{
    Unknown = 0,
    Queue = 1,
    Streaming = 2,
    HealthCheck = 3,
    Repair = 4,
    BufferedStreaming = 5,
    Analysis = 6,
    QueueAnalysis = 7
}
```

- [ ] **Step 2: Add `_queueAnalysisSemaphore` to `GlobalOperationLimiter`**

In `backend/Clients/Usenet/Connections/GlobalOperationLimiter.cs`:

Add the field alongside the existing semaphores (after line 19):

```csharp
private readonly SemaphoreSlim _queueAnalysisSemaphore;
```

In the constructor, after `maxQueueConnections = Math.Max(1, maxQueueConnections);` (line 31), compute the analysis limit. Check the `QUEUE_ANALYSIS_MAX_CONNECTIONS` env var first, then fall back to half of queue connections:

```csharp
var envQueueAnalysis = Environment.GetEnvironmentVariable("QUEUE_ANALYSIS_MAX_CONNECTIONS");
var maxQueueAnalysisConnections = int.TryParse(envQueueAnalysis, out var parsedQA) && parsedQA > 0
    ? parsedQA
    : Math.Max(2, maxQueueConnections / 2);
```

Add to `_guaranteedLimits` dictionary (after the existing `Analysis` entry at line 43):

```csharp
{ ConnectionUsageType.QueueAnalysis, maxQueueAnalysisConnections },
```

Add to `_currentUsage` initialization — this happens automatically since the foreach iterates `_guaranteedLimits.Keys`.

Create the semaphore (after line 56):

```csharp
_queueAnalysisSemaphore = new SemaphoreSlim(maxQueueAnalysisConnections, maxQueueAnalysisConnections);
```

Update the constructor log comment (line 58) to include the new value:

```csharp
// Serilog.Log.Information($"[GlobalOperationLimiter] Initialized: Queue={maxQueueConnections}, QueueAnalysis={maxQueueAnalysisConnections}, HealthCheck={maxHealthCheckConnections}, Streaming={maxStreamingConnections}, Total={totalConnections}");
```

- [ ] **Step 3: Route `QueueAnalysis` in `GetSemaphoreForType`**

In `GetSemaphoreForType` (line 184-195), add the new case:

```csharp
private SemaphoreSlim GetSemaphoreForType(ConnectionUsageType type)
{
    return type switch
    {
        ConnectionUsageType.Queue => _queueSemaphore,
        ConnectionUsageType.QueueAnalysis => _queueAnalysisSemaphore,
        ConnectionUsageType.HealthCheck => _healthCheckSemaphore,
        ConnectionUsageType.Repair => _healthCheckSemaphore,
        ConnectionUsageType.Analysis => _healthCheckSemaphore,
        ConnectionUsageType.Streaming => _streamingSemaphore,
        ConnectionUsageType.BufferedStreaming => _streamingSemaphore,
        _ => _streamingSemaphore
    };
}
```

- [ ] **Step 4: Update `LogInfoForType` and `GetComponentForType`**

In `LogInfoForType` (line 225-241), add `QueueAnalysis` to the debug-level log filter:

```csharp
if (usageType == ConnectionUsageType.HealthCheck || 
    usageType == ConnectionUsageType.Repair || 
    usageType == ConnectionUsageType.Analysis ||
    usageType == ConnectionUsageType.QueueAnalysis ||
    usageType == ConnectionUsageType.Streaming ||
    usageType == ConnectionUsageType.BufferedStreaming)
```

In `GetComponentForType` (line 243-255), add:

```csharp
ConnectionUsageType.QueueAnalysis => LogComponents.Queue,
```

- [ ] **Step 5: Dispose the new semaphore**

In `Dispose()` (line 257-261), add:

```csharp
_queueAnalysisSemaphore.Dispose();
```

- [ ] **Step 6: Exclude `QueueAnalysis` from BufferedStreaming in `NzbFileStream`**

In `backend/Streams/NzbFileStream.cs` line 262-263, update the check:

```csharp
var shouldUseBufferedStreaming = _useBufferedStreaming &&
    _usageContext.UsageType != ConnectionUsageType.Queue &&
    _usageContext.UsageType != ConnectionUsageType.QueueAnalysis;
```

- [ ] **Step 7: Add `QueueAnalysis` to `ShouldDeferToStreaming` in `NzbProviderAffinityService`**

In `backend/Services/NzbProviderAffinityService.cs` line 236-242:

```csharp
private static bool ShouldDeferToStreaming(ConnectionUsageType usageType)
{
    return usageType is ConnectionUsageType.Queue
        or ConnectionUsageType.QueueAnalysis
        or ConnectionUsageType.HealthCheck
        or ConnectionUsageType.Analysis;
}
```

- [ ] **Step 8: Build and verify**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2 && /opt/homebrew/opt/dotnet/bin/dotnet build backend/
```

Expected: Build succeeds with no errors. Any warnings about unused `QueueAnalysis` are fine — Task 2 wires it up.

- [ ] **Step 9: Commit**

```bash
git add backend/Clients/Usenet/Connections/ConnectionUsageContext.cs \
       backend/Clients/Usenet/Connections/GlobalOperationLimiter.cs \
       backend/Streams/NzbFileStream.cs \
       backend/Services/NzbProviderAffinityService.cs
git commit -m "feat: add QueueAnalysis connection type with capped semaphore"
```

---

## Task 2: Wire `QueueAnalysis` Context in QueueItemProcessor (nzbdav2)

**Files:**
- Modify: `backend/Queue/QueueItemProcessor.cs:164-234`

- [ ] **Step 1: Switch to `QueueAnalysis` before analysis, back to `Queue` after**

In `backend/Queue/QueueItemProcessor.cs`, the analysis happens at lines 228-246 (the `GetFileSizesBatchAsync` call). The context is set at line 169.

We need to swap the context before analysis and restore it after. The current code at line 167-170:

```csharp
// Create a linked token for context propagation (more robust than setting on existing token)
using var queueCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
using var _1 = queueCts.Token.SetScopedContext(new ConnectionUsageContext(ConnectionUsageType.Queue, queueItem.JobName));
var queueCt = queueCts.Token;
```

The analysis that needs the capped context is `GetFileSizesBatchAsync` at line 234. This is the only call that does full segment analysis during queue processing. The earlier `FetchFirstSegments` (line 209) and `GetPar2FileDescriptors` (line 215) are lightweight — they only fetch 1-2 segments per file.

Replace the single context with a swap around the `GetFileSizesBatchAsync` call. Find the block at lines 228-246:

```csharp
// step 1b -- batch fetch file sizes for files without Par2 descriptors
var filesWithoutSize = fileInfos.Where(f => f.FileSize == null).Select(f => f.NzbFile).ToList();
if (filesWithoutSize.Count > 0)
{
    Log.Debug("[QueueItemProcessor] Step 1d: Fetching file sizes for {FileCount} files without Par2 descriptors in {JobName}...",
        filesWithoutSize.Count, queueItem.JobName);
    var fileSizeStartTime = DateTime.UtcNow;
    var fileSizes = await usenetClient.GetFileSizesBatchAsync(filesWithoutSize, concurrency, queueCt).ConfigureAwait(false);
```

Replace with:

```csharp
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
```

No changes needed after the block — the `using` disposes automatically and the parent `queueCt` still carries `ConnectionUsageType.Queue` for all subsequent file processing.

- [ ] **Step 2: Build and verify**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2 && /opt/homebrew/opt/dotnet/bin/dotnet build backend/
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add backend/Queue/QueueItemProcessor.cs
git commit -m "feat: use QueueAnalysis context for file size analysis in QueueItemProcessor"
```

---

## Task 3: DMCA Fast-Fail in `AnalyzeNzbAsync` (nzbdav2)

**Files:**
- Modify: `backend/Clients/Usenet/UsenetStreamingClient.cs:69-151`

- [ ] **Step 1: Add DMCA detection helper method**

Add a private helper method to `UsenetStreamingClient` (before `AnalyzeNzbAsync`, around line 68):

```csharp
/// <summary>
/// Checks if an exception indicates article-not-found (DMCA/takedown signal).
/// Matches UsenetArticleNotFoundException or any exception chain containing "430" or "not found".
/// </summary>
private static bool IsArticleNotFoundException(Exception ex)
{
    // Direct match
    if (ex is UsenetArticleNotFoundException) return true;

    // Check inner exceptions
    var inner = ex.InnerException;
    while (inner != null)
    {
        if (inner is UsenetArticleNotFoundException) return true;
        inner = inner.InnerException;
    }

    // Check for NNTP 430 status in message (some NNTP libs wrap the code in a generic exception)
    var message = ex.Message ?? "";
    return message.Contains("430") || message.Contains("no such article", StringComparison.OrdinalIgnoreCase);
}
```

You will need to add `using NzbWebDAV.Exceptions;` at the top of the file if not already present. Check — it is already there at line 7.

- [ ] **Step 2: Add DMCA confirmation check to the Smart Analysis catch block**

In `AnalyzeNzbAsync`, replace the catch block at lines 117-120:

```csharp
catch (Exception ex)
{
    Serilog.Log.Warning(ex, "[UsenetStreamingClient] Smart Analysis failed/skipped. Falling back to full scan.");
}
```

With:

```csharp
catch (Exception ex)
{
    // Check if this looks like a DMCA/takedown (article not found)
    if (IsArticleNotFoundException(ex) && segmentIds.Length > 3)
    {
        Serilog.Log.Warning("[UsenetStreamingClient] Smart Analysis failed with article-not-found. Running DMCA confirmation check...");

        // Confirmation: try one segment from the middle of the NZB
        var midIndex = segmentIds.Length / 2;
        try
        {
            using var confirmCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            confirmCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var _ = confirmCts.Token.SetScopedContext(usageContext);

            await _client.GetSegmentYencHeaderAsync(segmentIds[midIndex], confirmCts.Token).ConfigureAwait(false);

            // Middle segment succeeded — not a full DMCA, proceed with full scan
            Serilog.Log.Warning("[UsenetStreamingClient] DMCA confirmation check PASSED (mid-segment exists). Falling back to full scan.");
        }
        catch (Exception confirmEx) when (IsArticleNotFoundException(confirmEx))
        {
            // Confirmed: first/last AND middle segments are missing — DMCA/takedown pattern
            Serilog.Log.Warning("[UsenetStreamingClient] DMCA/takedown pattern confirmed: first, last, and mid segments all missing. Failing fast.");
            throw new NonRetryableDownloadException(
                $"DMCA/takedown pattern detected: multiple segments across NZB are missing (first, mid={midIndex}, last). " +
                $"Skipping full scan of {segmentIds.Length} segments.");
        }
        catch (Exception confirmEx)
        {
            // Confirmation check failed with a non-article error (timeout, connection issue)
            // Proceed with full scan — might be a transient network problem
            Serilog.Log.Warning(confirmEx, "[UsenetStreamingClient] DMCA confirmation check failed with non-article error. Falling back to full scan.");
        }
    }
    else
    {
        // Not article-not-found (timeout, connection reset, etc.) — proceed with full scan
        Serilog.Log.Warning(ex, "[UsenetStreamingClient] Smart Analysis failed/skipped. Falling back to full scan.");
    }
}
```

- [ ] **Step 3: Build and verify**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2 && /opt/homebrew/opt/dotnet/bin/dotnet build backend/
```

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add backend/Clients/Usenet/UsenetStreamingClient.cs
git commit -m "feat: DMCA fast-fail — confirmation check before full scan in AnalyzeNzbAsync"
```

---

## Task 4: Per-Episode Attempt Limit (UsenetStreamer)

**Files:**
- Modify: `/Users/dgherman/Documents/projects/UsenetStreamer/src/cache/nzbdavCache.js`
- Modify: `/Users/dgherman/Documents/projects/UsenetStreamer/src/services/nzbdav.js:703-758`
- Modify: `/Users/dgherman/Documents/projects/UsenetStreamer/server.js:4005-4126`

- [ ] **Step 1: Add episode attempt tracking to `nzbdavCache.js`**

In `/Users/dgherman/Documents/projects/UsenetStreamer/src/cache/nzbdavCache.js`, add after the negative cache variables (after line 10):

```javascript
// Episode-level attempt tracking — prevents retrying the same episode with dozens of NZBs
const episodeAttemptCache = new Map();
let EPISODE_ATTEMPT_TTL_MS = 6 * 60 * 60 * 1000; // 6 hours default
let MAX_EPISODE_ATTEMPTS = 5;
```

In the `reloadNzbdavCacheConfig()` function (after the negative cache TTL block, around line 27), add:

```javascript
  // Episode attempt limit
  const maxAttempts = Number(process.env.EPISODE_MAX_ATTEMPTS);
  if (Number.isFinite(maxAttempts) && maxAttempts > 0) {
    MAX_EPISODE_ATTEMPTS = maxAttempts;
  } else {
    MAX_EPISODE_ATTEMPTS = 5;
  }

  const attemptTtlHours = Number(process.env.EPISODE_ATTEMPT_TTL_HOURS);
  if (Number.isFinite(attemptTtlHours) && attemptTtlHours > 0) {
    EPISODE_ATTEMPT_TTL_MS = attemptTtlHours * 60 * 60 * 1000;
  } else {
    EPISODE_ATTEMPT_TTL_MS = 6 * 60 * 60 * 1000;
  }
```

Add these functions before the `module.exports` block:

```javascript
// --- Episode attempt tracking ---

function cleanupEpisodeAttempts() {
  const now = Date.now();
  for (const [key, entry] of episodeAttemptCache.entries()) {
    if (entry.expiresAt && entry.expiresAt <= now) {
      episodeAttemptCache.delete(key);
    }
  }
}

function checkEpisodeAttemptLimit(episodeKey) {
  cleanupEpisodeAttempts();
  const entry = episodeAttemptCache.get(episodeKey);
  if (!entry || (entry.expiresAt && entry.expiresAt <= Date.now())) {
    episodeAttemptCache.delete(episodeKey);
    return { allowed: true, attempts: 0, maxAttempts: MAX_EPISODE_ATTEMPTS };
  }
  return {
    allowed: entry.attempts < MAX_EPISODE_ATTEMPTS,
    attempts: entry.attempts,
    maxAttempts: MAX_EPISODE_ATTEMPTS,
  };
}

function incrementEpisodeAttempts(episodeKey) {
  cleanupEpisodeAttempts();
  const existing = episodeAttemptCache.get(episodeKey);
  const attempts = existing ? existing.attempts + 1 : 1;
  episodeAttemptCache.set(episodeKey, {
    attempts,
    firstAttemptAt: existing?.firstAttemptAt || Date.now(),
    lastAttemptAt: Date.now(),
    expiresAt: Date.now() + EPISODE_ATTEMPT_TTL_MS,
  });
  if (attempts >= MAX_EPISODE_ATTEMPTS) {
    console.log(`[EPISODE LIMIT] Episode ${episodeKey} reached attempt limit (${attempts}/${MAX_EPISODE_ATTEMPTS})`);
  }
  return attempts;
}

function resetEpisodeAttempts(episodeKey) {
  if (episodeAttemptCache.has(episodeKey)) {
    episodeAttemptCache.delete(episodeKey);
    console.log(`[EPISODE LIMIT] Reset attempts for ${episodeKey} (successful stream)`);
  }
}

function clearAllEpisodeAttempts(reason = 'manual') {
  if (episodeAttemptCache.size > 0) {
    console.log('[EPISODE LIMIT] Cleared all episode attempt counters', { reason, entries: episodeAttemptCache.size });
  }
  episodeAttemptCache.clear();
}

function getEpisodeAttemptStats() {
  cleanupEpisodeAttempts();
  const entries = [];
  for (const [key, entry] of episodeAttemptCache.entries()) {
    entries.push({ episodeKey: key, ...entry });
  }
  return {
    entries,
    count: episodeAttemptCache.size,
    maxAttempts: MAX_EPISODE_ATTEMPTS,
    ttlMs: EPISODE_ATTEMPT_TTL_MS,
  };
}
```

Add the new functions to `module.exports`:

```javascript
module.exports = {
  cleanupNzbdavCache,
  clearNzbdavStreamCache,
  clearNzbdavStreamCacheEntry,
  getOrCreateNzbdavStream,
  buildNzbdavCacheKey,
  // Negative cache exports
  isDownloadUrlFailed,
  markDownloadUrlFailed,
  clearFailedDownloadUrl,
  clearAllFailedDownloadUrls,
  getNegativeCacheStats,
  getNegativeCacheEntries,
  getNzbdavCacheStats,
  reloadNzbdavCacheConfig,
  // Episode attempt tracking exports
  checkEpisodeAttemptLimit,
  incrementEpisodeAttempts,
  resetEpisodeAttempts,
  clearAllEpisodeAttempts,
  getEpisodeAttemptStats,
};
```

- [ ] **Step 2: Integrate episode limit in `buildNzbdavStream`**

In `/Users/dgherman/Documents/projects/UsenetStreamer/src/services/nzbdav.js`, the `buildNzbdavStream` function at line 703 receives `{ downloadUrl, category, title, requestedEpisode, existingSlot, inlineCachedEntry }`.

Add `episodeKey` parameter to the function signature and check the limit before the `addNzbToNzbdav` call. Modify `buildNzbdavStream` at line 703:

```javascript
async function buildNzbdavStream({ downloadUrl, category, title, requestedEpisode, existingSlot = null, inlineCachedEntry = null, episodeKey = null }) {
```

Inside the `else` block at line 738 (where new downloads are queued), before the `addNzbToNzbdav` call at line 744, add:

```javascript
          // Check episode attempt limit before submitting new NZB
          if (episodeKey) {
            const limitCheck = cache.checkEpisodeAttemptLimit(episodeKey);
            if (!limitCheck.allowed) {
              const limitError = new Error(`[NZBDAV] Episode attempt limit reached (${limitCheck.attempts}/${limitCheck.maxAttempts}) for ${episodeKey}`);
              limitError.isNzbdavFailure = true;
              limitError.failureMessage = `Episode attempt limit reached (${limitCheck.attempts}/${limitCheck.maxAttempts})`;
              throw limitError;
            }
            cache.incrementEpisodeAttempts(episodeKey);
          }
```

- [ ] **Step 3: Pass `episodeKey` from `handleNzbdavStream` and reset on success**

In `/Users/dgherman/Documents/projects/UsenetStreamer/server.js`, in `handleNzbdavStream`, build the episode key after `parseRequestedEpisode` (after line 4007):

```javascript
    const episodeKey = `${type}:${id}`;
```

Pass it to both `buildNzbdavStream` calls. At line 4065-4073:

```javascript
    let streamData = await cache.getOrCreateNzbdavStream(cacheKey, () =>
      nzbdavService.buildNzbdavStream({
        downloadUrl,
        category,
        title,
        requestedEpisode,
        existingSlot: existingSlotHint,
        inlineCachedEntry: inlineEasynewsEntry,
        episodeKey,
      })
    );
```

And the retry call at line 4088-4095:

```javascript
        streamData = await nzbdavService.buildNzbdavStream({
          downloadUrl,
          category,
          title,
          requestedEpisode,
          existingSlot: existingSlotHint,
          inlineCachedEntry: inlineEasynewsEntry,
          episodeKey,
        });
```

Reset the counter on successful proxy. After the `proxyNzbdavStream` call at line 4126, add:

```javascript
    await nzbdavService.proxyNzbdavStream(req, res, streamData.viewPath, streamData.fileName || '');
    // Successful stream — reset episode attempt counter
    if (episodeKey) {
      cache.resetEpisodeAttempts(episodeKey);
    }
```

- [ ] **Step 4: Verify UsenetStreamer starts**

```bash
cd /Users/dgherman/Documents/projects/UsenetStreamer && node -e "require('./src/cache/nzbdavCache'); console.log('OK')"
```

Expected: `OK` — no syntax errors.

- [ ] **Step 5: Commit**

```bash
cd /Users/dgherman/Documents/projects/UsenetStreamer
git add src/cache/nzbdavCache.js src/services/nzbdav.js server.js
git commit -m "feat: per-episode attempt limit to prevent DMCA retry storms"
```

---

## Task 5: Rate-Limit NZB Submissions (UsenetStreamer)

**Files:**
- Modify: `/Users/dgherman/Documents/projects/UsenetStreamer/src/services/nzbdav.js:136-299`

- [ ] **Step 1: Add async semaphore at module level**

In `/Users/dgherman/Documents/projects/UsenetStreamer/src/services/nzbdav.js`, find where module-level constants are defined (near the top, after the `require` statements). Add:

```javascript
// Submission concurrency limiter — prevents flooding nzbdav2's queue
const SUBMISSION_MAX_CONCURRENT = Number(process.env.NZBDAV_MAX_CONCURRENT_SUBMISSIONS) || 2;
const SUBMISSION_TIMEOUT_MS = 120000; // 120s max wait for a submission slot
let submissionActiveCount = 0;
const submissionWaiters = []; // FIFO queue of { resolve, reject, timer }
```

Add the semaphore functions:

```javascript
function acquireSubmissionSlot() {
  if (submissionActiveCount < SUBMISSION_MAX_CONCURRENT) {
    submissionActiveCount++;
    return Promise.resolve();
  }
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      const idx = submissionWaiters.findIndex(w => w.resolve === resolve);
      if (idx !== -1) submissionWaiters.splice(idx, 1);
      reject(new Error(`[NZBDAV] Submission queue timeout after ${SUBMISSION_TIMEOUT_MS / 1000}s (${submissionActiveCount} active, ${submissionWaiters.length} waiting)`));
    }, SUBMISSION_TIMEOUT_MS);
    submissionWaiters.push({ resolve, reject, timer });
  });
}

function releaseSubmissionSlot() {
  if (submissionWaiters.length > 0) {
    const next = submissionWaiters.shift();
    clearTimeout(next.timer);
    next.resolve();
    // Don't decrement — the slot transfers to the next waiter
  } else {
    submissionActiveCount = Math.max(0, submissionActiveCount - 1);
  }
}
```

- [ ] **Step 2: Wrap `addNzbToNzbdav` core logic with the semaphore**

In the `addNzbToNzbdav` function (line 136), wrap the body in the semaphore. Change:

```javascript
async function addNzbToNzbdav({ downloadUrl, cachedEntry = null, category, jobLabel }) {
  ensureNzbdavConfigured();
```

To:

```javascript
async function addNzbToNzbdav({ downloadUrl, cachedEntry = null, category, jobLabel }) {
  ensureNzbdavConfigured();

  await acquireSubmissionSlot();
  try {
    return await _addNzbToNzbdavInner({ downloadUrl, cachedEntry, category, jobLabel });
  } finally {
    releaseSubmissionSlot();
  }
}

async function _addNzbToNzbdavInner({ downloadUrl, cachedEntry = null, category, jobLabel }) {
```

Then find the closing brace of the original `addNzbToNzbdav` function (line 299 — after the final `return { nzoId };`). That closing brace now closes `_addNzbToNzbdavInner`.

All existing code between the original function body and its closing brace stays inside `_addNzbToNzbdavInner` unchanged.

- [ ] **Step 3: Add log for submission queue state**

Inside `acquireSubmissionSlot`, add a log when queuing:

```javascript
function acquireSubmissionSlot() {
  if (submissionActiveCount < SUBMISSION_MAX_CONCURRENT) {
    submissionActiveCount++;
    return Promise.resolve();
  }
  console.log(`[NZBDAV] Submission slot full (${submissionActiveCount}/${SUBMISSION_MAX_CONCURRENT}), queuing request (${submissionWaiters.length + 1} waiting)`);
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      const idx = submissionWaiters.findIndex(w => w.resolve === resolve);
      if (idx !== -1) submissionWaiters.splice(idx, 1);
      reject(new Error(`[NZBDAV] Submission queue timeout after ${SUBMISSION_TIMEOUT_MS / 1000}s (${submissionActiveCount} active, ${submissionWaiters.length} waiting)`));
    }, SUBMISSION_TIMEOUT_MS);
    submissionWaiters.push({ resolve, reject, timer });
  });
}
```

- [ ] **Step 4: Verify syntax**

```bash
cd /Users/dgherman/Documents/projects/UsenetStreamer && node -e "require('./src/services/nzbdav'); console.log('OK')"
```

Expected: `OK`

- [ ] **Step 5: Commit**

```bash
cd /Users/dgherman/Documents/projects/UsenetStreamer
git add src/services/nzbdav.js
git commit -m "feat: rate-limit concurrent NZB submissions to nzbdav2"
```

---

## Task 6: ECONNRESET Backoff in Triage (UsenetStreamer)

**Files:**
- Modify: `/Users/dgherman/Documents/projects/UsenetStreamer/src/services/triage/index.js:2521-2539`

- [ ] **Step 1: Add backoff tracker at module level**

In `/Users/dgherman/Documents/projects/UsenetStreamer/src/services/triage/index.js`, add near the top of the file (after the existing module-level variables — find a good spot after the `require` statements and config parsing):

```javascript
// Per-pool ECONNRESET backoff tracker
const poolBackoff = new Map(); // poolId → { until, consecutiveResets, lastErrorAt }
const BACKOFF_BASE_MS = 2000;
const BACKOFF_MAX_MS = 30000;
const BACKOFF_DECAY_MS = 60000; // Reset counter after 60s of no errors
```

- [ ] **Step 2: Add backoff helper functions**

Add these functions near the `runWithClient` function (around line 2520):

```javascript
function getPoolId(pool) {
  // Use pool's identity — each pool has a unique object reference
  if (!pool._backoffId) {
    pool._backoffId = `pool_${Date.now()}_${Math.random().toString(36).slice(2, 8)}`;
  }
  return pool._backoffId;
}

async function waitForBackoff(pool) {
  const id = getPoolId(pool);
  const entry = poolBackoff.get(id);
  if (!entry) return;

  // Decay: if no errors for BACKOFF_DECAY_MS, reset
  if (Date.now() - entry.lastErrorAt > BACKOFF_DECAY_MS) {
    poolBackoff.delete(id);
    return;
  }

  const remaining = entry.until - Date.now();
  if (remaining > 0) {
    timingLog('nntp-backoff:waiting', { poolId: id, waitMs: remaining, consecutiveResets: entry.consecutiveResets });
    await new Promise(resolve => setTimeout(resolve, remaining));
  }
}

function recordPoolError(pool) {
  const id = getPoolId(pool);
  const existing = poolBackoff.get(id);
  const consecutiveResets = existing ? existing.consecutiveResets + 1 : 1;
  const backoffMs = Math.min(BACKOFF_BASE_MS * Math.pow(2, consecutiveResets - 1), BACKOFF_MAX_MS);
  poolBackoff.set(id, {
    until: Date.now() + backoffMs,
    consecutiveResets,
    lastErrorAt: Date.now(),
  });
  timingLog('nntp-backoff:recorded', { poolId: id, backoffMs, consecutiveResets });
}

function recordPoolSuccess(pool) {
  const id = getPoolId(pool);
  if (poolBackoff.has(id)) {
    poolBackoff.delete(id);
  }
}

const CONNECTION_ERROR_CODES = new Set(['ETIMEDOUT', 'ECONNRESET', 'ECONNABORTED', 'EPIPE']);
```

- [ ] **Step 3: Integrate backoff into `runWithClient`**

Replace the `runWithClient` function at line 2521-2539:

```javascript
async function runWithClient(pool, handler) {
  if (!pool) throw new Error('NNTP pool unavailable');

  // Wait for any active backoff before acquiring a client
  await waitForBackoff(pool);

  const acquireStart = Date.now();
  const client = await pool.acquire();
  timingLog('nntp-client:acquired', {
    waitDurationMs: Date.now() - acquireStart,
  });
  if (currentMetrics) currentMetrics.clientAcquisitions += 1;
  if (!client) throw new Error('NNTP client unavailable');
  let dropClient = false;
  try {
    const result = await handler(client);
    recordPoolSuccess(pool);
    return result;
  } catch (err) {
    if (err?.dropClient) dropClient = true;
    // Track connection-level errors for backoff
    if (CONNECTION_ERROR_CODES.has(err?.code)) {
      recordPoolError(pool);
    }
    throw err;
  } finally {
    pool.release(client, { drop: dropClient });
  }
}
```

- [ ] **Step 4: Verify syntax**

```bash
cd /Users/dgherman/Documents/projects/UsenetStreamer && node -e "require('./src/services/triage/index'); console.log('OK')"
```

Expected: `OK` (or may need the module's dependencies — in that case, just check syntax):

```bash
cd /Users/dgherman/Documents/projects/UsenetStreamer && node --check src/services/triage/index.js
```

Expected: No output (clean parse).

- [ ] **Step 5: Commit**

```bash
cd /Users/dgherman/Documents/projects/UsenetStreamer
git add src/services/triage/index.js
git commit -m "feat: exponential backoff on ECONNRESET in triage NNTP pool"
```

---

## Task 7: Final Build Verification

- [ ] **Step 1: Full nzbdav2 build**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2 && /opt/homebrew/opt/dotnet/bin/dotnet build backend/
```

Expected: Build succeeded.

- [ ] **Step 2: UsenetStreamer syntax check**

```bash
cd /Users/dgherman/Documents/projects/UsenetStreamer && node --check server.js && node --check src/services/nzbdav.js && node --check src/services/triage/index.js && node --check src/cache/nzbdavCache.js
```

Expected: No output from any check (all clean).

- [ ] **Step 3: Review all changes**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2 && git log --oneline -5
cd /Users/dgherman/Documents/projects/UsenetStreamer && git log --oneline -5
```

Verify 3 commits in nzbdav2 (Tasks 1-3) and 3 commits in UsenetStreamer (Tasks 4-6).
