# Memory Usage Improvement Report v2 — 2026-04-28

> An earlier draft of this report existed but has been superseded by this version. All claims here are grounded in the current code at HEAD.

## Goal

Reduce NzbDav resident memory and GC pressure **without reducing streaming throughput**. That means:

- Do **not** reduce per-stream connection count to save memory.
- Do **not** disable segment-level read-ahead.
- Do **not** add inline CRC / decode work to the playback path.
- Prefer changes that lower retained bytes per active stream, eliminate duplicate materialisation, or shift large allocations to pooled / adaptive allocations.

## Executive Summary

The streaming path is already largely allocation-conscious: segment payloads use `ArrayPool<byte>`, multipart paths use `maxCachedStreams: 0`, and the WebDAV GET handler streams to the response. The remaining memory pressure comes from a small number of **multiplicative or eagerly-allocated** patterns:

1. The shared-stream ring buffer is allocated at full configured size **per active shared entry**, even when only one reader is present.
2. The shared-stream pump uses an unpooled 256 KB buffer for its lifetime.
3. The WebDAV GET copy loop allocates a fresh unpooled 256 KB buffer **per request**.
4. `BufferToEndStream` uses a 1 MB pause / 256 KB resume Pipe per active article, multiplied by concurrent worker count.
5. `BufferedSegmentStream` forces a channel capacity of at least `concurrentConnections × 2` segment slots per stream.
6. NZB ingestion materialises the same payload as `string` + `byte[]` + `MemoryStream` + a stored copy on the queue row.
7. Background work (analysis, health, repair, PAR2) can run concurrently with playback and is not memory-aware.

The recommendations below are ranked by `(estimated savings) / (throughput risk)` and grouped by area. Each lists the file + line range and the actual current code behaviour observed.

---

## Validation of v1 Claims

