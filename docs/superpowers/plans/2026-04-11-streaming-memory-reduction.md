# Streaming Memory Reduction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace ArrayPool with direct allocation and cap concurrent BufferedSegmentStreams to prevent unbounded memory growth from streaming retry storms.

**Architecture:** Two independent changes in BufferedSegmentStream.cs — swap ArrayPool.Rent/Return for new byte[]/null (Fix A), and add a static semaphore that NzbFileStream checks before creating a BufferedSegmentStream, falling through to the unbuffered sequential path when at capacity (Fix C). A new ConfigManager method exposes the cap as `usenet.max-concurrent-buffered-streams`.

**Tech Stack:** C# / .NET 10, no new dependencies

---

### Task 1: Replace ArrayPool with direct allocation (Fix A)

**Files:**
- Modify: `backend/Streams/BufferedSegmentStream.cs`

This task replaces every `ArrayPool<byte>.Shared.Rent()` with `new byte[]` and removes every `ArrayPool<byte>.Shared.Return()` call. The `PooledSegmentData.Dispose()` method nulls the reference instead of returning to the pool.

- [ ] **Step 1: Replace ArrayPool in main segment fetch**

In `backend/Streams/BufferedSegmentStream.cs`, find the main fetch method (around line 1040). Replace the initial rent and the resize loop:

```csharp
// Line ~1040 — initial buffer allocation
// BEFORE:
var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
// AFTER:
var buffer = new byte[1024 * 1024];

// Line ~1057 — resize when buffer is full
// BEFORE:
var newBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
Buffer.BlockCopy(buffer, 0, newBuffer, 0, totalRead);
ArrayPool<byte>.Shared.Return(buffer);
buffer = newBuffer;
// AFTER:
var newBuffer = new byte[buffer.Length * 2];
Buffer.BlockCopy(buffer, 0, newBuffer, 0, totalRead);
buffer = newBuffer;

// Line ~1095 — error path for incomplete segment
// BEFORE:
ArrayPool<byte>.Shared.Return(buffer);
throw new InvalidDataException(
// AFTER:
throw new InvalidDataException(

// Line ~1130 — catch-all error path
// BEFORE:
ArrayPool<byte>.Shared.Return(buffer);
throw;
// AFTER:
throw;
```

- [ ] **Step 2: Replace ArrayPool in zero-fill path**

In the graceful degradation path (around line 1286):

```csharp
// Line ~1286 — zero-fill buffer
// BEFORE:
var zeroBuffer = ArrayPool<byte>.Shared.Rent(zeroBufferSize);
// AFTER:
var zeroBuffer = new byte[zeroBufferSize];
```

- [ ] **Step 3: Replace ArrayPool in FetchSingleSegmentAsync**

In `FetchSingleSegmentAsync` (around line 1317):

```csharp
// Line ~1317 — initial buffer
// BEFORE:
var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
// AFTER:
var buffer = new byte[1024 * 1024];

// Line ~1325 — resize
// BEFORE:
var newBuffer = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
Buffer.BlockCopy(buffer, 0, newBuffer, 0, totalRead);
ArrayPool<byte>.Shared.Return(buffer);
buffer = newBuffer;
// AFTER:
var newBuffer = new byte[buffer.Length * 2];
Buffer.BlockCopy(buffer, 0, newBuffer, 0, totalRead);
buffer = newBuffer;

// Line ~1338 — error path
// BEFORE:
ArrayPool<byte>.Shared.Return(buffer);
throw;
// AFTER:
throw;
```

- [ ] **Step 4: Update PooledSegmentData.Dispose**

In the `PooledSegmentData` class (around line 1626):

```csharp
// BEFORE:
public void Dispose()
{
    if (_buffer != null)
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = null;
    }
}
// AFTER:
public void Dispose()
{
    _buffer = null;
}
```

- [ ] **Step 5: Remove unused ArrayPool import**

At line 1 of `BufferedSegmentStream.cs`:

```csharp
// BEFORE:
using System.Buffers;
// AFTER:
// (remove this line entirely)
```

- [ ] **Step 6: Build and verify**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet build --no-restore backend/NzbWebDAV.csproj`
Expected: 0 errors

- [ ] **Step 7: Commit**

```bash
git add backend/Streams/BufferedSegmentStream.cs
git commit -m "fix: replace ArrayPool with direct allocation in BufferedSegmentStream

