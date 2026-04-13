# Queue Processing Speed — Hybrid Pool + I/O Optimizations

**Date:** 2026-04-13
**Goal:** Reduce queue processing time from 7+ minutes to <20 seconds for a 60-part RAR NZB, matching or beating original nzbdav's <30 second performance.

## Problem

Queue processing in nzbdav2 is dramatically slower than original nzbdav due to four compounding bottlenecks:

1. **Hard-partitioned connection pool**: `GlobalOperationLimiter` uses separate semaphores per operation type. `MaxQueueConnections` defaults to 1, serializing all queue network operations even when no streaming is happening and 249 connections sit idle.

2. **Buffered streaming disabled for queue**: `NzbFileStream.cs:279-283` blanket-disables buffered streaming for Queue/QueueAnalysis contexts. RAR header parsing needs to seek to the end of each 524MB part (EndArchive header), triggering multiple lazy segment fetches — each gated behind the single queue permit.

3. **No article caching between steps**: Step 1 fetches the first segment of every file. Step 2's RarProcessor re-fetches the same segments from Usenet for header parsing. Original nzbdav caches decoded segments to temp files via `ArticleCachingNntpClient`, eliminating redundant network round-trips.

4. **Concurrency caps derived from MaxQueueConnections=1**: `FetchFirstSegmentsStep` and `QueueItemProcessor` both use `GetMaxQueueConnections()` for their concurrency limits, capping all parallel work to 1.

### Evidence

Debug log from GitHub issue #3 (user with 4 providers, 250 total connections):
- Step 1 (60 first-segment fetches at concurrency 1): 114 seconds
- Step 2 (60 RAR parts, "concurrency 3" but gated to 1 by GlobalPool): 335 seconds
- Total: 448 seconds (7.5 minutes)
- GlobalPool wait times: consistently 2-5 seconds per permit acquisition
- Every operation creates a new connection (idle connections expire between serialized operations)

Original nzbdav with the same NZB completes in <30 seconds using shared-pool priority scheduling at concurrency 20+.

## Fix 1: Hybrid Dynamic Borrowing — Replace GlobalOperationLimiter Internals

### What changes

Replace the 4 fixed `SemaphoreSlim` instances (`_queueSemaphore`, `_healthCheckSemaphore`, `_streamingSemaphore`, `_queueAnalysisSemaphore`) with a single `PrioritizedSemaphore` sized to `totalConnections`.

Operations are classified:
- **High priority**: Streaming, BufferedStreaming
- **Low priority**: Queue, QueueRarProcessing, QueueAnalysis, HealthCheck, Analysis, Repair, Unknown

The `PrioritizedSemaphore` (ported from original nzbdav) uses an accumulated-odds mechanism: when both High and Low priority waiters are queued, streaming wins `streamingPriority`% of the time (default 80%, configurable via `usenet.streaming-priority`).

**Streaming reserve floor**: To prevent queue burst from completely blocking new stream requests, track active Low-priority operations via an `Interlocked` counter. Before acquiring the `PrioritizedSemaphore`, Low-priority callers check: if `activeLowPriorityCount >= totalConnections - streamingReserve` (default 5), they wait on a secondary `SemaphoreSlim` that is released when any Low-priority operation completes. High-priority callers skip this check entirely — they always go straight to the `PrioritizedSemaphore`. This guarantees streaming can always get at least `streamingReserve` connections without waiting behind queued Low-priority work.

### Why not just raise MaxQueueConnections

Hard partitioning permanently reserves connections. Setting queue to 15 means streaming loses 15 connections even when no queue work is happening. The hybrid approach gives queue the full pool when idle and yields gracefully when streaming starts.

### Public API (unchanged)

`AcquirePermitAsync(ConnectionUsageType, CancellationToken)` and `ReleasePermit()` keep the same signatures. All callers are unaffected.

### Files

- `backend/Clients/Usenet/Connections/GlobalOperationLimiter.cs`
  - Remove `_queueSemaphore`, `_healthCheckSemaphore`, `_streamingSemaphore`, `_queueAnalysisSemaphore`
  - Add `PrioritizedSemaphore _sharedPool` sized to `totalConnections`
  - Add `int _activeLowPriorityCount` with `Interlocked` tracking
  - `AcquirePermitAsync`: map usage type to priority, check streaming reserve for Low priority, acquire from shared pool
  - `ReleasePermit`: release to shared pool, decrement low-priority counter if applicable
  - Keep all logging and `OperationPermit` class as-is