| v1 Claim | Status | Evidence |
| --- | --- | --- |
| **A.** WebDAV GET copy uses unpooled 256 KB buffer per request | ✅ Confirmed | [backend/WebDav/Base/GetAndHeadHandlerPatch.cs](backend/WebDav/Base/GetAndHeadHandlerPatch.cs#L190): `var buffer = new byte[256 * 1024];` |
| **B.** `SharedStreamEntry` eagerly allocates the full ring buffer | ✅ Confirmed | [backend/Streams/SharedStreamEntry.cs](backend/Streams/SharedStreamEntry.cs#L74): `_ringBuffer = new byte[ringBufferSize];` in constructor |
| **C.** Shared-stream pump uses an unpooled 256 KB buffer | ✅ Confirmed | [backend/Streams/SharedStreamEntry.cs](backend/Streams/SharedStreamEntry.cs#L247): `var buffer = new byte[256 * 1024];` in `PumpLoop` |
| **D.** `BufferToEndStream` uses 1 MB pause / 256 KB resume per article | ✅ Confirmed | [backend/Streams/BufferToEndStream.cs](backend/Streams/BufferToEndStream.cs#L40-L48): default `pauseWriterThreshold = 1 MB`, `resumeWriterThreshold = 256 KB` |
| **E.** `BufferedSegmentStream` forces channel capacity ≥ `concurrentConnections × 2` | ✅ Confirmed | [backend/Streams/BufferedSegmentStream.cs](backend/Streams/BufferedSegmentStream.cs#L411): `bufferSegmentCount = Math.Max(bufferSegmentCount, concurrentConnections * 2);` |
| **F.** RAR / multipart paths use `maxCachedStreams: 0` | ✅ Confirmed (intentional) | [backend/Streams/DavMultipartFileStream.cs](backend/Streams/DavMultipartFileStream.cs#L125) and [backend/Streams/NzbFileStream.cs](backend/Streams/NzbFileStream.cs#L551) |
| **G.** PAR2 has small in-memory path without byte cap | ⚠️ Partially refuted | `Par2.cs` enforces `MaxConsecutiveNonFileDesc` and an optional `maxDescriptors` early termination. There is no explicit byte cap, but practical extraction is bounded by descriptor count. Lower priority than v1 implied. |
| **H.** NZB ingestion materialises content as string + bytes + stream + history copy | ✅ Confirmed | `AddFileController` does `string nzbFileContents` → `Encoding.UTF8.GetBytes(...)` → `new MemoryStream(documentBytes)` → also stored on the queue row. |
| **I.** Metadata list/maintenance queries load full compressed segment metadata | ⚠️ Not generally confirmed | Most list endpoints already paginate. Worth a follow-up audit but not a top item. |

The v1 report did not include claim D's exact numbers but described it correctly; this v2 report keeps it because the multiplier with `BufferedSegmentStream` worker count is significant.

---

## Streaming Hot Path

### 1. Make the `BufferedSegmentStream` channel capacity adaptive **(v1 Validated, refined)**

**File:** [backend/Streams/BufferedSegmentStream.cs](backend/Streams/BufferedSegmentStream.cs#L390-L420)

**Current behaviour:** `bufferSegmentCount = Math.Max(bufferSegmentCount, concurrentConnections * 2);` Each buffered segment holds a ~1 MB pooled byte array. With 25 connections the channel forces a minimum of 50 segment slots, ≈ 50 MB of segment payload retained per active stream before counting in-flight reads, the per-article `BufferToEndStream` Pipe, and the shared-stream ring.

**Why it costs memory:** This minimum applies even for clients that consume steadily and never need that much head-room. It is multiplied per active `BufferedSegmentStream`.

**Proposed change:**
- Start the channel near `concurrentConnections` (1×) and only grow toward `2×` (or up to the configured maximum) when the consumer is consistently faster than the producers (drain stall observed for N ms).
- Keep `2×` as the upper bound so high-latency providers still get head-room.
- Configurable behind an advanced setting for safety.

**Estimated savings:** ~25–50 MB per active stream when adaptive growth is not triggered.

**Throughput risk:** Low to medium. Mitigation: only adapt downward at startup; allow growth on observed stall. Validate on a high-latency provider before shipping.

### 2. Lower or make adaptive the `BufferToEndStream` Pipe thresholds **(v1 Validated)**

**File:** [backend/Streams/BufferToEndStream.cs](backend/Streams/BufferToEndStream.cs#L40-L60)

**Current behaviour:** Each per-article `BufferToEndStream` creates a `Pipe` with `pauseWriterThreshold = 1 MB` and `resumeWriterThreshold = 256 KB`, plus a rented `_segmentSize` scratch buffer. With many concurrent article workers per stream, this multiplies into tens of MB before any segment is queued downstream.

**Proposed change:**
- Lower defaults to `pause = 256 KB`, `resume = 64 KB`. The `BufferedSegmentStream` channel already provides the read-ahead that justifies a large per-article pipe.
- Keep the constructor parameters and add an environment / settings override for high-latency providers that benefit from larger per-article buffering.

**Estimated savings:** ~15–25 MB per active stream depending on worker count.

**Throughput risk:** Low. Connection parallelism is unchanged; only per-article pipe head-room shrinks.

### 3. Pool the WebDAV GET copy buffer **(v1 Validated)**

**File:** [backend/WebDav/Base/GetAndHeadHandlerPatch.cs](backend/WebDav/Base/GetAndHeadHandlerPatch.cs#L185-L210)

**Current behaviour:** `var buffer = new byte[256 * 1024];` allocated fresh per GET / HEAD request and never returned to a pool.

**Proposed change:** Rent from `ArrayPool<byte>.Shared.Rent(256 * 1024)`, return in `finally`. This is a contained, low-risk edit.

**Estimated savings:** Modest per-request allocation reduction; significant LOH pressure reduction under high concurrent ranged requests.

**Throughput risk:** None.

---

## Shared Stream Buffers

### 4. Lazy / adaptive `SharedStreamEntry` ring buffer **(v1 Validated)**

**File:** [backend/Streams/SharedStreamEntry.cs](backend/Streams/SharedStreamEntry.cs#L60-L100)

**Current behaviour:** `_ringBuffer = new byte[ringBufferSize];` at construction. With a configured ring of 32 MB this is allocated immediately, per active shared entry, regardless of how many readers attach. This is a long-lived allocation held for the duration of playback.

**Proposed change:**
- Allocate a small initial ring (e.g. 4 MB) on first pump write.
- Grow towards the configured maximum only when (a) a second reader attaches, or (b) the slowest reader consistently lags by > 50 % of the current ring.
- Release back to a smaller working set after the grace period if usage was below threshold.

**Estimated savings:** ~24–28 MB per single-reader shared entry.

**Throughput risk:** Low. Single-client playback rarely exercises the full ring; growth path preserves the worst case behaviour.

### 5. Pool the shared-stream pump buffer **(v1 Validated)**

**File:** [backend/Streams/SharedStreamEntry.cs](backend/Streams/SharedStreamEntry.cs#L243-L260)

**Current behaviour:** `var buffer = new byte[256 * 1024]; // 256KB chunks` allocated for the lifetime of the pump loop.

**Proposed change:** Rent from `ArrayPool<byte>.Shared` and return on pump exit / disposal. Or reuse the buffer length tied to `BufferedSegmentStream`'s preferred read size to avoid mismatched chunking.

**Estimated savings:** 256 KB per active shared entry plus reduced LOH churn on entry create/dispose.

**Throughput risk:** None.

### 6. Verify shared-entry slot release on grace expiry

**File:** [backend/Streams/SharedStreamEntry.cs](backend/Streams/SharedStreamEntry.cs#L220-L260)

**Why:** During testing of (4) and (5), confirm grace-period eviction actually disposes `_ringBuffer`, releases `_slot`, and stops the pump. This is to make sure the lazy / pooled changes do not regress eviction semantics. No code change unless a leak is found.

---

## RAR / Multipart **(v1 Validated, no change)**

`DavMultipartFileStream` and `NzbFileStream` use `new CombinedStream(parts, maxCachedStreams: 0)`. This is intentional and should be preserved. `CombinedStream` already uses `ArrayPool<byte>.Shared.Rent(65536)` for its discard buffer. **Do not** add child-stream caching as a memory optimisation — it is the opposite of what we want.

---

## Background Work and Concurrency

### 7. Memory-aware / playback-aware throttling for background work **(v1 Validated)**

**Files:**
- [backend/Services/MediaAnalysisService.cs](backend/Services/MediaAnalysisService.cs)
- [backend/Services/HealthCheckService.cs](backend/Services/HealthCheckService.cs)
- [backend/Par2Recovery/](backend/Par2Recovery/)
- [backend/Services/BackgroundTaskQueue.cs](backend/Services/BackgroundTaskQueue.cs)

**Current behaviour:** Background services have their own concurrency limits but no awareness of currently-active buffered playback streams. They can stack memory use with playback.

**Proposed change:** Add a lightweight global signal (`SharedStreamManager.ActiveEntryCount` or `BufferedSegmentStream` registration count). When > 0, background services voluntarily reduce concurrency (e.g. cap analysis to 1, defer health-check batches, pause new repair starts). When 0, return to normal concurrency.

**Estimated savings:** Avoids worst-case memory stacking during active playback.

**Throughput risk:** None for streaming. Background tasks complete more slowly under sustained playback.

### 8. Cap PAR2 byte usage and ffmpeg/ffprobe output capture **(v1 Refined)**

**Files:** `backend/Par2Recovery/Par2.cs`, `backend/Services/MediaAnalysisService.cs`

PAR2 is already partially bounded by descriptor early termination (correcting v1 claim G). Still worth adding an explicit byte cap on the in-memory path so a pathological PAR2 candidate cannot blow up the heap. Similarly, retain only the last N KB of ffmpeg/ffprobe stderr/stdout for diagnostics rather than full process output.

**Estimated savings:** Bounded worst case rather than steady-state savings.

**Throughput risk:** None for streaming.

---

## NZB Ingestion and Database

### 9. Eliminate NZB triple-materialisation **(v1 Validated)**

**File:** [backend/Api/SabControllers/AddFile/AddFileController.cs](backend/Api/SabControllers/AddFile/AddFileController.cs)

**Current behaviour:** Reads the upload as `string`, then `Encoding.UTF8.GetBytes(...)`, then wraps that in `MemoryStream`, then stores the original string on the queue row.

**Proposed change:**
- Stream-parse the request body into the NZB document directly; avoid the intermediate `string` representation if the parser supports it.
- If the parser must have a `string`, skip the explicit `byte[]` + `MemoryStream` step (parse from the string or from the request stream once).
- Store NZB content compressed once and reference it from queue/history rows by ID.

**Estimated savings:** Up to 3× the NZB size in transient memory during ingestion.

**Throughput risk:** None for streaming. Improves ingestion latency.

### 10. Project metadata in list/maintenance queries **(v1 Refined / unverified)**

The v1 claim was generic. Recommend a focused pass to confirm dashboard/list endpoints use `.Select(...)` projections rather than loading full `DavNzbFiles` rows (compressed segment metadata can be large). Treat as "verify and tighten" rather than a known issue.

---

## Recommendations to Avoid

These would harm the explicit goal:

- Lowering total streaming connections globally just to save memory.
- Lowering provider `MaxConnections` for memory reasons alone.
- Disabling buffered streaming for normal playback.
- Replacing `ArrayPool<byte>` with direct `new byte[]` for segment payloads. The pool retains buffers but avoids LOH allocation churn under sustained playback; replacing it would increase allocation rate and GC pressure rather than reduce it.
- Adding inline full-body CRC validation to the playback path.

> Note: the standalone audit document considered an "ArrayPool → direct allocation" change for `BufferedSegmentStream`. After review, that change is **not recommended** — pooling is the correct strategy for these short-lived ~1 MB segment buffers, and the real lever is the channel capacity (item 1) and the per-article pipe thresholds (item 2).

---

## Suggested Implementation Order

1. **(3)** Pool the WebDAV GET copy buffer.
2. **(5)** Pool the shared-stream pump buffer.
3. **(2)** Lower `BufferToEndStream` Pipe thresholds (configurable).
4. **(4)** Lazy / adaptive `SharedStreamEntry` ring buffer.
5. **(7)** Memory- and playback-aware background throttling.
6. **(1)** Adaptive `BufferedSegmentStream` channel capacity (most invasive; do last and behind a setting).
7. **(9)** NZB ingestion de-duplication.
8. **(8)** PAR2 byte cap and ffmpeg output cap.
9. **(10)** Metadata projection pass.
10. **(6)** Verify shared-entry eviction after the above ship.

---

## Validation Plan

For each change:

- `cd frontend && npm run typecheck`.
- `docker build -t local/nzbdav:3 .`.
- Stream one large file before and after with the same provider settings; record:
  - resident memory and GC heap;
  - active buffered streams;
  - active provider connections;
  - end-to-end throughput (MB/s).
- Run scenarios: single-client playback, multi-client playback, range seeks, and background analysis running during playback.
- Accept the change only if throughput stays within normal variance and resident memory drops measurably for the target scenario.

---

## Document History

- **v2 (this document, 2026-04-28)**: Independently audited; validated previously claimed items A, B, C, D, E, F, H against code; refined G; flagged I as unverified; corrected an "ArrayPool → direct allocation" suggestion (kept as a non-recommendation).
