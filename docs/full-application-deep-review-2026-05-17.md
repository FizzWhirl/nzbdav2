# Full Application Deep Review - 2026-05-17

Scope: full read-only review of the application after commit `4834ee74` (`Fix lifecycle cleanup issues`). This pass covered backend core, frontend/proxy/UI integration, operations/security/release, Docker/entrypoint, CI, documentation, database migration behavior, and observability.

Method: three independent sub-agent reviews per focus area, followed by manual verification of repeated or high-severity claims. Several severe sub-agent claims were rejected after source review; this report includes only confirmed or clearly bounded risks.

## Executive Summary

The application is practical and operationally mature in the areas that matter most to its domain: it has a clear dual-service architecture, robust WebDAV streaming concepts, SAB-compatible queue/history integration, Arr handoff logic, health checks, Rclone invalidation, and extensive logging for real-world provider failures. The recent lifecycle cleanup commit improved the codebase by fixing old hidden-history cleanup persistence, removing stale direct Arr queue-deletion paths, and making Arr lookup caches host-scoped and thread-safe.

The biggest remaining risks are cross-cutting lifecycle boundaries: startup/migration failure handling, background task ownership, WebSocket/backend message validation, static hooks, direct DbContext usage, and untyped frontend/backend contracts. These are not all urgent bugs, but they explain why regressions tend to happen at subsystem handoff points.

## Highest Priority Findings

### 1. Entrypoint does not stop when database migration fails

Severity: Critical  
Status: Confirmed

`entrypoint.sh` runs `su-exec appuser ./NzbWebDAV --db-migration` and then logs `Database migration completed.` without checking the command exit status. Because the script does not use `set -e`, a failed migration can still proceed to start the backend and frontend.

Relevant code:

- `entrypoint.sh`, database migration startup block

Impact:

- Backend can start against a partially migrated schema.
- `/health` may pass even though the database is unusable for real requests.
- The UI can come up and then fail later in confusing ways.

Recommendation: check the migration exit code explicitly and exit before starting services if migration fails.

### 2. Migration lock clearing is unconditional

Severity: High  
Status: Confirmed

During `--db-migration`, startup deletes all rows from `__EFMigrationsLock` before running migrations. This is helpful for stale locks from killed migrations, but it cannot distinguish a stale lock from a legitimate in-progress migration in shared-volume or accidental multi-container startup scenarios.

Relevant code:

- `backend/Program.cs`, `DELETE FROM __EFMigrationsLock WHERE 1=1;`

Impact:

- If two app instances ever run migrations against the same SQLite database, one can clear the other's lock.
- A previous crash may still leave partially applied schema changes; clearing the lock does not make the migration idempotent.

Recommendation: avoid clearing EF locks unconditionally. Prefer documented recovery, lock age metadata if available, or a separate exclusive startup lock around the whole migration phase.

### 3. Backend WebSocket proxy crashes on malformed backend frames

Severity: High  
Status: Confirmed

The frontend server's backend WebSocket client parses backend frames with `JSON.parse(rawMessage)` outside a try/catch. Browser-facing WebSocket parsing has a helper that catches malformed frames, but the server-side backend relay does not.

Relevant code:

- `frontend/server/websocket.server.ts`, `initializeWebsocketClient`
- `frontend/app/utils/websocket-util.ts`, client-side safer parsing helper

Impact:

- A malformed or unexpected backend WebSocket message can throw inside the Node `ws` message handler.
- Depending on runtime behavior, this can drop the backend relay or crash the frontend process.
- Browser clients get stale state with little feedback.

Recommendation: wrap backend relay parsing in try/catch, validate `Topic` and `Message`, log structured errors, and keep the relay alive.

### 4. Required frontend runtime environment is not validated at startup

Severity: High  
Status: Confirmed

Frontend code uses `process.env.BACKEND_URL`, `process.env.FRONTEND_BACKEND_API_KEY`, and WebSocket auth assumptions in multiple places. Some paths use non-null assertions, while many backend-client calls fall back to `""` for the API key.

