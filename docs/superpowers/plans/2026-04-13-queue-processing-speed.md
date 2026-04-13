# Queue Processing Speed Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce queue processing time from 7+ minutes to <20 seconds for 60-part RAR NZBs by replacing hard-partitioned connection limits with hybrid priority-based pooling, enabling buffered RAR reads, adding article caching, and raising concurrency caps.

**Architecture:** Replace `GlobalOperationLimiter`'s 4 fixed semaphores with a single `PrioritizedSemaphore` (ported from original nzbdav) that shares all connections and uses priority-based scheduling (streaming=High, queue=Low). Add a streaming reserve floor. Port `ArticleCachingNntpClient` from original nzbdav to eliminate redundant segment fetches between queue steps. Introduce `QueueRarProcessing` usage type to allow buffered streaming during RAR header parsing. Raise all queue concurrency caps to match the shared pool capacity.

**Tech Stack:** C# / .NET 10, SemaphoreSlim, ConcurrentDictionary, Interlocked, FileStream temp caching

**Spec:** `docs/superpowers/specs/2026-04-13-queue-processing-speed-design.md`

---

### Task 1: Port PrioritizedSemaphore and supporting types

**Files:**
- Create: `backend/Clients/Usenet/Concurrency/PrioritizedSemaphore.cs`
- Create: `backend/Clients/Usenet/Concurrency/SemaphorePriority.cs`
- Create: `backend/Clients/Usenet/Concurrency/SemaphorePriorityOdds.cs`

- [ ] **Step 1: Create the Concurrency directory**

```bash
mkdir -p /Users/dgherman/Documents/projects/nzbdav2/backend/Clients/Usenet/Concurrency
```

- [ ] **Step 2: Create `SemaphorePriority.cs`**

```csharp
// backend/Clients/Usenet/Concurrency/SemaphorePriority.cs
namespace NzbWebDAV.Clients.Usenet.Concurrency;

public enum SemaphorePriority
{
    Low,
    High
}
```

- [ ] **Step 3: Create `SemaphorePriorityOdds.cs`**

```csharp
// backend/Clients/Usenet/Concurrency/SemaphorePriorityOdds.cs
namespace NzbWebDAV.Clients.Usenet.Concurrency;

/// <summary>
/// Configures the odds of high-priority vs low-priority waiters winning contention.
/// HighPriorityOdds of 80 means streaming wins 80% of the time when both are waiting.
/// </summary>
public class SemaphorePriorityOdds
{
    public int HighPriorityOdds { get; set; } = 100;
    public int LowPriorityOdds => 100 - HighPriorityOdds;
}
```

- [ ] **Step 4: Create `PrioritizedSemaphore.cs`**

