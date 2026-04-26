# Codebase Streaming Reliability, Speed, and Health Review (2026-04-26)

## Scope

This review examined the current NzbDav workspace for issues that can affect the intended purpose of streaming NZB-backed media files through WebDAV, including backend streaming, queue processing, health/repair, database behavior, frontend proxy/WebSocket behavior, Docker/runtime operations, and relevant in-repository documentation.

No code was changed as part of the review. This document records the findings and a prioritized remediation plan.

## Verification performed

- Frontend typecheck was run via `npm run typecheck` and completed successfully.
- VS Code diagnostics currently report a TypeScript deprecation warning for `baseUrl` in `frontend/tsconfig.vite.json`.
- Backend `dotnet build` could not be run in the local terminal because `dotnet` was not available in `PATH`.
- A full Docker build was not run because this was a read-only review before this report file was created.

## Executive summary

The architecture is capable and already contains several good streaming-specific optimizations: shared streams for sequential playback, range-bounded prefetch for plain NZB files, provider affinity, DMCA/partial-takedown detection, ffprobe-based integrity checks, and SQLite/VFS cache handling. The main risks are not broad design failures; they are concentrated in concurrency/resource-accounting edge cases, archive/multipart range behavior, queue/health consistency gaps, and operational observability.

The highest-priority fixes are:

1. Fix connection-pool gate release when active connections are force-released/reset.
2. Fix `StreamingConnectionLimiter` tracking and resize behavior.
3. Stop releasing `GlobalOperationLimiter` stream permits before the returned stream is consumed.
4. Make provider-selection state immutable/per-operation instead of sharing mutable `ConnectionUsageDetails` between workers.
5. Move shared-stream context ownership from request `NzbFileStream` instances to `SharedStreamEntry`/pump lifetime.
6. Propagate range-bounded prefetch and analysis-mode throttles through RAR/multipart streams.
7. Make queue Step 4/5/6 filesystem/history changes more transactional and invalidate VFS caches after bulk deletes.

## Severity-ranked findings

### Critical 1 — Connection reset can permanently reduce provider pool capacity

**Evidence:** `ConnectionPool.Return()` removes active connections and checks `_doomedConnections`, but on the doomed/disposed path it disposes the returned connection and decrements `_live` without releasing `_gate`. `Destroy()` does release `_gate`, so the behavior is inconsistent. `ForceReleaseConnections()` marks active connections as doomed, which means the next normal return can leak a semaphore slot.

**Impact:** A provider reset or forced release while streams are active can silently shrink available pool capacity. Over time this can look like provider slowness, stuck streams, or unexplained connection starvation.

**Recommended fix:** Split disposed-pool handling from doomed-connection handling. If the pool is not disposed, always release `_gate` for an active lock that is returned/destroyed. Add a regression test that acquires `N` locks, calls `ForceReleaseConnections()`, returns the locks, and verifies `N` new locks can be acquired.

### Critical 2 — Streaming permit tracking is internally inconsistent

**Evidence:** `AcquireAsync()` delegates to `AcquireWithTrackingAsync()` using context `legacy`, but `Release()` without a permit ID cannot remove that tracked permit. The sweeper later sees those already-released permits as stuck and calls `_semaphore.Release()` again. `ResizeSemaphore()` also replaces and disposes the semaphore while holders/waiters may still exist, and releases after resize go to the new semaphore rather than the semaphore that was originally acquired.

**Impact:** Streaming connection accounting can drift in both directions: false stuck-permit releases can over-admit workers, while resize/dispose can throw `ObjectDisposedException` or lose permits. This directly affects smooth playback under concurrent streams, seeks, and settings changes.

**Recommended fix:** Remove the legacy bool API or make it return a disposable/lease object. A lease should hold the semaphore instance it acquired and remove its own tracking ID on disposal. Avoid replacing a live semaphore; instead implement an adjustable limiter with explicit max/in-use counters, or defer resizing until no holders remain.