- `backend/Clients/Usenet/Concurrency/PrioritizedSemaphore.cs` — new file, ported from original nzbdav (`/backend/Clients/Usenet/Concurrency/PrioritizedSemaphore.cs`). ~190 lines. Dual-queue semaphore with configurable priority odds, `UpdateMaxAllowed`, `UpdatePriorityOdds`.
- `backend/Config/ConfigManager.cs`
  - Add `GetStreamingPriority()` returning `SemaphorePriorityOdds` (default 80)
  - Add `GetStreamingReserve()` (default 5)
  - Keep `GetMaxQueueConnections()` working but log deprecation if explicitly set — its value is no longer used as a semaphore size
- `backend/Clients/Usenet/Concurrency/SemaphorePriority.cs` — new file (enum: High, Low)
- `backend/Clients/Usenet/Concurrency/SemaphorePriorityOdds.cs` — new file (record with `HighPriorityOdds` property)

## Fix 2: Re-enable Buffered Streaming for Queue RAR Header Reads

### What changes

Add `QueueRarProcessing = 8` to `ConnectionUsageType` enum. This type gets Low priority in the PrioritizedSemaphore (same as Queue) but is NOT excluded from buffered streaming in `NzbFileStream`.

The existing disable check at `NzbFileStream.cs:281-283` already excludes only `Queue` and `QueueAnalysis`. Since `QueueRarProcessing` is a new value, it naturally passes the check — no change needed to `NzbFileStream.cs`.

In `RarProcessor.GetFastNzbFileStream`, switch the `ConnectionUsageContext` from `Queue` to `QueueRarProcessing` before creating the stream. The 5-connection buffered request that `GetFastNzbFileStream` already makes will now actually work.

### Why this is safe

The blanket disable was added to prevent memory buildup during the memory leak era. We fixed the root causes (ArrayPool retention, unbounded concurrent streams, GC misconfiguration) in v0.7.0. The concurrent stream cap (`max-concurrent-buffered-streams`) still applies and prevents unbounded allocation. RAR header reads are short-lived (seconds, not minutes) and self-limiting (bounded by the number of RAR parts being processed).

### Files

- `backend/Clients/Usenet/Connections/ConnectionUsageContext.cs` — add `QueueRarProcessing = 8`
- `backend/Clients/Usenet/Connections/GlobalOperationLimiter.cs` — map `QueueRarProcessing` to Low priority
- `backend/Queue/FileProcessors/RarProcessor.cs` — in `GetFastNzbFileStream` or `ProcessPartAsync`, create/switch to `QueueRarProcessing` usage context

## Fix 3: Article Caching for Queue Processing

### What changes

Port `ArticleCachingNntpClient` from original nzbdav. This is a per-queue-item `INntpClient` wrapper (~300 lines) that:

- On first fetch of a segment: downloads from Usenet, writes decoded bytes to a temp file (`Directory.CreateTempSubdirectory()`), stores yenc headers in `ConcurrentDictionary<string, CacheEntry>`, returns the response
- On subsequent fetch of same segment ID: reads from temp file, returns cached yenc headers. Zero network, zero permits.
- Per-segment `SemaphoreSlim(1,1)` deduplication: if two concurrent tasks request the same segment, only one fetches
- On dispose (queue item processing complete): deletes temp directory with retry logic

In `QueueManager.cs`, wrap the usenet client before creating `QueueItemProcessor`:

```csharp
using var cachingClient = new ArticleCachingNntpClient(_usenetClient);
// pass cachingClient to QueueItemProcessor
```

### Adaptation needed

- Check that nzbdav2's `INntpClient` interface matches original nzbdav's (same upstream heritage, likely identical)
- Port `CachedYencStream` if nzbdav2 doesn't have it — a thin wrapper that serves file bytes with stored yenc headers (~20 lines)
- The `WrappingNntpClient` base class should already exist in nzbdav2

### Files

