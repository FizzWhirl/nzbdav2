# Memory Usage Improvement Report — 2026-04-28

## Goal

Identify ways to reduce NzbDav memory pressure without reducing streaming throughput. The recommendations below prioritise preserving connection parallelism and read-ahead behaviour where it directly contributes to playback speed, while reducing avoidable retained buffers, duplicate materialisation, and background-task memory contention.

## Executive Summary

The current streaming path is already mostly streaming-oriented: WebDAV responses copy from streams, RAR/multipart paths avoid caching multiple child streams, and segment payloads use pooled arrays in the hot path. The largest remaining memory risks are multiplicative buffer layers around high connection counts and background work that can run concurrently with playback.

Highest-value changes with low streaming-speed risk:

1. Pool fixed HTTP/WebDAV and shared-stream pump buffers instead of allocating new 256 KB arrays per request/pump.
2. Reduce or make adaptive the per-article `BufferToEndStream` pipe thresholds because segment-level buffering already provides read-ahead.
3. Make shared stream ring buffers adaptive instead of allocating the full configured ring immediately for every eligible unbounded stream.
4. Add memory-aware throttling for analysis, health, PAR2, and repair work when active playback streams are consuming buffered slots.
5. Remove duplicate NZB string/byte/stream materialisation in upload and queue processing.
6. Add byte caps to small-PAR2 in-memory descriptor extraction.

## Streaming Hot Path

### Current behaviour

- `BufferedSegmentStream` is the main read-ahead pipeline for segment streaming. It forces the segment channel capacity to at least `concurrentConnections * 2` in backend/Streams/BufferedSegmentStream.cs and uses `PooledSegmentData` backed by `ArrayPool<byte>` for decoded article payloads.
- Each provider article stream is read to completion by `BufferedSegmentStream.FetchSegmentWithRetryAsync()`, then queued as decoded segment data.
- `BufferToEndStream` wraps article reads in a `Pipe` with a 1 MB pause threshold and 256 KB resume threshold per active article connection.

### Memory risk

With 20 concurrent article workers and approximately 768 KB yEnc-decoded articles, the segment queue alone can retain roughly 40 decoded article buffers, around 30 MB per buffered stream before in-flight article readers, pipe buffers, shared rings, HTTP buffers, and process overhead. The `BufferToEndStream` pipe thresholds can multiply this when many article reads are active at once.

### Recommended changes

#### 1. Make `BufferToEndStream` pipe thresholds adaptive or lower by default

Current behaviour favours generous per-article buffering. Because `BufferedSegmentStream` already controls read-ahead at the segment level, lowering pipe thresholds should reduce memory without materially reducing speed.

Suggested approach:

- Start with a lower pause/resume pair, for example 256 KB / 64 KB.
- Keep thresholds configurable behind advanced settings or environment variables for testing.
- If throughput regressions appear on high-latency providers, consider adaptive thresholds based on observed read stall time rather than a fixed 1 MB per article.

Expected impact:

- Memory: high reduction when many article workers are active.
- Speed: low risk, because connection parallelism is unchanged.
- Complexity: low to medium.

#### 2. Make segment read-ahead capacity adaptive

The `concurrentConnections * 2` minimum is safe for speed but can over-buffer for clients that consume steadily. Consider an adaptive queue capacity that starts closer to `concurrentConnections` and grows only when the client is consistently draining faster than workers can refill.

Expected impact:

- Memory: medium to high reduction per active stream.
- Speed: low to medium risk depending on tuning.
- Complexity: medium.

Do not reduce the configured connection count as the primary memory fix. That would directly affect download/streaming speed and is less aligned with the goal.

## Shared Stream Buffers

### Current behaviour

Shared streams allow multiple readers of the same WebDAV item to attach to one buffered pump. `SharedStreamManager` creates shared entries after acquiring a global buffered stream slot. `SharedStreamEntry` allocates its ring buffer immediately and also uses a 256 KB pump buffer.

### Memory risk

A configured shared-stream buffer size such as 32 MB is allocated per active shared entry even when there is only one reader and no second client ever attaches. For single-client playback this memory often provides limited value.

### Recommended changes

#### 3. Allocate shared ring buffers lazily/adaptively

Options:

- Start with a smaller ring, for example 4–8 MB, and grow toward the configured maximum only when reader lag requires it.
- Promote to a full shared buffer only when a second reader attaches or when the client pattern shows reconnect/seek behaviour that benefits from reuse.
- Release or shrink idle shared buffers earlier after the grace period expires.

Expected impact:

- Memory: high reduction for single-client playback.
- Speed: low risk if growth is fast and the configured maximum remains available.
- Behaviour tradeoff: second-reader attachment may have slightly less historical buffer available immediately after initial playback starts.

#### 4. Pool shared-stream pump buffers

The shared pump uses a fixed 256 KB buffer. Rent this from `ArrayPool<byte>` and return it on disposal.

Expected impact:

- Memory/GC: modest but very low risk.
- Speed: no expected negative impact.
- Complexity: low.

## WebDAV / HTTP Response Copying

### Current behaviour