Port from `/Users/dgherman/Documents/projects/nzbdav/backend/Clients/Usenet/Concurrency/PrioritizedSemaphore.cs`. Adapt the namespace from `NzbWebDAV.Clients.Usenet.Concurrency` (same in both projects, so it's mostly a copy). The original uses `UsenetSharp.Concurrency.AsyncSemaphore` in its `ObjectDisposedException` — replace with `nameof(PrioritizedSemaphore)`.

The full source is in the original nzbdav repo at the path above. Key elements to preserve:
- Dual `LinkedList<TaskCompletionSource<bool>>` queues (high/low priority)
- `_accumulatedOdds` mechanism for fair probabilistic scheduling
- `CancellationToken` support with registration cleanup
- `UpdateMaxAllowed(int)` and `UpdatePriorityOdds(SemaphorePriorityOdds)` methods
- Thread-safe `Lock` (use `object _lock` if targeting older .NET — original uses `Lock`)

```csharp
// backend/Clients/Usenet/Concurrency/PrioritizedSemaphore.cs
namespace NzbWebDAV.Clients.Usenet.Concurrency;

public class PrioritizedSemaphore : IDisposable
{
    private readonly LinkedList<TaskCompletionSource<bool>> _highPriorityWaiters = [];
    private readonly LinkedList<TaskCompletionSource<bool>> _lowPriorityWaiters = [];
    private SemaphorePriorityOdds _priorityOdds;
    private int _maxAllowed;
    private int _enteredCount;
    private bool _disposed = false;
    private readonly Lock _lock = new();
    private int _accumulatedOdds;

    public PrioritizedSemaphore(int initialAllowed, int maxAllowed, SemaphorePriorityOdds? priorityOdds = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialAllowed);
        ArgumentOutOfRangeException.ThrowIfNegative(maxAllowed);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(initialAllowed, maxAllowed);
        _priorityOdds = priorityOdds ?? new SemaphorePriorityOdds { HighPriorityOdds = 100 };
        _enteredCount = maxAllowed - initialAllowed;
        _maxAllowed = maxAllowed;
    }

    public int CurrentCount
    {
        get { lock (_lock) { return _maxAllowed - _enteredCount; } }
    }

    public Task WaitAsync(SemaphorePriority priority, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PrioritizedSemaphore));

            if (_enteredCount < _maxAllowed)
            {
                _enteredCount++;
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queue = priority == SemaphorePriority.High ? _highPriorityWaiters : _lowPriorityWaiters;
            var node = queue.AddLast(tcs);

            if (cancellationToken.CanBeCanceled)
            {
                var registration = cancellationToken.Register(() =>
                {
                    var removed = false;
                    lock (_lock)
                    {
                        try
                        {
                            queue.Remove(node);
                            removed = true;
                        }
                        catch (InvalidOperationException)
                        {
                            // intentionally left blank
                        }
                    }

                    if (removed)
                        tcs.TrySetCanceled(cancellationToken);
                });

                tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
            }

            return tcs.Task;
        }
    }

    public void Release()
    {
        TaskCompletionSource<bool>? toRelease;
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PrioritizedSemaphore));

            if (_enteredCount > _maxAllowed)
            {
                toRelease = null;
            }
            else if (_highPriorityWaiters.Count == 0)
            {
                toRelease = Release(_lowPriorityWaiters);
            }
            else if (_lowPriorityWaiters.Count == 0)
            {
                toRelease = Release(_highPriorityWaiters);
            }
            else
            {
                _accumulatedOdds += _priorityOdds.LowPriorityOdds;
                var (one, two) = (_highPriorityWaiters, _lowPriorityWaiters);
                if (_accumulatedOdds >= 100)
                {
                    (one, two) = (two, one);
                    _accumulatedOdds -= 100;
                }

                toRelease = Release(one) ?? Release(two);
            }

            if (toRelease == null)
            {
                _enteredCount--;
                if (_enteredCount < 0)
                {
                    throw new InvalidOperationException("The semaphore cannot be further released.");
                }

                return;
            }
        }

        toRelease.TrySetResult(true);
    }

    private static TaskCompletionSource<bool>? Release(LinkedList<TaskCompletionSource<bool>> queue)
    {
        while (queue.Count > 0)
        {
            var node = queue.First!;
            queue.RemoveFirst();

            if (!node.Value.Task.IsCanceled)
            {
                return node.Value;
            }
        }

        return null;
    }

    public void UpdateMaxAllowed(int newMaxAllowed)
    {
        lock (_lock)
        {
            _maxAllowed = newMaxAllowed;
        }
    }

    public void UpdatePriorityOdds(SemaphorePriorityOdds newPriorityOdds)
    {
        lock (_lock)
        {
            _priorityOdds = newPriorityOdds;
        }
    }

    public void Dispose()
    {
        List<TaskCompletionSource<bool>> waitersToCancel;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            waitersToCancel = _highPriorityWaiters.Concat(_lowPriorityWaiters).ToList();
            _highPriorityWaiters.Clear();
            _lowPriorityWaiters.Clear();
        }

        foreach (var tcs in waitersToCancel)
            tcs.TrySetException(new ObjectDisposedException(nameof(PrioritizedSemaphore)));
    }
}
```

- [ ] **Step 5: Verify it compiles**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2 && /opt/homebrew/opt/dotnet/bin/dotnet build backend/NzbWebDAV.csproj --no-restore 2>&1 | tail -5
```

Expected: Build succeeded (new files are not referenced yet, so no errors).

- [ ] **Step 6: Commit**

```bash
git add backend/Clients/Usenet/Concurrency/
git commit -m "feat: add PrioritizedSemaphore and supporting types

Ported from original nzbdav. Dual-queue semaphore with configurable
priority odds for High (streaming) vs Low (queue) operations."
```

---

### Task 2: Add config methods and QueueRarProcessing usage type

**Files:**
- Modify: `backend/Config/ConfigManager.cs:253-259` — add new config methods
- Modify: `backend/Clients/Usenet/Connections/ConnectionUsageContext.cs:139-149` — add QueueRarProcessing enum value

- [ ] **Step 1: Add `QueueRarProcessing` to `ConnectionUsageType` enum**

In `backend/Clients/Usenet/Connections/ConnectionUsageContext.cs`, add the new enum value after `QueueAnalysis = 7`:

```csharp
// Add after QueueAnalysis = 7
QueueRarProcessing = 8
```

- [ ] **Step 2: Add config methods to `ConfigManager.cs`**

After the existing `GetMaxQueueConnections()` method (line 253), add:

```csharp
public int GetStreamingReserve()
{
    return int.Parse(
        StringUtil.EmptyToNull(GetConfigValue("usenet.streaming-reserve"))
        ?? "5"
    );
}

public SemaphorePriorityOdds GetStreamingPriority()
{
    var stringValue = StringUtil.EmptyToNull(GetConfigValue("usenet.streaming-priority"));
    var numericalValue = int.Parse(stringValue ?? "80");
    return new SemaphorePriorityOdds() { HighPriorityOdds = numericalValue };
}

public int GetMaxDownloadConnections()
{
    var stringValue = StringUtil.EmptyToNull(GetConfigValue("usenet.max-download-connections"));
    if (stringValue != null)
    {
        return int.Parse(stringValue);
    }
    var providerConfig = GetUsenetProviderConfig();
    return Math.Min(providerConfig.TotalPooledConnections, 15);
}
```

Add the required using at the top of `ConfigManager.cs`:

```csharp
using NzbWebDAV.Clients.Usenet.Concurrency;
```

- [ ] **Step 3: Verify it compiles**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2 && /opt/homebrew/opt/dotnet/bin/dotnet build backend/NzbWebDAV.csproj --no-restore 2>&1 | tail -5
```

Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add backend/Config/ConfigManager.cs backend/Clients/Usenet/Connections/ConnectionUsageContext.cs
git commit -m "feat: add QueueRarProcessing usage type and config methods

New config: usenet.streaming-reserve (default 5), usenet.streaming-priority
(default 80), usenet.max-download-connections (default min(totalPooled, 15)).
QueueRarProcessing allows buffered streaming during RAR header parsing."
```

---

### Task 3: Rewrite GlobalOperationLimiter to use PrioritizedSemaphore

**Files:**
- Modify: `backend/Clients/Usenet/Connections/GlobalOperationLimiter.cs` — full rewrite of internals
- Modify: `backend/Clients/Usenet/UsenetStreamingClient.cs:489-497` — update constructor call

- [ ] **Step 1: Rewrite `GlobalOperationLimiter`**

Replace the entire class body. The public API stays the same: `AcquirePermitAsync(ConnectionUsageType, CancellationToken)` returns `OperationPermit`, which disposes to release.

Key changes:
- Remove 4 `SemaphoreSlim` fields, replace with 1 `PrioritizedSemaphore _sharedPool`
- Add `_lowPriorityReserveGate` (`SemaphoreSlim`) sized to `totalConnections - streamingReserve` — Low-priority callers acquire this BEFORE the shared pool, High-priority callers skip it
- Add `int _activeLowPriorityCount` tracked via `Interlocked`
- Constructor takes `totalConnections`, `streamingReserve`, `SemaphorePriorityOdds`, `ConfigManager?`
- Map `ConnectionUsageType` to `SemaphorePriority`: Streaming/BufferedStreaming → High, everything else → Low

```csharp
// backend/Clients/Usenet/Connections/GlobalOperationLimiter.cs
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Logging;
using Serilog;

namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Global operation limiter using a shared PrioritizedSemaphore with streaming reserve.
/// Streaming (High priority) always has guaranteed slots. Queue (Low priority) can use
/// the full pool when no streaming is active, but yields gracefully under contention.
/// </summary>
public class GlobalOperationLimiter : IDisposable
{
    private readonly PrioritizedSemaphore _sharedPool;
    private readonly SemaphoreSlim _lowPriorityGate;
    private readonly Dictionary<ConnectionUsageType, int> _currentUsage = new();
    private readonly object _lock = new();
    private readonly int _totalConnections;
    private readonly int _streamingReserve;
    private readonly ConfigManager? _configManager;

    public GlobalOperationLimiter(
        int totalConnections,
        int streamingReserve,
        SemaphorePriorityOdds priorityOdds,
        ConfigManager? configManager = null)
    {
        _configManager = configManager;
        _totalConnections = Math.Max(1, totalConnections);
        _streamingReserve = Math.Max(1, Math.Min(streamingReserve, _totalConnections - 1));

        _sharedPool = new PrioritizedSemaphore(_totalConnections, _totalConnections, priorityOdds);

        // Low-priority gate: allows up to (total - reserve) concurrent low-priority operations.
        // This guarantees that 'streamingReserve' slots are always available for High-priority.
        var lowPriorityMax = _totalConnections - _streamingReserve;
        _lowPriorityGate = new SemaphoreSlim(lowPriorityMax, lowPriorityMax);

        // Initialize usage tracking for all known types
        foreach (var type in Enum.GetValues<ConnectionUsageType>())
        {
            _currentUsage[type] = 0;
        }

        Log.Information("[GlobalPool] Initialized: TotalConnections={Total}, StreamingReserve={Reserve}, LowPriorityMax={LowMax}, StreamingPriority={Priority}%",
            _totalConnections, _streamingReserve, lowPriorityMax, priorityOdds.HighPriorityOdds);
    }

    // Backwards-compatible constructor for callers that haven't been updated yet
    public GlobalOperationLimiter(
        int maxQueueConnections,
        int maxHealthCheckConnections,
        int totalConnections,
        ConfigManager? configManager = null)
        : this(
            totalConnections,
            streamingReserve: configManager?.GetStreamingReserve() ?? 5,
            priorityOdds: configManager?.GetStreamingPriority() ?? new SemaphorePriorityOdds { HighPriorityOdds = 80 },
            configManager)
    {
        // Log deprecation if max-queue-connections was explicitly set
        if (configManager != null && maxQueueConnections != 1)
        {
            Log.Warning("[GlobalPool] api.max-queue-connections is deprecated. Queue now shares the full connection pool with priority-based scheduling. " +
                        "Use usenet.streaming-reserve (default 5) and usenet.streaming-priority (default 80) instead.");
        }
    }

    /// <summary>
    /// Acquires a permit for the given operation type. Must be released via OperationPermit.Dispose().
    /// </summary>
    public async Task<OperationPermit> AcquirePermitAsync(ConnectionUsageType usageType, CancellationToken cancellationToken = default)
    {
        var priority = GetPriorityForType(usageType);

        var context = cancellationToken.GetContext<ConnectionUsageContext>();
        var fileDetails = context.Details;

        LogDebugForType(usageType, "Requesting permit for {UsageType}. Priority: {Priority}. Current usage: {UsageBreakdown}",
            usageType, priority, GetUsageBreakdown());

        var waitStartTime = DateTime.UtcNow;

        // Low-priority callers must first acquire the reserve gate to ensure
        // streaming always has 'streamingReserve' slots available
        bool acquiredLowPriorityGate = false;
        if (priority == SemaphorePriority.Low)
        {
            await _lowPriorityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquiredLowPriorityGate = true;
        }

        try
        {
            // Acquire from the shared pool with appropriate priority
            await _sharedPool.WaitAsync(priority, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // If shared pool acquisition fails (cancellation), release the low-priority gate
            if (acquiredLowPriorityGate)
                _lowPriorityGate.Release();
            throw;
        }

        var waitElapsed = DateTime.UtcNow - waitStartTime;

        // Track usage
        int currentUsage;
        lock (_lock)
        {
            _currentUsage[usageType]++;
            currentUsage = _currentUsage[usageType];
        }

        if (waitElapsed.TotalSeconds > 2)
        {
            if (fileDetails != null)
            {
                Log.Debug("[GlobalPool] Acquired permit for {UsageType} after waiting {WaitSeconds:F1}s. File: {FileDetails}. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
                    usageType, waitElapsed.TotalSeconds, fileDetails, currentUsage, GetUsageBreakdown());
            }
            else
            {
                Log.Debug("[GlobalPool] Acquired permit for {UsageType} after waiting {WaitSeconds:F1}s. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
                    usageType, waitElapsed.TotalSeconds, currentUsage, GetUsageBreakdown());
            }
        }
        else
        {
            if (fileDetails != null)
            {
                LogDebugForType(usageType, "Acquired permit for {UsageType}. File: {FileDetails}. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
                    usageType, fileDetails, currentUsage, GetUsageBreakdown());
            }
            else
            {
                LogDebugForType(usageType, "Acquired permit for {UsageType}. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
                    usageType, currentUsage, GetUsageBreakdown());
            }
        }

        return new OperationPermit(this, usageType, acquiredLowPriorityGate, DateTime.UtcNow, fileDetails);
    }

    private void ReleasePermit(ConnectionUsageType usageType, bool releaseLowPriorityGate, DateTime acquiredAt, string? fileDetails)
    {
        var heldDuration = DateTime.UtcNow - acquiredAt;

        int currentUsage;
        lock (_lock)
        {
            if (_currentUsage.ContainsKey(usageType) && _currentUsage[usageType] > 0)
            {
                _currentUsage[usageType]--;
            }
            else
            {
                Log.Error("[GlobalPool] CRITICAL: Attempted to release permit for {UsageType} but usage counter is already 0!",
                    usageType);
            }
            currentUsage = _currentUsage[usageType];
        }

        // Release shared pool first, then low-priority gate
        _sharedPool.Release();
        if (releaseLowPriorityGate)
            _lowPriorityGate.Release();

        if (heldDuration.TotalMinutes > 5)
        {
            if (fileDetails != null)
            {
                Log.Warning("[GlobalPool] Released permit for {UsageType} after holding for {HeldMinutes:F1} minutes. File: {FileDetails}. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
                    usageType, heldDuration.TotalMinutes, fileDetails, currentUsage, GetUsageBreakdown());
            }
            else
            {
                Log.Warning("[GlobalPool] Released permit for {UsageType} after holding for {HeldMinutes:F1} minutes. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
                    usageType, heldDuration.TotalMinutes, currentUsage, GetUsageBreakdown());
            }
        }
        else if (heldDuration.TotalSeconds > 30)
        {
            if (fileDetails != null)
            {
                LogInfoForType(usageType, "Released permit for {UsageType} after {HeldSeconds:F1}s. File: {FileDetails}. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
                    usageType, heldDuration.TotalSeconds, fileDetails, currentUsage, GetUsageBreakdown());
            }
            else
            {
                LogInfoForType(usageType, "Released permit for {UsageType} after {HeldSeconds:F1}s. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
                    usageType, heldDuration.TotalSeconds, currentUsage, GetUsageBreakdown());
            }
        }
        else
        {
            if (fileDetails != null)
            {
                LogDebugForType(usageType, "Released permit for {UsageType} after {HeldSeconds:F1}s. File: {FileDetails}. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
                    usageType, heldDuration.TotalSeconds, fileDetails, currentUsage, GetUsageBreakdown());
            }
            else
            {
                LogDebugForType(usageType, "Released permit for {UsageType} after {HeldSeconds:F1}s. Current usage: {CurrentUsage}. Total: {UsageBreakdown}",
                    usageType, heldDuration.TotalSeconds, currentUsage, GetUsageBreakdown());
            }
        }
    }

    private static SemaphorePriority GetPriorityForType(ConnectionUsageType type)
    {
        return type switch
        {
            ConnectionUsageType.Streaming => SemaphorePriority.High,
            ConnectionUsageType.BufferedStreaming => SemaphorePriority.High,
            _ => SemaphorePriority.Low
        };
    }

    private string GetUsageBreakdown()
    {
        lock (_lock)
        {
            var parts = _currentUsage
                .Where(kvp => kvp.Value > 0)
                .Select(kvp => $"{kvp.Key}={kvp.Value}")
                .ToArray();
            return parts.Length > 0 ? string.Join(",", parts) : "none";
        }
    }

    private void LogDebugForType(ConnectionUsageType usageType, string message, params object[] args)
    {
        if (_configManager == null)
        {
            Log.Debug("[GlobalPool] " + message, args);
            return;
        }

        var component = GetComponentForType(usageType);
        if (_configManager.IsDebugLogEnabled(component))
        {
            Log.Debug("[GlobalPool] " + message, args);
        }
    }

    private void LogInfoForType(ConnectionUsageType usageType, string message, params object[] args)
    {
        if (usageType == ConnectionUsageType.HealthCheck ||
            usageType == ConnectionUsageType.Repair ||
            usageType == ConnectionUsageType.Analysis ||
            usageType == ConnectionUsageType.QueueAnalysis ||
            usageType == ConnectionUsageType.Streaming ||
            usageType == ConnectionUsageType.BufferedStreaming)
        {
            LogDebugForType(usageType, message, args);
            return;
        }

        Log.Information("[GlobalPool] " + message, args);
    }

    private static string GetComponentForType(ConnectionUsageType usageType)
    {
        return usageType switch
        {
            ConnectionUsageType.Queue => LogComponents.Queue,
            ConnectionUsageType.QueueRarProcessing => LogComponents.Queue,
            ConnectionUsageType.QueueAnalysis => LogComponents.Queue,
            ConnectionUsageType.HealthCheck => LogComponents.HealthCheck,
            ConnectionUsageType.Repair => LogComponents.HealthCheck,
            ConnectionUsageType.Analysis => LogComponents.Analysis,
            ConnectionUsageType.Streaming => LogComponents.BufferedStream,
            ConnectionUsageType.BufferedStreaming => LogComponents.BufferedStream,
            _ => LogComponents.Usenet
        };
    }

    public void Dispose()
    {
        _sharedPool.Dispose();
        _lowPriorityGate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Represents a permit to perform an operation. Must be disposed to release the permit.
    /// </summary>
    public sealed class OperationPermit : IDisposable
    {
        private readonly GlobalOperationLimiter _limiter;
        private readonly ConnectionUsageType _usageType;
        private readonly bool _releaseLowPriorityGate;
        private readonly DateTime _acquiredAt;
        private readonly string? _fileDetails;
        private int _disposed;

        internal OperationPermit(GlobalOperationLimiter limiter, ConnectionUsageType usageType, bool releaseLowPriorityGate, DateTime acquiredAt, string? fileDetails)
        {
            _limiter = limiter;
            _usageType = usageType;
            _releaseLowPriorityGate = releaseLowPriorityGate;
            _acquiredAt = acquiredAt;
            _fileDetails = fileDetails;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _limiter.ReleasePermit(_usageType, _releaseLowPriorityGate, _acquiredAt, _fileDetails);
            }
        }
    }
}
```

- [ ] **Step 2: Verify it compiles**

The old constructor signature `(int maxQueueConnections, int maxHealthCheckConnections, int totalConnections, ConfigManager?)` is preserved as a backwards-compatible overload, so `UsenetStreamingClient.cs:492` doesn't need changes yet.

```bash
cd /Users/dgherman/Documents/projects/nzbdav2 && /opt/homebrew/opt/dotnet/bin/dotnet build backend/NzbWebDAV.csproj --no-restore 2>&1 | tail -10
```

Expected: Build succeeded. If there are errors from the `OperationPermit` constructor change (no longer taking a `SemaphoreSlim`), fix any callers.

- [ ] **Step 3: Commit**

```bash
git add backend/Clients/Usenet/Connections/GlobalOperationLimiter.cs
git commit -m "feat: replace hard-partitioned semaphores with PrioritizedSemaphore

GlobalOperationLimiter now uses a single shared pool with priority-based
scheduling. Streaming (High) gets guaranteed reserve slots. Queue (Low)
can use the full pool when idle, yields under contention. Old constructor
preserved for backwards compatibility."
```

---

### Task 4: Enable buffered streaming for QueueRarProcessing

**Files:**
- Modify: `backend/Queue/FileProcessors/RarProcessor.cs:240-268` — switch usage context to `QueueRarProcessing`

- [ ] **Step 1: Update `GetFastNzbFileStream` in `RarProcessor.cs`**

At line 256 of `RarProcessor.cs`, the code reads:
```csharp
var usageContext = ct.GetContext<ConnectionUsageContext>();
```

This inherits the parent Queue context. Replace with a new `QueueRarProcessing` context:

```csharp
// Create a QueueRarProcessing context so NzbFileStream allows buffered streaming
// (Queue and QueueAnalysis contexts disable buffering, but QueueRarProcessing does not)
var parentContext = ct.GetContext<ConnectionUsageContext>();
var usageContext = new ConnectionUsageContext(ConnectionUsageType.QueueRarProcessing, parentContext.Details);
```

- [ ] **Step 2: Verify it compiles**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2 && /opt/homebrew/opt/dotnet/bin/dotnet build backend/NzbWebDAV.csproj --no-restore 2>&1 | tail -5
```

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add backend/Queue/FileProcessors/RarProcessor.cs
git commit -m "feat: use QueueRarProcessing context for buffered RAR header reads

RAR header parsing now uses QueueRarProcessing usage type instead of
inheriting Queue context. This allows NzbFileStream to use buffered
multi-connection streaming during RAR processing, enabling pre-fetched
segment reads instead of one-at-a-time lazy fetches."
```

---

### Task 5: Port ArticleCachingNntpClient

**Files:**
- Create: `backend/Clients/Usenet/ArticleCachingNntpClient.cs`
- Modify: `backend/Queue/QueueManager.cs:212-216` — wrap usenet client

- [ ] **Step 1: Create `ArticleCachingNntpClient.cs`**

Adapted from `/Users/dgherman/Documents/projects/nzbdav/backend/Clients/Usenet/ArticleCachingNntpClient.cs` for nzbdav2's interface (nzbdav2 uses `GetSegmentStreamAsync` returning `YencHeaderStream` instead of `DecodedBodyAsync` returning `UsenetDecodedBodyResponse`). Also cache `GetSegmentYencHeaderAsync` results.

```csharp
// backend/Clients/Usenet/ArticleCachingNntpClient.cs
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// Per-queue-item INntpClient wrapper that caches decoded segments to temp files.
/// Eliminates redundant network fetches when the same segment is read in multiple
/// queue processing steps (e.g., Step 1 first-segment fetch reused by Step 2 RAR parsing).
/// Short-lived: deletes all cached data on disposal.
/// </summary>
public class ArticleCachingNntpClient : WrappingNntpClient
{
    private readonly string _cacheDir = Directory.CreateTempSubdirectory("nzbdav-queue-cache-").FullName;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, CacheEntry> _cachedSegments = new();

    private record CacheEntry(UsenetYencHeader YencHeaders, UsenetArticleHeaders? ArticleHeaders);

    public ArticleCachingNntpClient(INntpClient usenetClient, bool leaveOpen = true)
        : base(usenetClient)
    {
        _leaveOpen = leaveOpen;
    }

    private readonly bool _leaveOpen;

    public override async Task<YencHeaderStream> GetSegmentStreamAsync(
        string segmentId, bool includeHeaders, CancellationToken ct)
    {
        var semaphore = _pendingRequests.GetOrAdd(segmentId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // Check if already cached
            if (_cachedSegments.TryGetValue(segmentId, out var existingEntry))
            {
                return ReadFromCache(segmentId, existingEntry);
            }

            // Fetch from usenet and cache
            var response = await base.GetSegmentStreamAsync(segmentId, includeHeaders, ct).ConfigureAwait(false);

            // Read yenc headers before consuming the stream
            var yencHeaders = response.Header;
            var articleHeaders = response.ArticleHeaders;

            // Cache the decoded stream to disk
            var cachePath = GetCachePath(segmentId);
            await using (var fileStream = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                await response.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            }
            await response.DisposeAsync().ConfigureAwait(false);

            // Store cache entry
            _cachedSegments.TryAdd(segmentId, new CacheEntry(yencHeaders, articleHeaders));

            // Return a new stream from the cached file
            return ReadFromCache(segmentId, new CacheEntry(yencHeaders, articleHeaders));
        }
        finally
        {
            semaphore.Release();
        }
    }

    public override Task<UsenetYencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken cancellationToken)
    {
        // Return cached yenc headers if available (avoids network round-trip for interpolation search)
        return _cachedSegments.TryGetValue(segmentId, out var existingEntry)
            ? Task.FromResult(existingEntry.YencHeaders)
            : base.GetSegmentYencHeaderAsync(segmentId, cancellationToken);
    }

    private YencHeaderStream ReadFromCache(string segmentId, CacheEntry entry)
    {
        var cachePath = GetCachePath(segmentId);
        var fileStream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        return new YencHeaderStream(entry.YencHeaders, entry.ArticleHeaders, fileStream);
    }

    private string GetCachePath(string segmentId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(segmentId));
        var filename = Convert.ToHexString(hash);
        return Path.Combine(_cacheDir, filename);
    }

    public override void Dispose()
    {
        if (!_leaveOpen)
            base.Dispose();

        foreach (var semaphore in _pendingRequests.Values)
            semaphore.Dispose();
        _pendingRequests.Clear();
        _cachedSegments.Clear();

        // Clean up cache directory in background
        Task.Run(async () => await DeleteCacheDir(_cacheDir));
        GC.SuppressFinalize(this);
    }

    private static async Task DeleteCacheDir(string cacheDir)
    {
        var ct = SigtermUtil.GetCancellationToken();
        var delay = 1000;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Directory.Delete(cacheDir, recursive: true);
                return;
            }
            catch (Exception)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = Math.Min(delay * 2, 10000);
            }
        }
    }
}
```

- [ ] **Step 2: Verify the `SigtermUtil.GetCancellationToken()` reference exists**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2 && grep -r "GetCancellationToken" backend/Utils/SigtermUtil.cs 2>/dev/null || grep -rn "class SigtermUtil" backend/ | head -3
```