ArrayPool<byte>.Shared retains returned buffers indefinitely, preventing
memory from returning to the OS. Direct allocation lets GC reclaim segment
buffers after stream disposal — critical for 1GB VPS deployments."
```

---

### Task 2: Add ConfigManager method for max concurrent buffered streams

**Files:**
- Modify: `backend/Config/ConfigManager.cs`

- [ ] **Step 1: Add GetMaxConcurrentBufferedStreams method**

In `backend/Config/ConfigManager.cs`, add after the `GetStreamBufferSize()` method (around line 181):

```csharp
public int GetMaxConcurrentBufferedStreams()
{
    return int.Parse(
        StringUtil.EmptyToNull(GetConfigValue("usenet.max-concurrent-buffered-streams"))
        ?? "2"
    );
}
```

- [ ] **Step 2: Build and verify**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet build --no-restore backend/NzbWebDAV.csproj`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add backend/Config/ConfigManager.cs
git commit -m "feat: add usenet.max-concurrent-buffered-streams config (default 2)"
```

---

### Task 3: Add concurrent stream cap to BufferedSegmentStream (Fix C)

**Files:**
- Modify: `backend/Streams/BufferedSegmentStream.cs`
- Modify: `backend/Streams/NzbFileStream.cs`
- Modify: `backend/Program.cs`

- [ ] **Step 1: Add static semaphore and slot methods to BufferedSegmentStream**

In `backend/Streams/BufferedSegmentStream.cs`, add these static members after the class declaration (around line 16, after `public class BufferedSegmentStream : Stream`):

```csharp
// Concurrent stream cap — limits how many BufferedSegmentStreams can exist simultaneously
private static SemaphoreSlim s_concurrentStreamSlots = new(2, 2);
private bool _holdsSlot;

public static void SetMaxConcurrentStreams(int max)
{
    s_concurrentStreamSlots = new SemaphoreSlim(max, max);
}

public static bool TryAcquireSlot()
{
    return s_concurrentStreamSlots.Wait(0);
}
```

- [ ] **Step 2: Track slot ownership in constructor and release in disposal**

In the constructor (around line 330, where the `BufferedSegmentStream` is initialized), add after the existing initialization code:

```csharp
_holdsSlot = true; // Caller must have acquired a slot via TryAcquireSlot()
```

In `DisposeAsync()` (the async disposal method), add slot release after the existing cleanup, just before `_disposed = true`:

```csharp
// Release concurrent stream slot
if (_holdsSlot)
{
    _holdsSlot = false;
    s_concurrentStreamSlots.Release();
}
```

In `Dispose(bool disposing)` (the synchronous disposal method), add the same slot release inside the `if (disposing)` block, just before closing the block:

```csharp
// Release concurrent stream slot
if (_holdsSlot)
{
    _holdsSlot = false;
    s_concurrentStreamSlots.Release();
}
```

- [ ] **Step 3: Add slot check in NzbFileStream before creating BufferedSegmentStream**

In `backend/Streams/NzbFileStream.cs`, modify the buffered streaming decision (around line 267). Change:

```csharp
// BEFORE:
// Use buffered streaming if configured for better performance
if (shouldUseBufferedStreaming && _concurrentConnections >= 3 && _fileSegmentIds.Length > _concurrentConnections)
{
```

to:

```csharp
// AFTER:
// Use buffered streaming if configured, enough connections, and a slot is available
if (shouldUseBufferedStreaming && _concurrentConnections >= 3 && _fileSegmentIds.Length > _concurrentConnections
    && BufferedSegmentStream.TryAcquireSlot())
{
```

No other changes needed in NzbFileStream — the existing unbuffered fallback path at line 343+ handles the case when the slot check fails.

- [ ] **Step 4: Wire up config in Program.cs**

In `backend/Program.cs`, add after the existing `OnConfigChanged` handler (around line 204):

```csharp
// Set initial concurrent buffered stream cap
BufferedSegmentStream.SetMaxConcurrentStreams(configManager.GetMaxConcurrentBufferedStreams());

// Update on config change
configManager.OnConfigChanged += (_, eventArgs) =>
{
    if (eventArgs.NewConfig.ContainsKey("usenet.max-concurrent-buffered-streams"))
    {
        BufferedSegmentStream.SetMaxConcurrentStreams(configManager.GetMaxConcurrentBufferedStreams());
    }
};
```

Add the required using at the top of `Program.cs` if not already present:

```csharp
using NzbWebDAV.Streams;
```

- [ ] **Step 5: Build and verify**

Run: `/opt/homebrew/opt/dotnet/bin/dotnet build --no-restore backend/NzbWebDAV.csproj`
Expected: 0 errors

- [ ] **Step 6: Commit**

```bash
git add backend/Streams/BufferedSegmentStream.cs backend/Streams/NzbFileStream.cs backend/Program.cs
git commit -m "feat: cap concurrent BufferedSegmentStreams to prevent retry storm memory growth

When the cap is reached, new streams fall through to the unbuffered
sequential path instead of allocating another full buffer. Configurable
via usenet.max-concurrent-buffered-streams (default: 2)."
```
