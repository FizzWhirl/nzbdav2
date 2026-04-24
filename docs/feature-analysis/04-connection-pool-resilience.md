# Feature Report — Connection Pool Resilience

**Files:**
- [backend/Clients/Usenet/Connections/ConnectionPool.cs](../../backend/Clients/Usenet/Connections/ConnectionPool.cs) (~600 LOC, ~70% rewritten)
- [backend/Clients/Usenet/Connections/GlobalOperationLimiter.cs](../../backend/Clients/Usenet/Connections/GlobalOperationLimiter.cs) (rewritten)
- [backend/Clients/Usenet/Connections/StreamingConnectionLimiter.cs](../../backend/Clients/Usenet/Connections/StreamingConnectionLimiter.cs) (reserve mechanism)

## Summary
A defensive layer between callers and raw NNTP connections that adds:
- Idle eviction
- Stuck-connection detection (30 min)
- Health pings on idle (>60 s) connections
- Circuit breaker on consecutive create failures
- Exponential backoff on socket exhaustion
- Reserve slots so new requests cannot deadlock waiting on long streams
- Cancellation-aware logging (Debug for caller-initiated cancels, Warning
  for real failures, Error after 3+ in a row to trip the circuit breaker)

## Value
Vanilla upstream relied on the underlying NNTP library's own pool. In the
field this caused:
- Long-tail latency from connections wedged in `ESTABLISHED` after a
  provider socket-half-closed.
- Connection storms when a provider had a transient outage (no backoff).
- New playback requests blocked indefinitely while a long-running queue
  analysis held all available slots.
- Noisy logs from every client disconnect surfacing as a Warning.

The fork's `ConnectionPool<T>` addresses each of these.

## Key Mechanisms

### Circuit Breaker
```
on factory throw:
  consecutiveFailures++
  if consecutiveFailures > 5:
     pause new attempts for 2 s
on factory success:
  consecutiveFailures = 0
```

### Socket Exhaustion Backoff (EAGAIN / AddressInUse)
- Exponential: 100 → 200 → 400 → 800 → 1600 ms.
- Up to `maxRetries` attempts before bubbling up.

### Idle / Stuck Sweeper
- Background task per pool.
- Idle ≥ 30 s → returned to factory or destroyed.
- Idle ≥ 60 s → 3 s health ping (`DateAsync()`); destroy on failure.
- Active ≥ 30 min → marked doomed, force-disposed.

### Reserve (commit `88f4e1f`, refined `e6345ec`, `e0990fe`)
- A configurable number of slots per pool are held back in a "streaming
  reserve".
- Background work (analysis, queue probe) cannot consume them.
- When a new streaming request arrives while every "general" slot is
  busy, the reserve gives it a guaranteed entry without preempting
  in-flight work.

### Cancellation-Aware Logging (this session)
```
isCancellation = ex is OperationCanceledException
                 || cancellationToken.IsCancellationRequested
                 || linked.IsCancellationRequested
if isCancellation:        Log.Debug
elif failures > 3:        Log.Error  (circuit breaker imminent)
else:                     Log.Warning
```

## Possible Issues / Edge Cases

| # | Issue | Severity |
|---|---|---|
| 1 | Circuit-breaker cooldown is fixed at 2 s. During a sustained outage you get a steady-state "wait 2 s, retry, fail, wait 2 s" loop. Adaptive cooldown (back off to 30 s) would be cleaner. | Medium |
| 2 | Stuck-connection threshold is 30 min — a legitimately long stream can hit this. Currently the threshold is hardcoded; should be at least 2 × longest expected file duration. | Medium |
| 3 | CS8714 `T : INntpClient` warning — generic constraint allows nullable `T`. Practically safe but noisy. | Cosmetic |
| 4 | Reserve fraction is global per pool; no per-provider tuning. A misbehaving provider's reserve cannot be shrunk independently. | Low |
| 5 | When the pool's factory itself is slow (TLS to a distant host), the cancellation discrimination relies on the factory's own behaviour around `OperationCanceledException`. False categorisation possible if a custom factory wraps it. | Low |

## Code Quality
- Lock granularity is good — no global lock; per-pool plus `Interlocked`
  for counters.
- Sweeper task lifecycle is correctly tied to pool disposal via
  `_sweepCts`.
- The retry/backoff is well isolated from the circuit-breaker logic; both
  can be tuned independently.

## Recommended Improvements
1. **Adaptive circuit-breaker cooldown** — start at 2 s, double up to 30 s
   under sustained failures, reset on first success.
2. **Per-provider reserve override** — `usenet.providers[N].reserve` so a
   slow provider doesn't tax the budget.
3. **Expose `pool_active`, `pool_idle`, `pool_failures_total`,
   `circuit_breaker_open_seconds_total` as metrics**. Currently only
   visible via log scraping.
4. **Distinguish "stuck doomed" from "timed out" in logs** so Grafana
   alerts can target the right one.
