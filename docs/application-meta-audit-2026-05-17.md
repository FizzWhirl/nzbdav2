# Application Meta-Audit - 2026-05-17

Scope: second-pass deep audit of the full NzbDav application after the existing full-application review. This report reviews the earlier reviews, adds architecture-focused review, identifies the application's core code paths, runs repeated independent review over each path, and then manually checks contested claims against source.

Method:

- 10 sub-agents reviewed the existing review reports.
- 10 sub-agents reviewed the overall architecture.
- 10 core application paths were identified.
- 10 sub-agents reviewed each core path.
- Total sub-agent passes: 120.
- Final findings below are manually triaged. Repeated but stale findings are explicitly rejected.

Related reports:

- `docs/codebase-deep-review-2026-05-17.md`
- `docs/full-application-deep-review-2026-05-17.md`

## Core Code Paths Reviewed

1. Startup, configuration, authentication bootstrap, migration, and process lifecycle.
2. Queue ingestion, SAB-compatible API, queue state, history state, and history cleanup.
3. NZB parsing, deobfuscation, Par2 metadata extraction, archive aggregation, media probing, and post-processing.
4. WebDAV virtual filesystem, path resolution, PROPFIND, range GET/HEAD, symlink exposure, and rclone invalidation.
5. Streaming, range reads, shared stream fanout, Usenet provider fallback, buffering, and connection pools.
6. Health checks, repair/deletion workflow, deleted-file stats, media-analysis deletion, and replacement-search triggers.
7. Sonarr/Radarr integration, post-processing, import monitoring, failed-download handoff, and partial-file replacement search.
8. Frontend server, Express proxy, session auth, frontend-to-backend API key handoff, and WebSocket relay.
9. Frontend routes, loaders/actions, browser WebSocket subscriptions, UI state, and data contracts.
10. Persistence, migrations, SQLite/WAL settings, maintenance, cleanup, schema compatibility, and VFS invalidation.

## Executive Conclusion

The current architecture is workable and has improved materially after the lifecycle cleanup fixes. The most important remaining problems are not in the domain idea itself; they are at subsystem boundaries:

- startup/migration should fail early and clearly;
- frontend/backend runtime configuration should be validated once;
- WebSocket and JSON contracts should be guarded;
- VFS invalidation should be observed and retried;
- Arr notifications need better retry/diagnostic handling;
- raw SQL and bulk database operations need a more explicit invalidation contract;
- tests should target queue/history/Arr, startup, and streaming boundaries first.

The highest-confidence immediate fix remains `entrypoint.sh`: a failed migration currently does not stop service startup.

## Highest Priority Confirmed Findings

### P0. Entrypoint starts services even when database migration fails

Status: confirmed  
Files: `entrypoint.sh`

The entrypoint runs:

```sh
su-exec appuser ./NzbWebDAV --db-migration
log_json info "Database migration completed."
```

There is no `set -e` and no explicit exit-code check. A failed migration can be followed by backend/frontend startup against a partial or stale schema.

Fix: wrap the migration command and exit on failure before starting the backend.

### P0. Backend WebSocket relay parses backend frames without protection

Status: confirmed  
Files: `frontend/server/websocket.server.ts`

Browser subscription parsing is guarded, but the frontend server's backend WebSocket relay does:

```ts
var topicMessage = JSON.parse(rawMessage);
```

outside a try/catch and without validating `Topic` or `Message`. A malformed backend frame can break the relay and may crash the frontend process depending on runtime error handling.

Fix: add try/catch, validate shape, log the bad frame metadata, and keep the relay alive.

### P0. Frontend runtime configuration is not validated at startup

Status: confirmed  
Files: `frontend/server/app.ts`, `frontend/server/websocket.server.ts`, `frontend/app/clients/backend-client.server.ts`

`BACKEND_URL` and `FRONTEND_BACKEND_API_KEY` are read directly in several places. Some paths use non-null assertions and others fall back to an empty string. Misconfiguration creates lazy failures: failed proxy calls, closed WebSockets, empty UI data, or unauthorized backend calls.

Fix: create one startup config loader/validator for the frontend server. Fail fast if required env vars are missing or malformed.

### P1. Frontend proxy has no explicit timeout or proxy error handling

Status: confirmed  
Files: `frontend/server/app.ts`

