# Feature Report — Article Caching

**File:** [backend/Clients/Usenet/ArticleCachingNntpClient.cs](../../backend/Clients/Usenet/ArticleCachingNntpClient.cs) (130 LOC, new)

## Summary
Decorator around an inner `INntpClient` that caches **decoded** segments to
a per-queue-item temp directory. Eliminates re-fetching of the same
articles between pipeline steps that each touch them (e.g. RAR header
read in Step 2, RAR aggregation in Step 4, ffprobe in Step 5).

## Value
For a typical multi-RAR queue item, the same first segment of `part01.rar`
is needed by:
1. Step 1: deobfuscation header read.
2. Step 2: RAR descriptor extraction.
3. Step 4: RAR aggregator metadata.
4. Optional Step 5: ffprobe of inner media.

Without the cache: 4 NNTP fetches + 4 yenc decodes per shared segment.
With the cache: 1 fetch + 1 decode + 3 file reads.

For NZBs with hundreds of small files (typical scene release with many
parts), this collapses minutes of analysis into seconds.

## Behavioural Model
- Wraps any `INntpClient`. Calls flow through it.
- Cache key: segment ID string.
- Storage: temp file per segment under `{tmp}/nzbdav-articles-{guid}/`.
- Per-segment `SemaphoreSlim` serializes concurrent fetches of the same
  segment (collaborative fetch — first caller fetches, the rest wait).
- Lifecycle: bound to the queue item being processed. Disposed when the
  item completes/fails.

## Possible Issues / Edge Cases

| # | Issue | Severity |
|---|---|---|
| 1 | Cleanup is best-effort background (5 retries, 1–10 s backoff). Crash mid-cleanup leaves orphaned temp dirs. | Low (housekeeping) |
| 2 | No size cap. A queue item with hundreds of MB of headers cached can balloon disk usage briefly. | Medium |
| 3 | No TTL within a single queue item — anything cached stays cached until disposal. | Low |
| 4 | Decoded segments are written as raw bytes; if the same segment is requested twice with different yenc extra metadata, the cache returns the first one's payload only. | Low |

## Code Quality
- Decorator pattern is clean — caller is unaware of caching.
- Semaphore pool is per-segment-id, scoped to instance, no contention with
  other queue items.
- No hot-path allocations — `byte[]` rented from `ArrayPool` would be
  marginally better but I/O dominates anyway.

## Recommended Improvements
1. Add a soft size cap (e.g. 512 MB), evict LRU when exceeded.
2. Add a startup sweep that nukes orphaned `nzbdav-articles-*` dirs older
   than 1 hour.
3. Expose a metric for cache hit/miss ratio per queue item — useful to
   prove the feature's value in production.