- `backend/Clients/Usenet/ArticleCachingNntpClient.cs` — new file, ported from original nzbdav
- `backend/Streams/CachedYencStream.cs` — new file if not present (thin YencStream wrapper over FileStream)
- `backend/Queue/QueueManager.cs` — wrap usenet client in `ArticleCachingNntpClient` before creating `QueueItemProcessor`

## Fix 4: Concurrency Tuning

### What changes

Update concurrency limits that were derived from `GetMaxQueueConnections()` (which was 1) to use the download connection count, matching original nzbdav's approach. The PrioritizedSemaphore is the real gate now — these values should be high enough to keep the pool saturated.

**FetchFirstSegmentsStep.cs line 30:**
```
Before: var maxConcurrency = configManager.GetMaxQueueConnections();
After:  var maxConcurrency = configManager.GetMaxDownloadConnections() + 5;
```

If `GetMaxDownloadConnections()` doesn't exist in nzbdav2, add it: returns `min(totalPooledConnections, 15)` or a configured value. The `+ 5` soft buffer matches original nzbdav's pattern — allows slightly more tasks than connections since tasks spend time on CPU work (parsing) between network calls.

**QueueItemProcessor.cs line 164 (Step 1 concurrency):**
```
Before: var concurrency = configManager.GetMaxQueueConnections();
After:  var concurrency = configManager.GetMaxDownloadConnections() + 5;
```

**QueueItemProcessor.cs line ~258 (file processor concurrency):**
```
Before: var fileConcurrency = Math.Min(maxQueueConnections, totalPooledConnections / 5);
After:  var fileConcurrency = configManager.GetMaxDownloadConnections() + 5;
```

### Files

- `backend/Queue/DeobfuscationSteps/1.FetchFirstSegment/FetchFirstSegmentsStep.cs` — update concurrency
- `backend/Queue/QueueItemProcessor.cs` — update concurrency in two places
- `backend/Config/ConfigManager.cs` — add `GetMaxDownloadConnections()` if not present

### Config surface summary

| Config key | Default | Purpose |
|---|---|---|
| `usenet.streaming-priority` | 80 | % odds streaming wins contention (0-100) |
| `usenet.streaming-reserve` | 5 | Minimum connections guaranteed for streaming |
| `usenet.max-download-connections` | min(totalPooled, 15) | Max concurrent download operations (queue + streaming shared) |
| `api.max-queue-connections` | deprecated | Logged as deprecated if set; no longer controls a semaphore |

## Expected Performance

| Phase | Before (default config) | After |
|---|---|---|
| Step 1: First segments (60 files) | ~114s (concurrency 1) | ~3-5s (concurrency 20, cached for Step 2) |
| Step 2: RAR processing (60 parts) | ~335s (serial permits, unbuffered, re-fetching) | ~10-15s (parallel, buffered, first-segment cache hits) |
| **Total** | **~450s (7.5 min)** | **~15-20s** |

Faster than original nzbdav (<30s) because we combine: full pool access (which they have) + buffered multi-connection RAR reads (which they don't) + article caching (which they have).

## Implementation Order

The four fixes are independent and can be landed incrementally:

1. **Fix 1 (Hybrid Pool)** — biggest single impact, unblocks everything else
2. **Fix 4 (Concurrency Tuning)** — trivial once Fix 1 lands, makes Fix 1 effective
3. **Fix 3 (Article Caching)** — independent, eliminates redundant fetches
4. **Fix 2 (Buffered RAR Reads)** — independent, reduces per-part latency

Each fix can be tested and measured in isolation. Fix 1+4 together should get us to ~30-40 seconds. Adding Fix 3 drops it to ~20-25s. Adding Fix 2 gets us to the target ~15-20s.

## Testing

- Queue a 60-part RAR NZB (e.g., a 4K UHD BluRay remux) and measure total queue processing time
- Queue an NZB while actively streaming a different file — verify streaming latency is unaffected
- Start streaming mid-queue — verify streaming preempts queue within 1-2 seconds
- Monitor memory during queue processing — verify no regression from re-enabled buffered streaming
- Check that `api.max-queue-connections` deprecation warning appears in logs when explicitly set
- Verify article cache temp directory is cleaned up after queue item completes
