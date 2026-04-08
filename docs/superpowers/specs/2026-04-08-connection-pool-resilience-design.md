# Connection Pool Resilience — Design Spec

Prevent NNTP connection pool death spirals caused by DMCA'd content retry loops.

**Date:** 2026-04-08
**Incident:** UsenetStreamer endlessly cycling through DMCA'd Fresh Off the Boat NZBs, each spawning a QueueItemProcessor that burned connections and starved real playback streams.
**Repos affected:** nzbdav2 (C# backend), UsenetStreamer (Node.js)

---

## Problem Statement

When usenetstreamer encounters a DMCA'd release, this sequence plays out:

1. usenetstreamer searches indexers, finds NZB candidates, picks one
2. Submits NZB to nzbdav2 via addfile/addurl
3. nzbdav2's QueueItemProcessor runs Smart Analysis (3 segments) — fails on DMCA'd content
4. Smart Analysis catch block falls back to **full scan** of ALL segments
5. Full scan burns Queue semaphore connections for minutes on content that will never work
6. Meanwhile, usenetstreamer's stream handler times out waiting for the WebDAV mount
7. usenetstreamer tries the next fallback NZB candidate — also DMCA'd — repeat
8. Connection pool saturates, real playback streams starve or truncate
9. Truncated streams get negative-cached, blocking retries even after the loop stops

The core issue: there's no circuit breaker at any layer. nzbdav2 doesn't fail fast on DMCA patterns, and usenetstreamer doesn't limit how many times it retries the same episode or how fast it submits NZBs.

---

## Improvement 1: Isolated QueueProcessing Connection Type

**Repo:** nzbdav2
**Files:**
- `backend/Clients/Usenet/Connections/ConnectionUsageContext.cs` (enum)
- `backend/Clients/Usenet/Connections/GlobalOperationLimiter.cs` (semaphore routing)
- `backend/Queue/QueueItemProcessor.cs:169` (usage site)

### Current Behavior

`QueueItemProcessor` sets `ConnectionUsageType.Queue` on its cancellation token context (line 169). In `GlobalOperationLimiter`, `Queue` routes to `_queueSemaphore` — a dedicated semaphore separate from streaming. This seems fine in isolation.

However, `NzbFileStream` (line 262-263) has logic that disables `BufferedStreaming` for `Queue` usage type. If we later change the Queue semaphore size or if file processing paths evolve, the coupling between Queue and streaming semaphores could cause contention.

More importantly, the **analysis phase** (`AnalyzeNzbAsync`) runs under the same `Queue` context and burns connections during full-scan fallback. We need the analysis phase to have tighter limits than regular queue file processing.

### Design

Add a new `ConnectionUsageType.QueueAnalysis = 7` for the analysis phase specifically:

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
    QueueAnalysis = 7  // NEW — analysis during queue processing
}
```

In `GlobalOperationLimiter`:
- Add `_queueAnalysisSemaphore` with a **low limit** (e.g., `maxQueueConnections / 2`, minimum 2)
- Route `QueueAnalysis` to this new semaphore
- This caps how many concurrent connections the analysis phase can consume, even during full-scan fallback

In `QueueItemProcessor`:
- Before calling `AnalyzeNzbAsync`, switch the token context to `ConnectionUsageType.QueueAnalysis`
- After analysis completes (sizes are known), switch back to `ConnectionUsageType.Queue` for file processing

The semaphore limit should be configurable via environment variable `QUEUE_ANALYSIS_MAX_CONNECTIONS` with a sensible default (half of `MAX_QUEUE_CONNECTIONS`, minimum 2).

### Why This Helps

Even if a full scan runs on DMCA'd content, it can only consume a small fraction of the connection pool. Regular queue file processing and all streaming operations remain unaffected.

---

## Improvement 2: DMCA Fast-Fail in Analysis

**Repo:** nzbdav2
**File:** `backend/Clients/Usenet/UsenetStreamingClient.cs:69-151`

### Current Behavior

`AnalyzeNzbAsync` tries Smart Analysis (3 segments). If any exception occurs (line 117-120), it logs a warning and falls back to a **full scan** of every segment with concurrency. For DMCA'd content, every segment fails — so the full scan burns connections checking hundreds of segments that all return 430 (article not found) or throw `UsenetArticleNotFoundException`.

### Design

When Smart Analysis fails, run a **confirmation check** before committing to full scan:

1. Smart Analysis fails (catches exception at line 117)
2. Instead of immediately falling back, check whether the failure looks like a DMCA/takedown:
   - If the caught exception (or its inner exception) is `UsenetArticleNotFoundException`, `NntpArticleNotFoundException`, or contains status code 430 — this is a strong DMCA signal
   - Try **one more segment** from the middle of the array (`segmentIds[segmentIds.Length / 2]`) as confirmation
   - If that also throws an article-not-found exception: **throw `NonRetryableDownloadException`** with message indicating DMCA/takedown pattern detected
   - If the middle segment succeeds: proceed with full scan (the failure was a flaky first/last segment, not DMCA)
3. If the original Smart Analysis failure was NOT article-not-found (e.g., timeout, connection reset): proceed with full scan as before — those are transient network issues

This adds at most 1 extra segment check (~2-5 seconds) before deciding, but prevents multi-minute full scans on confirmed DMCA'd content.

The `NonRetryableDownloadException` will cause `QueueItemProcessor`'s generic catch block (line 119-136) to move the item to history as failed — exactly the right outcome.

### Exception Flow

```
Smart Analysis (3 segments) fails with article-not-found
  → Confirmation check: try segmentIds[len/2]
    → Also article-not-found → throw NonRetryableDownloadException("DMCA/takedown pattern")
    → Succeeds → proceed with full scan (was a flaky segment, not DMCA)