If `SigtermUtil` doesn't exist in nzbdav2, replace the `DeleteCacheDir` method with a simpler version that doesn't use it:

```csharp
private static async Task DeleteCacheDir(string cacheDir)
{
    var delay = 1000;
    for (var i = 0; i < 5; i++)
    {
        try
        {
            Directory.Delete(cacheDir, recursive: true);
            return;
        }
        catch (Exception)
        {
            await Task.Delay(delay).ConfigureAwait(false);
            delay = Math.Min(delay * 2, 10000);
        }
    }
}
```

- [ ] **Step 3: Understand the integration approach**

`QueueItemProcessor` takes `UsenetStreamingClient` (concrete type), which holds `CachingNntpClient _client` (also concrete). We can't swap the client at the `QueueManager` level. Instead: add an `_articleCache` field to `UsenetStreamingClient` with `EnableArticleCaching()` returning `IDisposable`. When active, `GetSegmentStreamAsync` routes through the cache first. When disposed, it clears. Steps 4 and 5 implement this.

- [ ] **Step 4: Add `EnableArticleCaching()` to `UsenetStreamingClient`**

In `backend/Clients/Usenet/UsenetStreamingClient.cs`, add a field and method:

After the existing fields (around line 26), add:

```csharp
private ArticleCachingNntpClient? _articleCache;
```

