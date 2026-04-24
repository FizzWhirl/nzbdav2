# Feature Report — Prioritized Semaphore

**File:** [backend/Clients/Usenet/Concurrency/PrioritizedSemaphore.cs](../../backend/Clients/Usenet/Concurrency/PrioritizedSemaphore.cs) (171 LOC, new)

## Summary
Two-tier (High / Low) semaphore with a fairness algorithm based on
**accumulated odds**. Used to protect connection-pool acquisition from
starvation by background work.

## Value
Without prioritization, a heavy queue item (Step 3 + Step 5 doing parallel
analysis across dozens of files) can saturate the global connection
budget and block a user's playback request from even acquiring its first
connection. The prioritized semaphore guarantees that user-facing
streaming reservations always get serviced ahead of analysis traffic — but
without total starvation of the analysis (low-priority) queue.

## Algorithm

```
on Release():
  if both queues have waiters:
     accumulator += lowOdds  (default 20)
     if accumulator >= 100:
        wake low waiter
        accumulator -= 100
     else:
        wake high waiter
  elif only high has waiters:
     wake high
  elif only low has waiters:
     wake low
```

So with the default `usenet.streaming-priority = 80`:
- ~80% of releases wake high-priority callers.
- ~20% wake low-priority.
- Low priority **cannot starve** even under sustained high-priority load.

## Configuration
- `usenet.streaming-priority` (default 80, percentage going to High).
- `usenet.streaming-reserve` (default 5 slots reserved for High, completely
  unavailable to Low).

## Possible Issues / Edge Cases

| # | Issue | Severity |
|---|---|---|
| 1 | Lock-based (not `ConcurrentQueue`) — under sustained 1000+ ops/s, lock contention measurable. Nzbdav workloads do not approach this. | Low |
| 2 | No timeout API — caller must implement via `CancellationToken`. Misuse risk. | Low |
| 3 | No fairness *within* a tier — first-come-first-served via `LinkedList` ordering, which is what you want, but worth a unit test. | Cosmetic |
| 4 | `usenet.streaming-reserve` default of 5 may be too small for users with 50+ provider connections. Should scale with connection budget. | Medium |

## Code Quality
- Cancellation handled cleanly: the waiting node is removed from the
  linked list when its token fires.
- TaskCompletionSource pattern is correct (waiters block on `await`, no
  spinning).
- Internal accumulator is documented in code comments, which makes the
  fairness math auditable.

## Recommended Improvements
1. Auto-scale `streaming-reserve` to `max(5, totalConnections / 10)` if
   not explicitly set.
2. Expose Prometheus counters for "high releases" / "low releases" so the
   fairness ratio can be observed over time.
3. Consider a third "Critical" tier reserved for shutdown / health-check
   probes — currently those compete with normal streaming.