Relevant code:

- `frontend/server/app.ts`, proxy target and API key injection
- `frontend/server/websocket.server.ts`, backend WebSocket auth send
- `frontend/app/clients/backend-client.server.ts`, repeated `process.env.FRONTEND_BACKEND_API_KEY || ""`

Impact:

- Misconfigured deployments fail lazily on first request or WebSocket reconnect rather than at startup.
- Operators get symptoms like empty queue, failed login, or closed WebSocket instead of a clear fatal configuration error.

Recommendation: validate `BACKEND_URL` and `FRONTEND_BACKEND_API_KEY` once during frontend startup and fail fast with a clear message.

### 5. `/metrics` is unauthenticated by default on the backend

Severity: Medium-High  
Status: Confirmed

Backend `/metrics` is registered before WebDAV auth and is only protected when `METRICS_REQUIRE_API_KEY=true`. The frontend proxy protects `/metrics` behind frontend auth, but direct backend access remains open unless the environment variable is set.

Relevant code:

- `backend/Program.cs`, metrics middleware and `UseMetricServer("/metrics")`
- `frontend/server/app.ts`, frontend-auth-gated metrics proxy

Impact:

- In the normal all-in-one Docker image only port 3000 is exposed, so direct backend exposure is usually not public.
- If a user maps backend port 8080, deploys split services, or scrapes backend directly, metrics can reveal provider, queue, stream, and performance information.

Recommendation: default backend metrics to require an API key, or document that exposing backend port 8080 also exposes unauthenticated metrics unless configured.

### 6. Background task ownership is inconsistent

Severity: Medium-High  
Status: Confirmed

The backend uses a mix of `BackgroundService`, `BackgroundTaskQueue`, timers, constructor-started `Task.Run`, and fire-and-forget loops. Some are well supervised; others are only indirectly stopped by cancellation tokens or process shutdown.

Examples:

- `QueueManager` starts `ProcessQueueAsync` from its constructor.
- `HealthCheckService` starts its monitoring loop from its constructor.
- `ProviderErrorService` owns a persistence task and has a shutdown flush.
- `OrganizedLinksUtil` starts a static refresh task and blocks synchronously in `StopRefreshService`.

Impact:

- It is hard to answer, for every service, whether startup completed, whether shutdown awaited it, and where exceptions are observed.
- This matters most for health checks, provider-error persistence, streaming cache refresh, and queue processing.

Recommendation: gradually move long-running loops into `IHostedService`/`BackgroundService` or the existing `BackgroundTaskQueue`, with explicit startup/shutdown semantics.

## Backend Core Review

### Confirmed backend issues

- `SharedStreamEntry` uses a synchronous `_pumpGate.Wait()` inside an async pump task. This is not a guaranteed deadlock by itself because the pump runs on `Task.Run`, but many paused shared streams can occupy thread-pool threads while waiting during grace periods. An async wait primitive or channel-based pause would scale better.
- `CombinedStream` uses synchronous `Read()` via `ReadAsync(...).GetAwaiter().GetResult()`. This is common for `Stream` overrides, but the call can still block callers that use sync reads against network-backed streams. Prefer async consumers where possible.
- `HistoryCleanupService` now gets proper cleanup rows from old hidden-history cleanup after commit `4834ee74`, but its delete plus VFS-forget process is still not transactionally coupled to the async rclone callback. Database deletion can succeed while rclone forget fails or is delayed.
- `ExceptionMiddleware` does useful health-trigger orchestration, but it still mixes HTTP exception translation, database mutation, background scheduling, and health cooldown logic in one middleware.
- Direct `new DavDatabaseContext()` usage remains common in `HealthCheckService`, `BufferedSegmentStream`, tools, and startup. This is mostly disposed correctly, but it bypasses a consistent DI/factory abstraction and makes testing harder.
- Static hooks remain a design debt: `DavDatabaseContext.VfsForgetCallback`, `BufferedSegmentStream.SetMissingArticleLedgerHooks`, `BufferedSegmentStream.SetMediaAnalysisServiceAccessor`, and `OrganizedLinksUtil.StartRefreshService` all create hidden startup-order dependencies.