Add the method (anywhere in the class):

```csharp
/// <summary>
/// Enables article caching for the duration of a queue item's processing.
/// Cached segments are reused across steps (e.g., first-segment fetch in Step 1
/// reused by RAR header parsing in Step 2). Dispose to clean up temp files.
/// </summary>
public IDisposable EnableArticleCaching()
{
    var cache = new ArticleCachingNntpClient(_client, leaveOpen: true);
    _articleCache = cache;
    return new DisposableAction(() =>
    {
        _articleCache = null;
        cache.Dispose();
    });
}

private class DisposableAction(Action action) : IDisposable
{
    public void Dispose() => action();
}
```

Then modify `GetSegmentStreamAsync` (line 312-315) from:

```csharp
public Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders, CancellationToken ct)
{
    return _client.GetSegmentStreamAsync(segmentId, includeHeaders, ct);
}
```

To:

```csharp
public Task<YencHeaderStream> GetSegmentStreamAsync(string segmentId, bool includeHeaders, CancellationToken ct)
{
    INntpClient client = _articleCache ?? _client;
    return client.GetSegmentStreamAsync(segmentId, includeHeaders, ct);
}
```

Also check if `GetSegmentYencHeaderAsync` exists on `UsenetStreamingClient` and route it through cache too. Search for any method that delegates to `_client.GetSegmentYencHeaderAsync` and apply the same pattern.