### Critical 3 — Global operation permits for streams are released before stream consumption

**Evidence:** `MultiConnectionNntpClient.RunStreamWithConnection()` wraps the returned segment stream with a cleanup callback that disposes `globalPermit`, but the method-level `finally` also calls `globalPermit?.Dispose()` unconditionally. Because C# runs `finally` before the caller consumes the returned stream, the global permit is released immediately after stream creation. The permit class is idempotent, so this is not a double-release bug, but it defeats the intended lifetime of the global limiter for streaming operations.

**Impact:** `GlobalOperationLimiter` does not actually cap active segment streams for the full read duration. Queue/health/streaming fairness can be much weaker than intended during slow providers or large range reads.

**Recommended fix:** Transfer ownership of the permit to the returned stream wrapper on success. In the method `finally`, dispose the permit only when `success == false`. Add an integration test where a stream is returned but not disposed and verify limiter usage remains active until disposal.

### High 4 — Connection cleanup likely causes unnecessary pool churn

**Evidence:** `DisposableCallbackStream.DisposeAsync()` disposes the inner stream before invoking `onDisposeAsync`. `RunStreamWithConnection()` then calls `connectionLock.Connection.WaitForReady()` in that callback. If disposing the article stream closes the response/client state needed to drain/readiness-check the NNTP connection, cleanup will replace otherwise reusable connections.

**Impact:** Successful segment reads can still lead to connection replacement, increasing TLS/login churn, latency, and provider throttling risk.

**Recommended fix:** Invert cleanup order for this use case: drain/readiness-check first, then dispose the article stream, or add a specialized NNTP segment lease that owns the stream, connection lock, and permit with explicit cleanup ordering.

### High 5 — Provider attribution and exclusion race between workers

**Evidence:** `ConnectionUsageDetails` contains mutable fields such as `CurrentProviderIndex`, `IsBackup`, `IsSecondary`, and `ExcludedProviderIndices`. `ConnectionUsageContext.WithProviderAdjustments()` creates a new context but reuses the same details object. `MultiProviderNntpClient` mutates `CurrentProviderIndex` when selecting a provider. `BufferedSegmentStream` later reads that mutable shared state to record failed providers and retry exclusions.

**Impact:** Parallel segment workers can overwrite each other’s provider state. Straggler detection, provider cooldowns, affinity, metrics, and missing-article attribution can blame the wrong provider or fail to exclude the provider that actually stalled. This can waste retries and make provider health decisions unreliable.

**Recommended fix:** Treat per-segment operation details as immutable. Clone `ConnectionUsageDetails` in `WithProviderAdjustments()`, or return actual provider index as part of the segment-fetch result. Avoid writing selected provider into a shared context object.

### High 6 — Shared-stream context lifetime is owned by the wrong object

**Evidence:** In the shared-stream factory inside `NzbFileStream`, the entry-scoped cancellation token gets a context via `_contextScope = entryCt.SetScopedContext(bufferedContext)`. `_contextScope` is a field on the request-scoped `NzbFileStream`, so disposing the first request stream can remove context from the entry token while the `SharedStreamEntry` pump continues serving other readers.

**Impact:** Long-lived shared pumps can lose provider/usage context mid-stream. This can break provider attribution, priority, affinity, and cleanup diagnostics for the shared stream after the initial reader disconnects or seeks.

**Recommended fix:** Move entry-token context scope ownership into `SharedStreamEntry` or the pump task itself. Request-scoped streams should not dispose context attached to an entry-scoped token.

### High 7 — Archive/multipart range reads can over-prefetch and over-consume connections

**Evidence:** Plain NZB files read `RequestedRangeEnd` from the WebDAV handler and pass it to `NzbFileStream`, which bounds buffered prefetch. RAR and multipart paths create `DavMultipartFileStream` without passing any requested range end. `DavMultipartFileStream` creates per-part `NzbFileStream` instances without `requestedEndByte` or segment-size metadata.