The backend proxy is created with only `target` and `changeOrigin: false`. That preserves rclone compatibility and should stay, but there is no explicit `timeout`, `proxyTimeout`, or structured proxy error response. Slow or hung backend calls can fail in opaque ways.

Fix: add explicit timeouts and proxy error logging while preserving `changeOrigin: false`.

### P1. VFS forget callback is fire-and-forget with no error observation

Status: confirmed  
Files: `backend/Database/DavDatabaseContext.cs`

`SaveChangesAsync()` calls `VfsForgetCallback` after commit with the discard pattern. `TriggerVfsForget()` does the same. If rclone is unavailable, rejects the path, or times out, the database has changed but rclone's VFS cache can remain stale with no retry or warning.

Fix: wrap callback execution in a small observed background path that logs failures and optionally retries. Normalize forget paths in one place.

### P1. Migration lock clearing is unconditional

Status: confirmed  
Files: `backend/Program.cs`

Migration mode deletes every row from `__EFMigrationsLock` before running EF migrations. This recovers from stale locks, but it also clears legitimate in-progress locks if two instances accidentally share the same config volume.

Fix: replace unconditional deletion with a safer startup lock strategy or documented recovery mode. At minimum, make this a loud warning with an opt-in override.

### P1. Migration/schema compatibility logic is too large and too powerful for `Program.cs`

Status: confirmed  
Files: `backend/Program.cs`

`Program.cs` owns command dispatch, migration mode, schema compatibility fixes, old blobstore migration, runtime PRAGMAs, service registration, middleware setup, static hook setup, and startup reporting. Some schema fixes recreate tables with foreign keys disabled. These flows may be necessary for legacy upgrades, but they are difficult to test and reason about inside the entry point.

Fix: extract migration compatibility into dedicated services/steps with row-count checks, integrity checks, and clear recovery logging.

### P1. Arr notification paths are not durable or retried

Status: confirmed  
Files: `backend/Services/ArrReplacementSearchService.cs`, `backend/Clients/RadarrSonarr/ArrClient.cs`, `backend/Services/ArrMonitoringService.cs`

The current direction is correct: whole-download failure asks Arr to refresh monitored downloads, and partial-file deletion performs targeted mark-failed/search. The weakness is reliability: Arr HTTP calls have no explicit retry/circuit policy, no per-instance timeout beyond `HttpClient` default behavior, and failure state is logged but not queued for retry.

Fix: add a small durable notification queue or retry worker for Arr replacement events, with clear success/failure status.

### P1. Health repair is effectively delete-and-research, not Par2 reconstruction

Status: confirmed  
Files: `backend/Services/HealthCheckService.cs`, `backend/Par2Recovery/Par2.cs`, `backend/Queue/DeobfuscationSteps/2.GetPar2FileDescriptors/GetPar2FileDescriptorsStep.cs`

Par2 code is used as a metadata oracle during queue processing and inspection. The health repair path does not reconstruct missing data from Par2; it marks/deletes/unlinks and triggers Arr replacement behavior. That may be the right operational model, but docs and UI language should not imply actual Par2 repair unless implemented.

Fix: either implement actual Par2 reconstruction or rename/document the feature as replacement repair.

### P1. Queue/history/SAB request parsing returns 500 for invalid GUID query values

Status: confirmed  
Files: `backend/Api/SabControllers/RemoveFromQueue/RemoveFromQueueRequest.cs`, `backend/Api/SabControllers/RemoveFromHistory/RemoveFromHistoryRequest.cs`, `backend/Api/SabControllers/SabApiController.cs`

`Guid.Parse()` is used on query values. Invalid GUIDs throw `FormatException`, which the SAB controller catches as a generic exception and returns a 500. Bad client input should be a 400.

Fix: use `Guid.TryParse()` and throw `BadHttpRequestException` for invalid IDs.

### P1. NZB upload has body-size protection but not content-shape limits

Status: confirmed  
Files: `backend/Program.cs`, `backend/Api/SabControllers/AddFile/AddFileRequest.cs`, queue deobfuscation/processing steps

Kestrel limits request body size with `MAX_REQUEST_BODY_SIZE` defaulting to 100 MB. That is good. The remaining gap is inside the accepted NZB: no clear cap on file count, segment count, archive entry count, or filename/path depth before expensive deobfuscation and archive probing starts.

