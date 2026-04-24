# Feature Report — Shared Streams

**Files:**
- [backend/Streams/SharedStreamManager.cs](../../backend/Streams/SharedStreamManager.cs) (137 LOC, new)
- [backend/Streams/SharedStreamEntry.cs](../../backend/Streams/SharedStreamEntry.cs) (410 LOC, new)
- [backend/Streams/SharedStreamHandle.cs](../../backend/Streams/SharedStreamHandle.cs) (128 LOC, new)
- Wired into [backend/Streams/NzbFileStream.cs:308](../../backend/Streams/NzbFileStream.cs#L308) (`GetCombinedStream`)

## Summary
Static, process-wide manager that gives multiple HTTP clients access to a
single in-flight `BufferedSegmentStream` per `DavItemId`. Late-joining
readers attach to a ring buffer; the underlying NNTP connections are
allocated **once** for the file, regardless of how many concurrent
consumers it has.

## Value

| Scenario | Without shared streams | With shared streams |
|---|---|---|
| Plex transcode + direct play of same file | 2× connection set, 2× bandwidth | 1× connection set, 1× bandwidth |
| HEAD probes from 3 health checks during playback | 4× downloads of first/last segments | 1× download, 3 attached |
| Multi-user household watching identical file | N× everything | 1× everything |

Largest win is for NZB libraries where the same file is "live" for several
hours (long movie + transcoder + a friend joining mid-stream).

## Behavioural Model

- `ConcurrentDictionary<Guid, SharedStreamEntry>` keyed by `DavItem.Id`.
- Each entry owns its `BufferedSegmentStream` + a ring buffer sized by
  `usenet.shared-stream-buffer-size` (default 32 MB).
- Per-reader position tracked in `ConcurrentDictionary<int, long>`.
- Pump is back-pressured: it blocks if writing would overwrite data the
  slowest active reader has not yet consumed.
- Grace period of `usenet.shared-stream-grace-period` (default 10 s) keeps
  the entry alive after the last reader detaches; a new reader arriving
  inside the grace window cancels the eviction timer.

## Activation Conditions
`NzbFileStream.GetCombinedStream()` only chooses the shared path when **all
four** are true:

```csharp
useBufferedStreaming
 && concurrentConnections >= 3
 && segmentCount > connections
 && davItemId.HasValue
 && !requestedEndByte                  // i.e. no HTTP Range
```

The `!requestedEndByte` gate is the important one: range-requested reads
are kept on the unbuffered path so the prefetch never goes past the
client's intended window.

## Possible Issues / Edge Cases

| # | Issue | Severity |
|---|---|---|
| 1 | A stalled reader (slow client) can throttle the pump for everyone attached. Mitigated by 60 s HTTP idle timeout that disconnects it. | Medium |
| 2 | Entry race window between `TryAdd` and the loser's fallback (handled correctly via "use winner's entry" pattern, but worth a unit test). | Low |
| 3 | `s_streamCount` field present but unused (preparatory observability hook). | Cosmetic |
| 4 | No metric exposed for "shared-hit ratio" — would be useful to verify the feature is actually deduplicating in production. | Observability gap |
| 5 | Grace period eviction is timer-driven; if the pump takes longer than grace to drain after last detach, you can briefly hold connections that nobody is reading. | Low |

## Code Quality
- Cancellation propagation is correct (entry-scoped CTS allows pump to
  outlive the originating HTTP request).
- Reference counting is conservative (handle leak guard on init failure
  via commit `a73b400`).
- No locks held during I/O — all blocking is on TPL primitives.
- Magic numbers (`32 MB`, `10 s`, `3 connections`) are configurable except
  the connection-count threshold.

## Recommended Improvements
1. Add a `shared_streams_active` / `shared_streams_attached` counter
   (Prometheus or just a periodic log line).
2. Log a one-shot "stalled reader detected" warning when a single reader
   becomes the back-pressure bottleneck for >5 s.
3. Consider promoting `concurrentConnections >= 3` to a config knob.