**Impact:** Ranged clients such as rclone, ffprobe, Plex/Jellyfin, or preview clients can request small closed ranges from RAR/multipart files but still trigger much larger downstream prefetches. This wastes Usenet bandwidth and connection slots exactly during seek-heavy playback.

**Recommended fix:** Add range-budget propagation to `DavMultipartFileStream`: convert the outer requested byte range into per-part byte ranges and pass bounded end bytes to each part stream. Also store per-part segment sizes where available.

### High 8 — Queue Step 4 commits filesystem entries before Step 5/6 can still fail

**Evidence:** `QueueItemProcessor` creates and saves `DavItem` filesystem entries in Step 4, then runs media analysis and deletes DMCA/probe-failed/corrupt items with `ExecuteDeleteAsync()` in Step 5. If Step 5 later throws, the generic fatal catch moves the job to failed history without the `mountFolder` reference used by normal Step 6 completion.

**Impact:** A job can partially mount files, then fail history completion or lose `DownloadDirId` linkage. Cleanup may still delete items by `HistoryItemId`, but failed/partial paths are more fragile, and bulk deletes bypass automatic VFS invalidation.

**Recommended fix:** Use a staged status for mounted-but-not-completed jobs. Either defer exposing the mount until after Step 5, or ensure the fatal path knows `mountFolderId` and enqueues cleanup with `DownloadDirId`. Wrap Step 5 deletion/history state changes in explicit cleanup logic and call VFS forget for affected parent directories after bulk operations.

### High 9 — Bulk EF operations bypass automatic VFS forget in several streaming-critical paths

**Evidence:** `DavDatabaseContext.SaveChangesAsync()` computes VFS forget directories from tracked added/deleted entities. The context explicitly notes `TriggerVfsForget()` is needed for bulk operations. Several paths use `ExecuteDeleteAsync()` or raw SQL, including queue analysis cleanup, health reset/deleted-file cleanup, provider-error cleanup, and history cleanup. History cleanup triggers VFS forget, but queue Step 5 deletes do not visibly do so.

**Impact:** WebDAV directory caches can stay stale after corrupt/DMCA files are removed, causing clients to see files that were deleted or miss newly corrected state until cache expiry/manual refresh.

**Recommended fix:** Centralize bulk `DavItem` delete/update helpers that compute affected parent directories and call `DavDatabaseContext.TriggerVfsForget()` consistently. Avoid raw `ExecuteDeleteAsync()` on `DavItems` outside those helpers.

### High 10 — `BufferedSegmentStream` urgent/racing logic can amplify load during provider slowness

**Evidence:** The urgent channel is unbounded. Straggler monitor writes urgent retry/race jobs with fire-and-forget `WriteAsync()` calls. Active assignment tracking is keyed only by segment index, so duplicate races overwrite each other; any finishing worker removes `activeAssignments` and `racingIndices` for that index.

**Impact:** During seek storms or slow providers, the stream can queue duplicate urgent work faster than workers can drain it. CAS protects the output slot from duplicate data, but bandwidth and connections are still wasted. Under memory pressure, the unbounded urgent channel is an OOM risk.

**Recommended fix:** Bound the urgent channel, coalesce by segment index, and represent active work as a set of attempt IDs rather than one assignment per segment. Consider a per-segment state machine: `queued`, `fetching`, `racing`, `complete`, `failed`.

### Medium 11 — Queue smart segment-size results are not persisted for normal files

**Evidence:** `UsenetStreamingClient.AnalyzeNzbAsync()` returns `long[]` segment sizes. Queue Step 3 calls it during smart article probing but ignores the returned sizes. `GetFileInfosStep` carries `SegmentSizes`, and `DavNzbFile` supports `SetSegmentSizes()`, but `FileProcessor.Result` and `FileAggregator` do not persist those sizes.

**Impact:** The application pays for smart analysis/probing but may still mount files without segment-size caches. First playback/seek can then require slower interpolation/head lookup or background analysis.