- [ ] **Step 5: Wire up in `QueueManager.BeginProcessingQueueItem`**

In `backend/Queue/QueueManager.cs`, inside `ProcessQueueAsync` (line 108), after the queue item is fetched and before processing begins, enable caching. Modify the section around line 150-158:

From:
```csharp
await LockAsync(() =>
{
    Log.Debug("[QueueManager] Beginning processing task for queue item: {QueueItemId}", queueItem.Id);
    _inProgressQueueItem = BeginProcessingQueueItem(
        _scopeFactory, queueItem, queueNzbContents, queueItemCancellationTokenSource
    );
}).ConfigureAwait(false);
```

To:
```csharp
var articleCacheScope = _usenetClient.EnableArticleCaching();
await LockAsync(() =>
{
    Log.Debug("[QueueManager] Beginning processing task for queue item: {QueueItemId}", queueItem.Id);
    _inProgressQueueItem = BeginProcessingQueueItem(
        _scopeFactory, queueItem, queueNzbContents, queueItemCancellationTokenSource
    );
}).ConfigureAwait(false);
```

Then in the `finally` block (around line 194), dispose it:

```csharp
finally
{
    Log.Debug("[QueueManager] Clearing in-progress queue item");
    await LockAsync(() => { _inProgressQueueItem = null; }).ConfigureAwait(false);
    articleCacheScope?.Dispose();
    Log.Debug("[QueueManager] Loop iteration complete, checking for next item...");
}
```