The WebDAV GET handler streams to the response body and does not buffer whole files. However, the copy loop allocates a new 256 KB buffer per request.

### Recommended change

#### 5. Rent the response copy buffer from `ArrayPool<byte>`

Replace per-request allocation with pooled rental and return in `finally`.

Expected impact:

- Memory/GC: modest improvement, especially under multiple clients or frequent range requests.
- Speed: no expected negative impact.
- Complexity: low.

## RAR and Multipart Streaming

### Current behaviour

RAR and multipart file paths use `DavMultipartFileStream`, optionally AES decoding and `RarDeobfuscationStream`. Child streams are created lazily and `CombinedStream` caching is disabled with `maxCachedStreams: 0` for major multipart paths.

### Recommendation

Keep this behaviour. Avoid adding child-stream caching unless a measured random-seek issue requires it.

Expected impact:

- Memory: preserves current low-retention behaviour.
- Speed: current lazy active-part streaming is appropriate for playback.

## Queue, Analysis, Health, Repair, and PAR2 Work

### Current behaviour

Background jobs can run media analysis, health checks, queue probing, repair, and PAR2 descriptor extraction while playback is active. Analysis concurrency is configurable, and the background queue has multiple workers. PAR2 descriptor extraction has an in-memory path for small candidates.

### Memory risk

Even if each task is individually reasonable, concurrent background operations can stack memory use with active playback: ffmpeg/ffprobe process output, article buffers, parsed metadata, PAR2 byte arrays, and database materialisation.

### Recommended changes

#### 6. Add a memory-aware background limiter

Introduce a lightweight process-level memory budget or playback-aware limiter. When active buffered streams exist, reduce concurrency for:

- media analysis and decode checks;
- health checks;
- PAR2 descriptor extraction;
- repair verification;
- full-download/integrity tasks if added later.

The limiter should reduce background concurrency, not streaming concurrency.

Expected impact:

- Memory: high improvement during playback + background work.
- Speed: protects streaming speed by yielding background work first.
- Queue latency: background tasks may complete later under active playback.

#### 7. Add byte caps to small-PAR2 in-memory handling

PAR2 descriptor extraction currently uses an in-memory path for small PAR2 candidates. Add a byte-size cap as well as any segment-count cap, then stream or skip larger candidates.

Expected impact:

- Memory: protects against unexpectedly large PAR2 payloads.
- Speed: no playback impact.
- Functionality tradeoff: very large PAR2 descriptor candidates may be handled more slowly or skipped unless streamed.

#### 8. Cap ffmpeg/ffprobe captured output

Where media analysis reads process stdout/stderr to completion, cap retained output to the last N KB needed for diagnostics.

Expected impact:

- Memory: protects against pathological process output.
- Speed: no playback impact.
- Diagnostics: enough tail output should be retained for useful errors.

## NZB Upload, Queue, and Database Materialisation

### Current behaviour

Some queue paths duplicate NZB content as a string, UTF-8 bytes, and `MemoryStream`. Queue history can retain full NZB contents again. Metadata converters and blob compatibility paths decompress full metadata into memory before deserialisation.

### Recommended changes

#### 9. Avoid duplicate NZB materialisation

Prefer a single canonical representation for incoming NZB content. Where possible:

- stream-parse upload input;
- avoid converting string → byte[] → `MemoryStream` when one representation is enough;
- store compressed NZB content once and reference it from queue/history records.

Expected impact:

- Memory: medium to high during queue ingestion of large NZBs.
- Speed: no streaming impact; queue ingestion may improve.
- Complexity: medium.

#### 10. Lazy-load or project large metadata fields

Ensure list/table queries do not load compressed segment metadata unless the operation actually needs it. Prefer projections for UI list endpoints and maintenance scans.

Expected impact:

- Memory: medium reduction in dashboard/maintenance/queue operations.
- Speed: may improve database/UI operations; no playback downside.
- Complexity: medium.

## Recommendations to Avoid

Avoid these as first-line memory fixes because they can directly harm streaming speed or user experience:

- Lowering total streaming connections globally.
- Lowering provider `MaxConnections` just to reduce memory.
- Disabling buffered streaming for normal playback.
- Removing segment-level read-ahead without replacing it with adaptive behaviour.
- Running full CRC validation inline with normal playback unless the user explicitly chooses integrity over speed.

## Suggested Implementation Order

1. Pool the WebDAV response copy buffer and shared-stream pump buffer.
2. Lower or make configurable the `BufferToEndStream` pipe thresholds.
3. Add background memory/playback-aware throttling.
4. Add PAR2 byte caps and process-output caps.
5. Make shared stream ring buffers adaptive.
6. Refactor NZB upload/history materialisation.
7. Add metadata projection/lazy-load cleanups.

## Validation Plan

For each change, validate both memory and speed:

- Run frontend typecheck and Docker build for compile safety.
- Stream one large file with the same provider settings before and after the change.
- Track resident memory, GC heap, active buffered streams, active/live provider connections, and throughput.
- Test single-client playback, multiple concurrent streams, range seeks, and background analysis running during playback.
- Confirm throughput stays within normal variance before accepting any memory tuning change.