**Recommended fix:** Carry segment sizes from smart analysis into processor results and call `DavNzbFile.SetSegmentSizes()` in `FileAggregator`. Do the same for multipart/RAR part metadata where feasible.

### Medium 12 — RAR header processing can oversubscribe streaming resources

**Evidence:** `RarProcessor` can process many parts concurrently and `GetFastNzbFileStream()` uses up to five header connections per part. Those streams use the same global streaming limiter as playback.

**Impact:** Large RAR releases can compete with active playback for permits and provider connections, particularly on low-RAM or low-connection deployments.

**Recommended fix:** Give RAR header reads a separate low-priority queue/limiter, or cap total RAR header workers globally instead of `parts × workers`. Keep playback and urgent WebDAV reads high priority.

### Medium 13 — Provider-error service can miss blocking missing-article patterns

**Evidence:** Normal persistence skips `MissingArticleEvents` to save space and updates summaries only. Blocking detection can only prove all-provider failure for the same segment within the current 10-second batch. The service cancels its persistence loop on dispose without awaiting the task or flushing the in-memory buffer.

**Impact:** Missing articles spread across batches may never be marked blocking, and recent missing-article observations can be lost during shutdown. Health/repair decisions and UI reporting may understate provider/content failure.

**Recommended fix:** Persist a compact per-file/per-segment provider bitset instead of full granular events, or maintain rolling segment/provider evidence in the summary row. On shutdown, cancel, await the persistence loop, and flush remaining buffer.

### Medium 14 — Health-check state/result updates are not atomic on failure paths

**Evidence:** Error and timeout handling uses `ExecuteUpdateAsync()` to update `DavItem`, then adds `HealthCheckResult` and saves in a later operation. These are not wrapped in an explicit transaction.

**Impact:** A crash or DB failure between operations can leave an item marked corrupted without a matching result row, or a result row without the intended item state. The health UI and repair scheduler can drift.

**Recommended fix:** Use explicit EF transactions for item state plus result insert, or avoid `ExecuteUpdateAsync()` where a tracked item update can be saved with the result in one transaction.

### Medium 15 — Health queue skips any item still linked to history

**Evidence:** `GetHealthCheckQueueItemsQuery()` excludes files where `HistoryItemId != null` to avoid checking jobs not yet imported by Arr clients.

**Impact:** This is understandable, but it means any failed history cleanup/unlink path can permanently exclude mounted content from routine health checks.

**Recommended fix:** Keep the skip, but add a stale-history watchdog: if a history-linked item is older than a configured import grace period, surface it in diagnostics and optionally health-check it at low priority.

### Medium 16 — Bandwidth and affinity persistence can lose data on save failure

**Evidence:** `BandwidthService.GetAndResetPendingDbBytes()` resets pending bytes before `SaveChangesAsync()`. `NzbProviderAffinityService.SaveToDb()` sets `_isDirty = false` before the database save has definitely succeeded.

**Impact:** Transient DB failures can drop recent bandwidth samples or provider performance updates, weakening provider-selection feedback and observability.

**Recommended fix:** Snapshot pending counters without clearing, save, then acknowledge/reset only after successful commit. For affinity, clear dirty state only after `SaveChangesWithRetryAsync()` returns successfully.

### Medium 17 — Background fire-and-forget tasks lack supervised shutdown/retry behavior

**Evidence:** `NzbAnalysisService.TriggerAnalysisInBackground()` starts unobserved `Task.Run()` work; ffprobe retry is another untracked `Task.Run()` with a one-hour delay. `ExceptionMiddleware` uses a fire-and-forget health-trigger task, and inside it starts an unawaited `ExecuteDeleteAsync()` on the same context used for later updates.

**Impact:** Work can be lost on restart, exceptions may be missed, and unawaited EF work can race with context disposal or later operations.

