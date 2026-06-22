# Upstream Review and Recommendations (2026-06-21)

## Scope
This review compares our fork against the upstream materials below:
- https://raw.githubusercontent.com/dgherman/nzbdav2/refs/heads/main/README.md
- https://raw.githubusercontent.com/dgherman/nzbdav2/refs/heads/main/docs/upstream-sync-2026-06-09-fizzwhirl.md

The goal is to identify what we should still implement in our version.

## Findings

### Already implemented in our fork (no action needed)
The major upstream/FizzWhirl sync items from 2026-06-09 are already present in our codebase:
- Arr replacement search integration on queue-failure and health-delete paths.
- Prometheus metrics endpoint and optional backend API-key gate.
- rclone v1.74 PROPFIND compatibility cleanup middleware.
- SAB API behavior where limit=0 is treated as unlimited.
- Frontend websocket reconnect exponential backoff.
- Connection-usage fallback labeling as Unlabeled.
- Memory/OOM hardening primitives (pooled buffers, cooldown gate, bounded buffering).
- Bounded archive/ranged prefetch using requested range end.

Representative locations:
- [backend/Services/ArrReplacementSearchService.cs](backend/Services/ArrReplacementSearchService.cs)
- [backend/Services/HealthCheckService.cs](backend/Services/HealthCheckService.cs)
- [backend/Queue/QueueItemProcessor.cs](backend/Queue/QueueItemProcessor.cs)
- [backend/Middlewares/PropFindResponseCleanupMiddleware.cs](backend/Middlewares/PropFindResponseCleanupMiddleware.cs)
- [backend/Api/SabControllers/GetQueue/GetQueueRequest.cs](backend/Api/SabControllers/GetQueue/GetQueueRequest.cs)
- [backend/Api/SabControllers/GetHistory/GetHistoryRequest.cs](backend/Api/SabControllers/GetHistory/GetHistoryRequest.cs)
- [frontend/server/websocket.server.ts](frontend/server/websocket.server.ts)
- [backend/Clients/Usenet/Connections/ConnectionUsageContext.cs](backend/Clients/Usenet/Connections/ConnectionUsageContext.cs)
- [backend/Streams/BufferedSegmentStream.cs](backend/Streams/BufferedSegmentStream.cs)
- [backend/WebDav/DatabaseStoreMultipartFile.cs](backend/WebDav/DatabaseStoreMultipartFile.cs)

### Gaps vs upstream addendum details (recommended)
The following improvements from upstream's 2026-06-10 addendum appear not fully applied in our fork:

1. Remove high-cardinality path label from shared-stream counters.
- Current state: shared-stream Prometheus counters still include a path label.
- Risk: unbounded time-series cardinality growth and higher metrics storage/query cost.
- Evidence: [backend/Metrics/AppMetrics.cs](backend/Metrics/AppMetrics.cs)

2. Wire active-readers gauge so it reports real values.
- Current state: active-readers metric is declared, but no setter/update path was found.
- Risk: metric remains constant and cannot be used operationally.
- Evidence: [backend/Metrics/AppMetrics.cs](backend/Metrics/AppMetrics.cs)

3. Deduplicate circuit-breaker threshold magic number.
- Current state: threshold value is repeated in multiple places (> 5), rather than a single shared constant.
- Risk: drift during future edits, inconsistent trip behavior.
- Evidence: [backend/Clients/Usenet/Connections/ConnectionPool.cs](backend/Clients/Usenet/Connections/ConnectionPool.cs)

## Recommendations

### Priority 1 (implement next)
1. Metrics cardinality hardening.
- Remove the path label from shared-stream hit/miss counters.
- Keep miss reason label (bounded enum-like values).

2. Complete active-readers observability.
- Add a reader-count source from shared-stream entries.
- Refresh the gauge through existing collector flow.

3. Circuit-breaker threshold cleanup.
- Introduce one constant (for example, CircuitBreakerFailureThreshold).
- Use it for both trip checks and log text.

### Priority 2 (targeted validation after changes)
1. Add/adjust tests or diagnostics for:
- Shared-stream metrics label schema and sample emission.
- Active-readers gauge non-zero behavior during multi-reader playback.
- Circuit-breaker trip/reset behavior around threshold boundary.

2. Update docs/changelog entry to record parity with upstream addendum decisions.

## Suggested implementation strategy
1. Apply the three Priority 1 code changes in one small PR focused only on metrics and connection-pool constants.
2. Run a short streaming load test and confirm metric output shape and values.
3. Document the adoption in changelog and the next upstream sync note.

## Bottom line
Our fork already contains the substantive 2026-06-09 upstream feature/fix set. The highest-value remaining work is small and surgical: align metrics-cardinality and gauge-wiring details, and centralize the circuit-breaker threshold constant.