Declare `articleCacheScope` before the try block so it's accessible in finally:

```csharp
IDisposable? articleCacheScope = null;
try
{
    // ... existing code ...
    articleCacheScope = _usenetClient.EnableArticleCaching();
    // ... rest of processing ...
```

- [ ] **Step 6: Verify it compiles**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2 && /opt/homebrew/opt/dotnet/bin/dotnet build backend/NzbWebDAV.csproj --no-restore 2>&1 | tail -10
```

Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add backend/Clients/Usenet/ArticleCachingNntpClient.cs backend/Clients/Usenet/UsenetStreamingClient.cs backend/Queue/QueueManager.cs
git commit -m "feat: add article caching for queue processing

Port ArticleCachingNntpClient from original nzbdav. Caches decoded segments
to temp files per queue item, eliminating redundant Usenet fetches between
Step 1 (first-segment fetch) and Step 2 (RAR header parsing). Cache is
automatically cleaned up when queue item processing completes."
```

---

### Task 6: Update concurrency caps

**Files:**
- Modify: `backend/Queue/DeobfuscationSteps/1.FetchFirstSegment/FetchFirstSegmentsStep.cs:30`
- Modify: `backend/Queue/QueueItemProcessor.cs:164,258`

- [ ] **Step 1: Update `FetchFirstSegmentsStep.cs`**