### Rejected or downgraded backend claims

- `ProviderErrorService` does flush on dispose; it cancels the persistence loop, waits briefly, then calls `PersistEvents()`.
- `HealthCheckService._processingIds` is removed in a `finally` block, so it is not leaked on normal exceptions.
- `NzbFileStream` disposes `_contextScope` in both sync and async disposal paths.
- Stream permits in `MultiConnectionNntpClient` are transferred to the returned callback stream and released when the stream is disposed, not immediately on successful creation.
- `CombinedStream` cache does have expiration and max-size eviction; it is not unbounded.

### Backend patterns worth preserving

- Single-threaded queue processing with explicit retry/failure boundaries.
- `HistoryCleanupService` recursive CTE deletion for mounted folder trees.
- `SharedStreamManager`'s intent of sharing one pump among multiple readers.
- Container-aware graceful degradation in `BufferedSegmentStream`.
- Recent Arr handoff direction: whole-download failures refresh Arr monitored downloads, partial file deletions trigger targeted replacement search.

## Frontend And Integration Review

### Confirmed frontend issues

- Frontend server backend WebSocket parsing lacks a try/catch and schema guard.
- Frontend startup does not validate required env vars; empty API-key fallbacks produce lazy failures.
- Backend response validation is mostly trust-based. `backend-client.server.ts` parses JSON and returns assumed shapes without runtime validation.
- WebSocket payload parsing is mostly stringly typed. Queue status messages use `id|status`/`id|progress`, history removal uses comma-separated IDs, and JSON event payloads are parsed per route.
- Direct browser `fetch()` calls in routes bypass the typed backend client and often do not apply timeouts or unified error handling.
- Reconnect logic is repeated in multiple route/components despite having shared helpers.
- User-facing stale-data behavior is weak when WebSockets fail; most failures are console warnings only.
- Session max age is one year. That may be intentional for appliance-style use, but it is worth documenting as a security tradeoff.

### Rejected or downgraded frontend claims

- HTTP header case mismatch (`X-Api-Key` vs `x-api-key`) is not a functional bug; headers are case-insensitive. Standardizing is still useful for readability.
- The browser queue WebSocket cleanup pattern is not clearly leaking in the reviewed code; cleanup clears the reconnect timer, marks disposed, and closes the socket. It is duplicated and fragile, but not proven leaky.
- `DISABLE_FRONTEND_AUTH=true` does not block WebSockets; `isAuthenticated()` returns true when auth is disabled.
- Sending the internal key over the backend WebSocket is not exposed to browser DevTools; it is server-to-server from the frontend process to the backend process. It should still be protected by TLS or local networking if split across hosts.
- Query-string API keys are a real leakage concern, but SABnzbd compatibility commonly requires `apikey` query support. Treat this as a documentation/logging/redaction concern rather than simply removing query support.

### Frontend patterns worth preserving

- Frontend proxy keeps `changeOrigin: false`, which preserves WebDAV `href` host behavior for rclone.
- Session key persistence is better than ephemeral-only mode: it creates `/config/data-protection/frontend-session.key` when possible.
- `receiveMessage()` already protects browser subscriptions from malformed WebSocket frames.
- Backend client has a central timeout helper for server-side calls.

## Operations, Security, And Release Review

### Confirmed operations issues

- Migration failure exit code is not checked in `entrypoint.sh`.
- Generated `FRONTEND_BACKEND_API_KEY` is not persisted. In the all-in-one container this is fine for backend/frontend because both processes share the same env on each start. It becomes a footgun for split deployments, direct backend clients, or any external automation that expects a stable key.
- `BACKEND_URL` and frontend API key are not validated by frontend startup.
- Backend `/metrics` is unauthenticated by default if port 8080 is exposed.
- Runtime PRAGMAs are hardcoded: `synchronous=NORMAL`, 64 MB cache, and 1.5 GB mmap. These are performance-oriented defaults, but they should be documented as durability/memory tradeoffs and made configurable for low-memory hosts.
- `CONFIG_PATH` writability is not validated with a clear startup check before migrations try to open the DB.
- CI builds and pushes Docker images without an explicit backend test suite, frontend `typecheck`, or focused smoke checks.
- CI patch version calculation depends on a hardcoded run-number offset.
- Production binary includes many dev/test command modes in `Program.cs`.
- Migration/schema compatibility code is large and concentrated in `Program.cs`.
- Documentation does not give a complete deployment checklist for writable `/config`, WAL-aware backup/restore, metrics exposure, split-service API key requirements, or rollback.