Smart Analysis fails with timeout/connection error
  → Proceed with full scan as before (transient issue)
```

---

## Improvement 3: Per-Episode Attempt Limit

**Repo:** UsenetStreamer
**File:** `src/cache/nzbdavCache.js`

### Current Behavior

The negative cache (`failedDownloadUrlCache`) is keyed by **download URL**. Each NZB candidate for the same episode has a different URL. So if Sonarr/Radarr triggers a stream request for "Fresh Off the Boat S01E01", usenetstreamer can try 25 different NZB candidates (from `triage/runner.js` `maxCandidates`), each getting its own negative cache entry when it fails. The next stream request starts the cycle again with any candidates not yet negative-cached.

There's no per-episode memory saying "we already tried 10 NZBs for this episode and they all failed."

### Design

Add an **episode attempt counter** alongside the existing negative cache:

```javascript
// Episode-level attempt tracking
const episodeAttemptCache = new Map();
const EPISODE_ATTEMPT_TTL_MS = 6 * 60 * 60 * 1000; // 6 hours
const MAX_EPISODE_ATTEMPTS = 5; // Max NZB submissions per episode before backing off
```

**Key format:** `${type}:${id}` (e.g., `movie:tt1234567` or `series:12345:S01E03`)

**Integration point:** `addNzbToNzbdav()` in `src/services/nzbdav.js`. Before submitting an NZB:

1. Build the episode key from the `type` + `id` parameters (already available in `handleNzbdavStream`)
2. Check `episodeAttemptCache.get(episodeKey)`
3. If attempts >= `MAX_EPISODE_ATTEMPTS` and TTL hasn't expired: **reject immediately** with a descriptive error that gets negative-cached
4. If under the limit: increment the counter and proceed with submission
5. On successful stream (WebDAV mount becomes available and streams without truncation): reset the counter for that episode

**New exports from nzbdavCache.js:**
- `checkEpisodeAttemptLimit(episodeKey)` — returns `{ allowed: bool, attempts: number, maxAttempts: number }`
- `incrementEpisodeAttempts(episodeKey)` — bumps the counter
- `resetEpisodeAttempts(episodeKey)` — called on successful stream
- `clearAllEpisodeAttempts(reason)` — for manual cache clearing
- `getEpisodeAttemptStats()` — for the cache stats endpoint

**Environment variables:**
- `EPISODE_MAX_ATTEMPTS` (default: 5)
- `EPISODE_ATTEMPT_TTL_HOURS` (default: 6)

### Why 5 Attempts / 6 Hours

5 attempts is generous enough to try different NZB sources (indexers, obfuscation variants) while capping the damage. 6 hours is long enough to prevent rapid retry storms but short enough that new uploads or provider changes get a fresh chance. Both are configurable.

---

## Improvement 4: Rate-Limit NZB Submissions

**Repo:** UsenetStreamer
**File:** `src/services/nzbdav.js` (in `addNzbToNzbdav()`)

### Current Behavior

`addNzbToNzbdav()` fires immediately on every call. If 10 stream requests arrive simultaneously (e.g., Stremio prefetch), 10 NZBs get submitted to nzbdav2 in rapid succession. Even though nzbdav2's QueueManager processes one at a time, the submissions pile up in the queue, and each eventually spawns a QueueItemProcessor that consumes connections.

### Design

Add a **submission semaphore** that limits concurrent in-flight NZB submissions:

```javascript
const MAX_CONCURRENT_SUBMISSIONS = 2;
let activeSubmissions = 0;
const submissionQueue = [];
```

**Implementation:** Wrap the core of `addNzbToNzbdav()` in a concurrency gate:

1. If `activeSubmissions >= MAX_CONCURRENT_SUBMISSIONS`: enqueue the request and wait (with a timeout of 120s)
2. When a slot opens: dequeue the next request and proceed
3. On completion (success or failure): decrement `activeSubmissions` and wake the next queued request

This is a simple async semaphore pattern — no external dependencies needed. The queue is FIFO to preserve request ordering.

**Environment variable:** `NZBDAV_MAX_CONCURRENT_SUBMISSIONS` (default: 2)

### Why 2 Concurrent

nzbdav2 processes queue items sequentially (QueueManager's `SemaphoreSlim(1,1)`), so submitting more than 2 at a time just builds a queue. Allowing 2 means one can be processing while the next is being uploaded — pipelining without pile-up.

---

## Improvement 5: ECONNRESET Backoff in Triage

**Repo:** UsenetStreamer
**File:** `src/services/triage/index.js`

### Current Behavior

`statSegmentWithClient` (line 2224) and `fetchSegmentBodyWithClient` (line 2270) detect ECONNRESET/ETIMEDOUT/ECONNABORTED/EPIPE and set `error.dropClient = true`, which removes the broken client from the pool. But **no backoff** is applied — the next triage worker immediately grabs another connection and retries, which can also ECONNRESET if the provider is having issues.

The triage system runs up to 8 concurrent workers (`runner.js:178`) with a 40-second budget. If a provider is resetting connections, all 8 workers burn through connections rapidly.

### Design

Add a **per-provider backoff tracker** in the triage module:

```javascript
const providerBackoff = new Map(); // provider key → { until: timestamp, consecutiveResets: number }
const BACKOFF_BASE_MS = 2000;      // 2s initial backoff
const BACKOFF_MAX_MS = 30000;      // 30s max backoff
const BACKOFF_RESET_MS = 60000;    // Reset counter after 60s of no errors
```

**Integration in `runWithClient`** (the function that wraps NNTP pool operations):

1. Before acquiring a client from the pool, check `providerBackoff` for the pool's provider
2. If `Date.now() < entry.until`: wait for the remaining backoff duration (or skip this provider if multiple are available)
3. On ECONNRESET/ETIMEDOUT/ECONNABORTED/EPIPE: increment `consecutiveResets` and set `until = Date.now() + min(BACKOFF_BASE_MS * 2^consecutiveResets, BACKOFF_MAX_MS)`
4. On successful operation: reset `consecutiveResets` to 0

**Exponential backoff sequence:** 2s, 4s, 8s, 16s, 30s (capped)

This prevents the triage system from hammering a struggling provider. The 40-second time budget naturally limits how long backoff can delay things — if a provider is consistently failing, the budget expires and triage moves on.

---

## What This Does NOT Change

- **QueueManager sequential processing**: Already processes one item at a time. No change needed.
- **Negative cache TTL/behavior**: The existing 24-hour negative cache is fine. The improvements above prevent bad entries from being created in the first place.
- **Connection pool circuit breaker**: The existing 5-failure/2s-cooldown circuit breaker in `ConnectionPool.cs` stays as-is. Our improvements operate at a higher level.
- **GlobalOperationLimiter streaming semaphore**: Streaming and BufferedStreaming continue to share `_streamingSemaphore`. No change needed — the issue is queue processing competing, not streaming competing with itself.
- **Fallback attempts in stream handler**: `handleNzbdavStream` keeps its 3-fallback limit. The per-episode attempt limit (Improvement 3) provides the cross-request cap that was missing.

---

## Testing Strategy

### nzbdav2 (Improvements 1-2)

1. **QueueAnalysis semaphore**: Submit an NZB with a known-good file. Verify in logs that analysis phase shows `QueueAnalysis` usage type, not `Queue`. Confirm file processing after analysis shows `Queue`.
2. **DMCA fast-fail**: Submit an NZB for known-DMCA'd content. Verify in logs:
   - Smart Analysis fails with article-not-found
   - Confirmation check runs (1 extra segment)
   - Item moves to history as failed with "DMCA/takedown" message
   - Full scan does NOT run (no "falling back to full scan" log after the confirmation)
3. **Non-DMCA fallback**: Submit an NZB where first segment times out but others are fine. Verify full scan still runs correctly.

### UsenetStreamer (Improvements 3-5)

4. **Episode attempt limit**: Trigger 6 stream requests for the same episode with failing NZBs. Verify the 6th request is rejected immediately with an attempt-limit error. Verify the cache stats endpoint reports the episode.
5. **Submission rate limit**: Send 5 simultaneous stream requests. Verify only 2 NZB submissions are in-flight at once (check nzbdav.js logs for timing).
6. **ECONNRESET backoff**: Simulate a provider returning ECONNRESET. Verify subsequent triage operations wait before retrying that provider (check triage logs for backoff messages).
7. **Integration**: Play a real stream while a queue item is processing. Verify playback is not degraded.

---

## Rollback

Each improvement is independent. If any causes issues:

- **Improvement 1**: Change `QueueAnalysis` back to `Queue` in `QueueItemProcessor.cs`. The extra enum value and semaphore are harmless if unused.
- **Improvement 2**: Remove the confirmation check in `AnalyzeNzbAsync`, restoring the direct full-scan fallback.
- **Improvement 3**: Set `EPISODE_MAX_ATTEMPTS=999` to effectively disable.
- **Improvement 4**: Set `NZBDAV_MAX_CONCURRENT_SUBMISSIONS=999` to effectively disable.
- **Improvement 5**: Set `BACKOFF_BASE_MS=0` or remove the backoff check in `runWithClient`.