**Recommended fix:** Introduce an `IHostedService` background work queue with bounded capacity, structured retries, cancellation, and shutdown drain. Never start EF operations without awaiting them on a live context.

### Medium 18 — Analysis mode is inconsistent for RAR-backed files

**Evidence:** `DatabaseStoreMultipartFile` detects `X-Analysis-Mode` and disables buffered streaming for packed streams, but `DatabaseStoreRarFile` does not inspect that header and always builds a full `DavMultipartFileStream` with total streaming connections.

**Impact:** ffprobe/decode checks of RAR-backed content can use the same high-resource path as playback, competing with users and potentially making analysis timeouts look like provider issues.

**Recommended fix:** Apply the same analysis-mode throttling to RAR streams as multipart/plain streams. Consider a dedicated `ConnectionUsageType.Analysis` all the way through WebDAV analysis requests.

### Medium 19 — Frontend/backend WebSocket reconnect loops have no backoff

**Evidence:** The frontend server reconnects to the backend WebSocket every second with a fixed delay. Several browser routes/components also reconnect every second on close.

**Impact:** During backend outages, restarts, or migrations, each browser tab and the frontend server can hammer reconnects and logs. This does not usually break streaming directly, but it degrades recovery and observability.

**Recommended fix:** Use exponential backoff with jitter and a maximum delay. Reset backoff on successful connection. Add visibility for “backend websocket disconnected” rather than silently looping.

### Medium 20 — UI proxy fallbacks hide backend failures as successful empty data

**Evidence:** `dashboard-proxy` and `provider-stats-proxy` catch backend errors and return HTTP 200 with empty/default data.

**Impact:** Operators can see a healthy-looking but empty dashboard when the backend is down or failing. Monitoring that relies on HTTP status also misses outages.

**Recommended fix:** Return `503` with a small structured error body, and let the UI render an explicit degraded-state card using cached last-known data where available.

### Medium 21 — Metrics endpoint is intentionally unauthenticated through both backend and frontend

**Evidence:** Backend `UseMetricServer("/metrics")` is placed before auth, and the frontend proxy forwards `/metrics` to the backend before route authentication.

**Impact:** This is convenient for Prometheus but risky if port 3000 is exposed beyond a trusted network. Metrics can reveal provider counts, performance, paths, or workload timing.

**Recommended fix:** Document that `/metrics` must be protected by network policy/reverse proxy, or add optional metrics auth/IP allowlisting.

### Low 22 — Session secret defaults invalidate all sessions on restart

**Evidence:** If `SESSION_KEY` is unset, the frontend generates a random cookie secret at startup.

**Impact:** Users are logged out after every restart. This is not a streaming failure, but it hurts operational UX and can look like auth instability.

**Recommended fix:** Generate and persist a default session key in config/data-protection storage, or make `SESSION_KEY` required unless `DISABLE_FRONTEND_AUTH=true`.

### Low 23 — Standalone backend Dockerfile is stale

**Evidence:** The root Dockerfile builds .NET 10, while `backend/NzbWebDAV.csproj` targets `net10.0`; however `backend/Dockerfile` still uses .NET 9 SDK/runtime images.

**Impact:** Developers using the standalone backend Dockerfile can get confusing build failures unrelated to application code.

**Recommended fix:** Update or remove standalone Dockerfiles, and add CI for any Dockerfile intended to be supported.

### Low 24 — Docker frontend builds are less reproducible than they could be

**Evidence:** Root and frontend Dockerfiles use `npm install` rather than `npm ci`.

**Impact:** Builds can drift if package-lock resolution changes. This can complicate diagnosing frontend regressions.

**Recommended fix:** Use `npm ci` in Docker builds when a lockfile is present.

### Low 25 — TypeScript 7 readiness warning

**Evidence:** VS Code diagnostics report `baseUrl` deprecation in `frontend/tsconfig.vite.json`.

**Impact:** No current typecheck failure, but TypeScript 7 may require config changes.