Fix: add configurable validation for maximum NZB files, maximum segments per file, maximum total segments, maximum archive entries, and maximum parsed path length.

### P1. Frontend data contracts are compile-time only

Status: confirmed  
Files: `frontend/app/clients/backend-client.server.ts`, `frontend/app/utils/websocket-util.ts`, `frontend/app/routes/**`

`backend-client.server.ts` returns typed objects but mostly trusts `response.json()`. Route code and WebSocket handlers parse strings/JSON with minimal shape checks. When backend contracts drift, the UI can silently ignore data, render stale state, or crash route loaders.

Fix: introduce lightweight runtime validators for the highest-risk response types and WebSocket topic payloads first: queue, history, health, stats, and settings.

### P1. Frontend route errors are inconsistent and sometimes mask the real response

Status: confirmed  
Files: `frontend/app/clients/backend-client.server.ts`, `frontend/app/routes/settings.update/route.tsx`, frontend routes

Backend client error handling often calls `(await response.json()).error`; if the backend returns non-JSON, the parse error masks the original status. `settings.update` parses JSON from form data without a try/catch and no schema. Error boundaries are inconsistent across routes.

Fix: centralize response error extraction using `text()` fallback, add route-level error boundaries for primary pages, and validate settings update input.

### P1. Background task ownership remains mixed

Status: confirmed  
Files: `backend/Queue/QueueManager.cs`, `backend/Services/HealthCheckService.cs`, `backend/Services/ProviderErrorService.cs`, `backend/Utils/OrganizedLinksUtil.cs`, `backend/Services/BackgroundTaskQueue.cs`

The app mixes constructor-started tasks, static background refreshes, timers, `BackgroundService`, and a shared `BackgroundTaskQueue`. Some loops are well supervised; others depend on process shutdown or local cancellation semantics.

Fix: move long-running loops toward `IHostedService`/`BackgroundService` ownership over time. Keep the existing `BackgroundTaskQueue` but add overflow/error metrics.

### P2. Metrics auth default is risky when backend port 8080 is exposed

Status: confirmed with deployment caveat  
Files: `backend/Program.cs`, `frontend/server/app.ts`

The frontend protects `/metrics` behind frontend authentication. Direct backend `/metrics` is protected only when `METRICS_REQUIRE_API_KEY=true`. In the standard combined container, only port 3000 is normally exposed, so this is not automatically public. In split-service or direct-8080 deployments, metrics are open by default.

Fix: either default backend metrics to protected or document the exposure prominently for direct backend deployments.

## Path-Specific Notes

### Startup and deployment

Confirmed:

- Migration failure handling is the most concrete P0 bug.
- `BACKEND_URL` and `FRONTEND_BACKEND_API_KEY` should be validated once in frontend startup.
- `CONFIG_PATH` writability should be checked before migration.
- Unconditional migration-lock clearing needs a safer mode.

Rejected/downgraded:

- Regenerating `FRONTEND_BACKEND_API_KEY` on each all-in-one container start does not break the default deployment because backend and frontend share the same environment. It is a split-service/documentation risk, not a default-container bug.

### Queue, SAB, and history

Confirmed:

- The queue-to-history transition is intentionally atomic in `MarkQueueItemCompleted`.
- Whole-download failures now follow Arr authority by refreshing monitored downloads rather than directly deleting Arr queue records.
- `RemoveHistoryItemsAsync` and `ArchiveHistoryItemsAsync` are caller-save helpers. Current reviewed callers save after calling them, but the pattern is fragile.
- Invalid GUID query values should return 400, not 500.

Rejected/downgraded:

- Old hidden-history cleanup is fixed in current source. It queues `HistoryCleanupItems`, includes `DownloadDirId`, saves, bulk-deletes, and commits in a transaction.
- Stale whole-failure direct Arr deletion methods and `RemoveFailedHistoryItemAfterDelay` are no longer present.

### NZB and archive processing

Confirmed:

- Kestrel caps upload body size, but accepted NZBs can still be structurally huge.
- Archive/NZB path and filename validation should be made explicit before metadata flows into VFS names.
- RAR/7z/header timeout handling has improved, but needs regression tests around retryability, password-protected archives, corrupt headers, and season-pack mapping.
- Par2 is metadata-oriented today, not actual health repair reconstruction.

Rejected/downgraded:

- Several parser-security claims depend on behavior inside the external `Usenet` library or forked SharpCompress and need focused reproduction before being treated as vulnerabilities.

### WebDAV and VFS

Confirmed:

- Preserving Host with `changeOrigin: false` is critical for rclone v1.74+ PROPFIND behavior and should not be changed casually.
- VFS invalidation failures are not observed.
- Bulk operations require manual `TriggerVfsForget`; this hidden contract remains a recurring risk.
- Directory listings are not paginated and may become expensive for very large virtual folders.

Rejected/downgraded:

- The claimed `end - start + 1 ?? long.MaxValue` null bug in `GetAndHeadHandlerPatch` is false. In C#, nullable arithmetic returns `long?`, so open-ended ranges coalesce to `long.MaxValue`.
- Health-check API endpoints inherit `BaseApiController` auth; the claim that `get-health-check-history` is unauthenticated is false.

### Streaming and Usenet

Confirmed:

- Shared stream design is valuable but complex: ring buffers, slow-reader backpressure, task-completion signaling, grace timers, and fallback streams all interact.
- `_pumpGate.Wait()` blocks a thread-pool thread during grace/no-reader periods; not a guaranteed deadlock, but not ideal under many paused streams.
- Observability is good in places but still missing histograms for provider latency, fetch wait time, stream buffer saturation, range request distribution, and combined-stream cache hit/miss.
- Streaming classes have very little isolated test coverage relative to their complexity.

Rejected/downgraded:

- Some reported shared-stream corruption races were mitigated by source checks, including `SharedStreamHandle`'s post-copy valid-range recheck.
- Some circuit-breaker/reset claims require closer inspection of the connection pool before being treated as bugs.

### Health, repair, deletion, and replacement

Confirmed:

- Health deletion/replacement flows now record deleted health results in queue Step 5.
- `HealthCheckResults` intentionally retain `DavItemId` as data without a foreign key. That supports deleted-file stats after the item row is gone; it should be documented as intentional.
- Repair deletion and Arr notification are not durably coupled. If Arr is down, deletion can proceed while replacement search is only logged as failed.
- Health repair state and stats should distinguish actual repair, deletion, deferred repair, and replacement-triggered deletion more explicitly.

Rejected/downgraded:

- Cascading `HealthCheckResults` on `DavItem` delete is not obviously desirable because deleted-file stats need to survive item deletion.

### Arr integration

Confirmed:

- The high-level model is now correct: Arr remains authoritative for whole-download failure; NzbDav forces targeted search only when individual files are removed after queue validation or later health deletion.
- Sonarr season-pack handling has a reasonable fallback chain, but episode resolution needs more tests and better logs when filename parsing fails.
- Arr HTTP calls need explicit timeout/retry/circuit behavior and response-body logging for failed mark/search/delete operations.
- Static caches are now `ConcurrentDictionary<(Host, Path), int>`; host scoping is fixed.

Rejected/downgraded:

- The old review claim that Arr caches are plain static dictionaries is stale.
- Direct removal of query-string API key support is not safe without SAB compatibility migration planning.

### Frontend server and proxy

Confirmed:

- Backend relay JSON parse must be guarded.
- Startup env validation is needed.
- Proxy timeout/error handling is missing.
- `changeOrigin: false` is correct and should be preserved.
- Reverse-proxy assumptions (`SECURE_COOKIES`, direct backend exposure, split-service API key stability) need clearer docs.

Rejected/downgraded:

- Plaintext frontend-to-backend WebSocket key is not visible to browser clients; it is server-to-server. It still needs TLS/local-network assumptions documented for split deployments.

### Frontend routes and UI contracts

Confirmed:

- Runtime response validation is absent.
- WebSocket code is duplicated across routes.
- High-frequency updates often map whole arrays for single-item state changes.
- Route error handling and user-facing diagnostics are inconsistent.
- Settings update input needs parse/schema protection.

Rejected/downgraded:

- XSS risk appears low because React escaping is used and no direct application `dangerouslySetInnerHTML` pattern stood out in the reviewed paths.
- CSRF risk is partially mitigated by `SameSite=Strict`; explicit CSRF tokens and security headers are still worthwhile hardening, not the first operational bug.

### Persistence and database lifecycle

Confirmed:

- VFS invalidation is a hidden contract split between EF change tracking and manual bulk-operation calls.
- Schema compatibility logic should be extracted and made testable.
- `synchronous=NORMAL` is a deliberate WAL durability/performance tradeoff; it needs documentation/configurability, not an automatic revert.
- CI versioning uses a hardcoded patch offset.
- CI lacks backend tests, frontend typecheck, and smoke tests before image publish.

Rejected/downgraded:

- `synchronous=NORMAL` is not automatic corruption. It is a known SQLite WAL tradeoff.
- Provider-error individual event non-persistence is an intentional storage tradeoff; if summaries fail, observability is degraded, but it is not automatically a data-integrity bug.

## False Positives Rejected After Source Review

1. Hidden-history cleanup is still broken. Rejected: fixed in current source.
2. Arr caches are unsynchronized and not host-scoped. Rejected: current source uses `ConcurrentDictionary<(Host, Path), int>`.
3. Stale whole-download direct Arr deletion methods remain. Rejected: no matching methods found.
4. `RemoveFailedHistoryItemAfterDelay` remains. Rejected: no matching method found.
5. `GetAndHeadHandlerPatch` open-ended range arithmetic throws on null end. Rejected: nullable arithmetic coalesces correctly.
6. `get-health-check-history` is unauthenticated. Rejected: it inherits `BaseApiController` auth.
7. Generated internal API key breaks default all-in-one restarts. Rejected for default deployment; true only as a split-service/documentation concern.
8. Query-string API keys can simply be removed. Rejected: SAB-compatible clients commonly use `apikey`; deprecate or redact carefully instead.
9. `HealthCheckResults` should necessarily cascade on `DavItem` delete. Rejected/downgraded: deleted-file stats require durable result rows after item deletion.
10. Frontend build failures are silently copied into Docker runtime. Rejected: Docker build steps fail if build commands fail.

## Recommended Fix Sequence

### Phase 1: Small, high-confidence fixes

1. Add migration exit-code handling in `entrypoint.sh`.
2. Add frontend startup env validation for `BACKEND_URL` and `FRONTEND_BACKEND_API_KEY`.
3. Wrap backend WebSocket relay `JSON.parse` in try/catch and validate topic/message shape.
4. Add frontend proxy timeout and proxy error logging.
5. Change invalid SAB GUID query values from 500 to 400.
6. Add `CONFIG_PATH` writability preflight before migration.

### Phase 2: Lifecycle and reliability

1. Observe/log/retry VFS forget callback failures.
2. Make migration lock clearing safe or explicitly opt-in.
3. Extract schema compatibility/migration recovery out of `Program.cs`.
4. Add Arr notification retry/durable queue for replacement-search events.
5. Add explicit Arr HTTP timeouts and response-body logging on failure.
6. Clarify health repair naming: Par2 metadata vs replacement repair.

### Phase 3: Contracts and validation

1. Add runtime validation for key frontend backend-client responses.
2. Define a typed WebSocket topic/payload contract shared by routes.
3. Add NZB content-shape limits: files, segments, archive entries, path length.
4. Add settings update schema validation and safer JSON parse handling.
5. Add security headers and document CSRF/SameSite tradeoffs.

### Phase 4: Testing and CI

1. Add backend tests for queue-to-history-to-Arr lifecycle.
2. Add tests for hidden-history cleanup and `HistoryCleanupService` VFS invalidation.
3. Add tests for failed queue removal not appearing in SAB queue.
4. Add tests for partial season-pack deletion resolving only affected Sonarr episodes.
5. Add tests for WebSocket malformed frame survival.
6. Add entrypoint migration-failure shell test.
7. Add frontend typecheck and minimal route/action validation tests to CI.
8. Add Docker publish smoke checks and document hardcoded patch offset.

## Final Assessment

The app's domain architecture is credible: virtual WebDAV files, streaming from Usenet, SAB compatibility, Arr handoff, and health/deletion stats are all coherent. The risk is not that the architecture is unsuitable; it is that too many critical transitions are implicit:

- shell exit code to service startup;
- DB commit to VFS forget;
- queue failure to Arr awareness;
- backend message to frontend UI state;
- raw SQL delete to cache invalidation;
- config env to runtime behavior.

The next reliability leap should focus on making those transitions explicit, observable, and testable. The first fix should be the migration failure exit handling because it is clear, cheap, and protects every other subsystem from starting in a bad state.