### Rejected or downgraded operations claims

- Frontend build failures are not silently copied into runtime: Docker `RUN npm run build` and `RUN npm run build:server` fail the image build if they fail.
- The generated internal API key does not break normal all-in-one container restarts, because backend and frontend are started from the same entrypoint environment.
- `synchronous=NORMAL` in WAL mode is a known performance/durability tradeoff, not automatically database corruption. It should be explicit and configurable, not necessarily reverted blindly.
- Direct API key query support cannot be removed casually because SAB-compatible clients commonly use `apikey` query parameters.

## Testing Gaps

The largest quality gap remains automated testing. The repo has no application test project and no frontend test script; Docker build is the primary validation path.

Highest-value tests:

1. Entrypoint migration failure exits before starting services. This can be shell-tested with a fake backend command.
2. Old hidden-history cleanup persists `HistoryCleanupItem` rows and carries `DownloadDirId`.
3. Failed queue items are visible in SAB history but absent from SAB queue after completion.
4. Whole-download failure refreshes Arr monitored downloads and never calls direct Arr queue deletion.
5. Partial season-pack deletion resolves only affected Sonarr episodes for common naming patterns.
6. Frontend backend WebSocket relay survives malformed backend frames.
7. Frontend startup fails fast when `BACKEND_URL` or `FRONTEND_BACKEND_API_KEY` is missing.
8. Backend `/metrics` auth behavior is covered for both `METRICS_REQUIRE_API_KEY=true` and false.
9. WebSocket message parser tests for queue/history topics and malformed messages.
10. Docker/CI runs backend build plus frontend `typecheck` before image publish.

## Recommended Work Plan

### Phase 1: Startup and deployment safety

- Make `entrypoint.sh` exit if migration fails.
- Validate frontend required env vars at startup.
- Add a clear `CONFIG_PATH` writable check before migrations.
- Decide and document backend `/metrics` default auth behavior.
- Document split-service API key requirements or persist generated keys under `/config`.

### Phase 2: Contract hardening

- Wrap backend WebSocket relay JSON parsing and validate topic/message shape.
- Centralize frontend API key/env access so missing secrets fail once.
- Start adding runtime validation for backend responses in `backend-client.server.ts`.
- Consolidate browser WebSocket reconnect logic into one hook/helper.

### Phase 3: Backend lifecycle cleanup

- Move constructor-started loops into hosted services where practical.
- Replace static service hooks with small injected facades/factories.
- Move schema recovery helpers out of `Program.cs` into a migration compatibility module.
- Make bulk DB mutation plus VFS invalidation contracts explicit.

### Phase 4: CI and documentation

- Add frontend `npm run typecheck` and backend build/test steps before Docker publish.
- Add deployment checklist covering `/config`, WAL backups, metrics, auth flags, reverse proxy, and rollback.
- Replace hardcoded patch-offset versioning or document the offset source and add guardrails.
- Move dev/test command modes out of the production entry point over time.

## Bottom Line

The application is solving a hard integration problem with a lot of useful operational knowledge baked in. The next reliability jump will come less from feature work and more from making lifecycle boundaries explicit: startup must fail early and loudly, background loops need owners, WebSocket/API contracts need validation, and deployment docs need to match the actual production shape.

The highest-confidence immediate fix from this full-app pass is `entrypoint.sh` migration exit handling. After that, frontend env validation and backend WebSocket relay parsing are the next clean wins.