**Recommended fix:** Follow the TypeScript migration guidance, or add `ignoreDeprecations` temporarily while planning path-alias migration.

## Findings checked but not considered current blockers

- `GlobalOperationLimiter.OperationPermit.Dispose()` is idempotent; the issue is early release on stream success, not double release.
- `CombinedStream` does dispose cached streams on eviction/dispose.
- `HealthCheckService` candidate-null semaphore handling is balanced in the inspected loop.
- Media analysis process argument construction has already moved to `ArgumentList`, reducing quoted-path fragility.
- DMCA fast-fail and article caching exist in the current code; older docs that imply otherwise are stale.
- The entrypoint-generated `FRONTEND_BACKEND_API_KEY` is exported before both backend and frontend processes start, so it is not an intra-container startup race.

## Recommended remediation order

### Phase 1 — Resource correctness and limiter safety

1. Fix `ConnectionPool.Return()` gate release for doomed active connections.
2. Replace `StreamingConnectionLimiter` bool acquire/release with disposable tracked leases.
3. Fix `MultiConnectionNntpClient.RunStreamWithConnection()` so global permits live until stream disposal.
4. Add fake-NNTP concurrency tests for reset, cancellation, slow streams, and seek storms.

### Phase 2 — Provider attribution and stream context correctness

1. Clone per-segment `ConnectionUsageDetails` or return provider index directly from provider operations.
2. Move shared-stream token context scope into `SharedStreamEntry`/pump lifetime.
3. Add metrics for actual provider used, duplicate segment races, straggler retries, and zero-filled bytes.

### Phase 3 — Range and archive streaming performance

1. Propagate requested range end through `DavMultipartFileStream`.
2. Persist segment-size metadata for normal and multipart parts when already known.
3. Apply analysis-mode throttles consistently to RAR-backed files.
4. Bound/coalesce urgent racing work in `BufferedSegmentStream`.

### Phase 4 — Queue/health/database consistency

1. Make queue Step 4/5/6 completion/cleanup explicit and recoverable.
2. Centralize bulk `DavItem` mutation helpers with VFS forget.
3. Transactionalize health-check failure state plus result inserts.
4. Add stale history-link diagnostics for health-check skipped items.

### Phase 5 — Operations/frontend polish

1. Add WebSocket reconnect backoff and visible degraded state.
2. Return non-200 for backend proxy failures while showing cached data in UI.
3. Protect or document `/metrics` exposure.
4. Update stale Dockerfiles and switch Docker frontend installs to `npm ci`.
5. Address TypeScript deprecation warning.

## Better-fit architectural alternatives

- **Unified lease model:** Replace separate ad-hoc semaphores with one lease object per resource: global operation permit, provider pool lock, streaming worker permit, and stream slot. `await using` leases make ownership/lifetime explicit and testable.
- **Immutable operation context:** Stop storing mutable provider state on cancellation-token context. Pass immutable context into operations and return operation results with actual provider index/latency/outcome.
- **Bounded background queue:** Replace fire-and-forget `Task.Run()` with a bounded `IHostedService` work queue that drains on shutdown and records failed jobs.
- **Archive-aware range planner:** For RAR/multipart streams, compute a range plan from outer WebDAV bytes to part-local bytes before opening NNTP streams. This preserves seeking speed without overfetch.
- **Compact health evidence store:** Store per-file/per-segment provider bitsets instead of full missing-article events. This keeps disk use low while preserving cross-batch blocking detection.
- **Streaming-focused test harness:** Build a fake NNTP provider suite with controllable delays, missing articles, disconnects, and wrong sizes. This would catch the most important failures faster than full Docker/runtime testing.

## Documentation drift

Some docs and prior analysis files are useful but stale in places. In particular, current code already includes DMCA fast-fail, article caching, `ArgumentList` process invocation for media analysis, and some queue Step 5 batching improvements. Future docs should distinguish implemented, planned, and superseded optimizations to avoid re-fixing old items or misprioritizing work.