At line 30, change:

```csharp
var maxConcurrency = configManager.GetMaxQueueConnections();
```

To:

```csharp
var maxConcurrency = configManager.GetMaxDownloadConnections() + 5;
```

- [ ] **Step 2: Update `QueueItemProcessor.cs` Step 1 concurrency**

At line 164, change:

```csharp
var concurrency = configManager.GetMaxQueueConnections();
```

To:

```csharp
var concurrency = configManager.GetMaxDownloadConnections() + 5;
```

Update the log message on line 165 to reflect the new variable meaning:

```csharp
Log.Information("[Queue] Processing '{JobName}': TotalConnections={TotalConnections}, DownloadConcurrency={Concurrency}", queueItem.JobName, providerConfig.TotalPooledConnections, concurrency);
```

- [ ] **Step 3: Update `QueueItemProcessor.cs` file processor concurrency**

At line 258, change:

```csharp
var fileConcurrency = Math.Max(1, Math.Min(concurrency, providerConfig.TotalPooledConnections / 5));
```

To:

```csharp
var fileConcurrency = configManager.GetMaxDownloadConnections() + 5;
```

- [ ] **Step 4: Verify it compiles**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2 && /opt/homebrew/opt/dotnet/bin/dotnet build backend/NzbWebDAV.csproj --no-restore 2>&1 | tail -5
```

Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add backend/Queue/DeobfuscationSteps/1.FetchFirstSegment/FetchFirstSegmentsStep.cs backend/Queue/QueueItemProcessor.cs
git commit -m "feat: raise queue concurrency caps to use shared pool capacity

FetchFirstSegmentsStep and QueueItemProcessor now use GetMaxDownloadConnections() + 5
instead of GetMaxQueueConnections() (which was 1). The PrioritizedSemaphore is the
real gate — these soft caps should be high enough to keep the pool saturated."
```

