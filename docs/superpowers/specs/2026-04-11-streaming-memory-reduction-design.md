# Streaming Memory Reduction

**Date:** 2026-04-11
**Goal:** Prevent streaming retry storms from consuming unbounded memory, enabling nzbdav2 to run on 1GB RAM VPS instances.

## Problem

Two compounding issues cause excessive memory retention during streaming:

1. **ArrayPool retention**: `BufferedSegmentStream` rents 1MB+ byte arrays from `ArrayPool<byte>.Shared` for each segment. After stream disposal, the arrays are returned to the pool but the pool retains them indefinitely for reuse. On memory-constrained systems (1GB VPS), this means memory is never returned to the OS.

2. **Retry storm amplification**: When UsenetStreamer's fallback loop retries a failed stream, each attempt creates a new WebDAV GET to nzbdav2, which spawns a fresh `BufferedSegmentStream` with its full buffer allocation (~60MB with 30 connections). Multiple concurrent retries stack up multiple full-size buffers. nzbdav2 cannot distinguish a retry from a new request.

## Fix A: Replace ArrayPool with direct allocation

### What changes

In `BufferedSegmentStream.cs`, segment fetch code rents from `ArrayPool<byte>.Shared` with 1MB minimum. `PooledSegmentData.Dispose()` returns buffers to the pool.

Replace `ArrayPool<byte>.Shared.Rent(size)` with `new byte[size]`. In `PooledSegmentData.Dispose()`, remove the `ArrayPool.Return()` call and just null the reference. GC reclaims the memory and returns it to the OS.

### Why this is safe

ArrayPool's advantage is for rapid rent/return cycles. Segment buffers are held for seconds (created once per segment, read once by the consumer, then disposed). This allocation pattern doesn't benefit from pooling. On a 1GB VPS, the pool's retention is actively harmful.

### Files

- `backend/Streams/BufferedSegmentStream.cs`
  - Segment fetch (~line 1031): `ArrayPool<byte>.Shared.Rent()` → `new byte[size]`
  - Error paths: remove `ArrayPool<byte>.Shared.Return(buffer)` calls
  - `PooledSegmentData.Dispose()`: remove `ArrayPool<byte>.Shared.Return(_buffer)`, just set `_buffer = null`

## Fix C: Cap concurrent BufferedSegmentStreams

### What changes

Add a static `SemaphoreSlim` in `BufferedSegmentStream` that limits how many instances can exist concurrently. When the limit is reached, `NzbFileStream` falls through to the sequential unbuffered path (the same path queue processing already uses).

### Flow

1. Before creating a `BufferedSegmentStream`, `NzbFileStream` calls `BufferedSegmentStream.TryAcquireSlot()`
2. If a slot is available, create the buffered stream as normal. The stream releases the slot on disposal.
3. If no slot is available, fall through to the unbuffered sequential path (lines 343-373 in `NzbFileStream.cs`)

### Configuration

New config key: `usenet.max-concurrent-buffered-streams` (default: 2)

New method in `ConfigManager.cs`:
```csharp
public int GetMaxConcurrentBufferedStreams()
{
    return int.Parse(
        StringUtil.EmptyToNull(GetConfigValue("usenet.max-concurrent-buffered-streams"))
        ?? "2"
    );
}
```

The semaphore size is set from config at startup via `BufferedSegmentStream.SetMaxConcurrentStreams(int max)` static method, and updated when config changes.

### Files

- `backend/Streams/BufferedSegmentStream.cs` — static semaphore, `TryAcquireSlot()` / `ReleaseSlot()` methods, release in `DisposeAsync()`
- `backend/Streams/NzbFileStream.cs` — check slot availability before creating `BufferedSegmentStream`, fall through to unbuffered path if at capacity
- `backend/Config/ConfigManager.cs` — `GetMaxConcurrentBufferedStreams()` method

## What This Does NOT Change

- BufferedSegmentStream's core features (straggler detection, multi-provider failover, CRC validation, retry logic) are unaffected
- Queue processing already uses the unbuffered path — no change there
- The buffer sizing fix (commit 737530f) and disposal drain fix (commit e529391) remain as-is
- Playback streaming quality is unchanged — the first stream gets the full buffered treatment

## Expected Results

On a 1GB VPS with default settings (2 concurrent buffered streams, 20-segment buffer):
- Single stream: ~20-40MB buffer (direct alloc, returned to OS on disposal)
- Retry storm: first stream buffered, subsequent retries use unbuffered sequential path
- After all streams end: memory returns to baseline (no ArrayPool retention)

On the NAS (8GB, 30 connections, 2 concurrent buffered streams):
- Single stream: ~60MB buffer, returned to OS on disposal
- Retry storm: capped at 2 × 60MB = ~120MB, rest use unbuffered path
- Post-streaming memory should drop to baseline instead of holding at 5+ GiB
