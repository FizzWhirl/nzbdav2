# Feature Report — NzbFileStream / BufferedSegmentStream

**Files:**
- [backend/Streams/NzbFileStream.cs](../../backend/Streams/NzbFileStream.cs) (~450 LOC, +291 vs upstream)
- [backend/Streams/BufferedSegmentStream.cs](../../backend/Streams/BufferedSegmentStream.cs) (~1,800 LOC, +269 vs upstream)
- [backend/Api/Controllers/GetWebdavItem/GetWebdavItemController.cs](../../backend/Api/Controllers/GetWebdavItem/GetWebdavItemController.cs) (Range header handling)

## Summary
The two-layer engine that converts a list of segment IDs into a seekable
HTTP-friendly `Stream`. The fork's changes turn it from a fire-and-forget
prefetcher into a memory-bounded, sequential-read-aware, range-aware
streaming primitive with multi-worker fetch and provider-quality scoring.

## Architecture

```
HTTP Range request
       │
       ▼
GetWebdavItemController         ─── parses Range, calls Seek(X) + LimitLength(N)
       │
       ▼
NzbFileStream                   ─── handles seeks, picks shared vs unbuffered path,
       │                             owns CombinedStream
       ▼
BufferedSegmentStream           ─── multi-worker prefetch with channel-based
       │                             ordering, ring buffer, range-bounded
       ▼                             prefetch, provider scoring
MultiProviderNntpClient         ─── balanced selection, affinity, failover
       │
       ▼
ConnectionPool                  ─── circuit-breaker, reserve, health checks
```

## Key Mechanisms

### Sliding Window Buffer (commit `2898c19`)
- Bounded by `bufferSegmentCount = max(config, concurrentConnections * 2)`.
- Workers race for slot ownership via
  `Interlocked.CompareExchange(ref slots[index], data, null)`. Loser
  disposes the duplicate, no leak.

### Predictive Sequential Prefetch (commit `08f2fa8`)
- Detects sequential-read pattern from a moving average of read offsets.
- When detected, prefetch aggressively forward using the streaming reserve.

### Range-Bounded Prefetch (commit `e7cef65`)
- Reads the HTTP `Range:` header's end byte through to the segment table.
- Prefetch stops at the segment containing the end byte.
- Critical for HEAD probes / partial reads (Plex codec sniffing) — without
  this, a 1 KB partial read would still trigger a multi-MB prefetch.

### Memory-Reuse Buffer Resizing (commit `e64906a`)
- Reuses backing arrays when buffer grows or shrinks instead of
  reallocating. Cuts LOH churn substantially.

### Non-Blocking Prefetch with Priority Queue (commit `756fc46`)
- Foreground `Read()` is never blocked by a prefetch operation.
- Prefetch dispatched on a separate priority queue; foreground requests
  preempt prefetch.

### Dynamic Straggler Timeout
- Per-stream rolling 30-op window of fetch times.
- Timeout = `max(5 s, avgFetchTime × 3)` with a minimum of 10 samples.
- A worker exceeding timeout is canceled and the segment is reissued to
  a different provider.

### Provider Scoring
- Per-stream rolling window with sticky failure weight (+2 fail, −1 success).
- Soft cooldown (1–10 s) for stragglers.
- Affects the next call into `MultiProviderNntpClient` for this stream.

## Seeking Behaviour (Critical for User Experience)

The fork preserves and improves the seek model:

| Scenario | Latency | Mechanism |
|---|---|---|
| Initial open / play from 0 | ~150 ms (1 RTT) | Direct fetch of segment 0 |
| Sequential `Read()` continuation | 0 ms (in-buffer) | Slides forward in ring |
| Forward seek **within current buffer window** | ~5 ms | Pointer move only |
| Forward seek **outside window** | 150–900 ms | Interpolation search (3–5 RTTs) + fetch |
| Forward seek with **cached segment offsets** | ~150 ms | Binary search + fetch |
| Backward seek | Same as forward | Buffer discarded, reallocate |
| Repeated seek to same exact byte (likely client bug) | Detected at 100+, error | Defensive |

**Seek mechanics inside `NzbFileStream.Seek()`:**
1. Translate byte offset → segment index using cached `(segIdx → byteRange)`
   table (built lazily as the file is explored).
2. If segment unknown, run interpolation search: fetch ~3 candidate
   segments to bracket the offset; refine from there.
3. Discard current `CombinedStream` (or reset its inner cursor); spawn a
   new one starting at the resolved segment.
4. Re-prime the buffer at the new position. Cancel any in-flight
   prefetches that are no longer in range.

**Worst-case practical seek time** = (interpolation RTTs × provider RTT)
+ (yenc decode of fetched segments) + (first-byte arrival of new segment).
For a typical 50 ms RTT to a US provider with cached offsets:
**~250–500 ms cold seek**, **~50 ms warm seek**.

## Possible Issues / Edge Cases

| # | Issue | Severity |
|---|---|---|
| 1 | OOM cooldown blocks for 750 ms — rare, but a stall during playback. | Low |
| 2 | Segment-size mismatch silently continues with zero-fill — playback gets garbage frames; relies on Health/Par2 to repair. | Medium |
| 3 | `_totalDecodeTimeMs` and `s_streamCount` fields exist but unused — dead code. | Cosmetic |
| 4 | Interpolation search can hit the same provider for all 3–5 candidates; if that provider is the slow one, seek is slow. Consider distributing candidates across providers. | Medium |
| 5 | Backward seek discards the entire buffer even when the target is only slightly behind the current head — expensive for fast scrub. A bounded back-buffer (e.g. 8 segments) would help. | Medium |
| 6 | "Infinite seek detected" error throws after 100 same-offset seeks — useful guard, but throws instead of capping silently; some buggy clients (older rclone HTTP backend) could trip this. | Low |

## Code Quality
- Lock-free fast paths for the hot read loop.
- Cancellation propagated correctly throughout (job CTS linked to caller).
- Diagnostic logs at sensible levels (after this session's cleanup).
- Magic numbers (idle 60 s, straggler 100 ms, OOM 750 ms) are documented
  inline but not configurable.

## Recommended Improvements
1. **Bounded back-buffer** so small reverse seeks don't reset the pump.
2. **Distribute interpolation candidates** across providers.
3. **Promote OOM cooldown to a config knob** (`usenet.oom-cooldown-ms`).
4. **Expose seek-latency histogram** as a metric to detect provider
   regressions.
5. **Reconsider the unused decode-time field** — wire it up or delete.