---

### Task 7: Update changelog and build version

**Files:**
- Modify: `README.md` — add changelog entry
- Modify: `backend/Program.cs` — update build version string

- [ ] **Step 1: Determine the correct version number**

Check the most recent CI build version:

```bash
cd /Users/dgherman/Documents/projects/nzbdav2 && git log --oneline -1
```

Count how many commits were added in Tasks 1-6 (should be 6). Check the current version in README.md changelog and increment PATCH by 6.

- [ ] **Step 2: Update `README.md` changelog**

Add a new entry at the top of the `## Changelog` section:

```markdown
## vX.Y.Z (2026-04-13)
- **Queue speed**: Replace hard-partitioned connection pool with priority-based shared pool — queue processing uses full connection capacity when not streaming
- **Queue speed**: Enable buffered multi-connection streaming during RAR header parsing via new `QueueRarProcessing` context
- **Queue speed**: Add per-queue-item article caching — segments fetched in Step 1 are reused in Step 2 without network round-trips
- **Queue speed**: Raise queue concurrency caps from 1 to `GetMaxDownloadConnections() + 5`
- **Config**: New `usenet.streaming-reserve` (default 5), `usenet.streaming-priority` (default 80), `usenet.max-download-connections`
- **Config**: `api.max-queue-connections` deprecated — logs warning if explicitly set
```

- [ ] **Step 3: Update build version in `backend/Program.cs`**

Find `BUILD v` string and update:

```
BUILD v2026-04-13-QUEUE-SPEED-HYBRID-POOL
```

- [ ] **Step 4: Commit**

```bash
git add README.md backend/Program.cs
git commit -m "docs: add vX.Y.Z changelog — hybrid pool, article caching, queue speed"
```

---

### Task 8: Push to test branch and provide test suggestions

- [ ] **Step 1: Push to a test branch**

```bash
cd /Users/dgherman/Documents/projects/nzbdav2 && git push origin main:queue-speed-fix
```

- [ ] **Step 2: Wait for CI build**

Check https://github.com/dgherman/nzbdav2/actions for the build to complete.

- [ ] **Step 3: Deploy test image and test**

Once CI builds, deploy the `queue-speed-fix` tag and test:

**Queue speed test:**
- Queue a 60-part RAR NZB (e.g., a 4K UHD BluRay remux)
- Watch logs for total processing time — target is <20 seconds
- Check that `[GlobalPool]` logs show shared pool with priority info instead of separate semaphores
- Verify article cache temp directory messages in logs

**Streaming priority test:**
- Queue an NZB and immediately start streaming a different file
- Verify streaming starts within 1-2 seconds (not blocked by queue work)
- Watch `[GlobalPool]` logs for priority scheduling messages

**Memory regression test:**
- Monitor container memory during queue processing
- Verify no regression from re-enabled buffered streaming for RAR headers
- Memory should stay similar to v0.7.0 levels

**Config test:**
- Set `api.max-queue-connections` explicitly and verify deprecation warning in logs
- Set `usenet.streaming-reserve` to different values and verify behavior

**Article cache cleanup test:**
- After queue item completes, verify no orphaned temp files remain
- Check logs for cache cleanup messages
