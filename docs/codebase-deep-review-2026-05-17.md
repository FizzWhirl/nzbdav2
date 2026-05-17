# Codebase Deep Review - 2026-05-17

Scope: read-only review of queue/Arr paths, streaming and health checks, database/VFS lifecycle, frontend/backend contract, Docker/runtime operations, and general architecture/code quality. This review used three independent sub-agent passes per focus area and then manual confirmation of the strongest repeated findings.

No code changes were made as part of the review, other than creating this report file at the user's request.

## Executive Summary

The recent queue/Arr fixes are directionally sound: failed whole downloads are now handed back to Arr through `RefreshMonitoredDownloads`, stale in-progress SAB queue rows are filtered, Step 3 queue probing is bounded, RAR header timeouts are treated as retryable, and deleted-file health stats are now recorded in the queue delete path. Those changes align with the architecture better than direct Arr queue deletion for whole-download failures.

The largest remaining risk is not one isolated subsystem; it is lifecycle discipline. Several important operations cross database writes, background tasks, VFS cache invalidation, Arr notifications, and streaming cancellation. Some of those handoffs are robust, but others rely on hidden static hooks, fire-and-forget work, caller-saves database patterns, or untested matching heuristics. That is where the next regressions are most likely to come from.

## Highest Priority Findings

### 1. Old hidden-history cleanup queues cleanup rows but never saves them

Severity: High  
Status: Confirmed

`DavDatabaseClient.CleanupOldHiddenHistoryItemsAsync` adds `HistoryCleanupItems`, then bulk-deletes matching hidden `HistoryItems` with `ExecuteDeleteAsync`, but it never calls `SaveChangesAsync` afterward. The caller in `DatabaseMaintenanceService` also does not save the context after this method returns.

Relevant code:

- `backend/Database/DavDatabaseClient.cs`, `CleanupOldHiddenHistoryItemsAsync`
- `backend/Services/DatabaseMaintenanceService.cs`, daily maintenance cleanup call

Impact:

- Old hidden history rows can be deleted without persisting the cleanup queue entry that `HistoryCleanupService` depends on.
- Mounted DavItems and files linked to those old hidden history rows may be left behind.
- Because the history row is already gone, later cleanup has less context to recover from.

This is the most concrete current bug I found in the review.

### 2. Caller-saves database helper methods make lifecycle bugs easy

Severity: High  
Status: Confirmed

`RemoveHistoryItemsAsync`, `ArchiveHistoryItemsAsync`, and `CleanupOldHiddenHistoryItemsAsync` mutate the DbContext but do not consistently own persistence. Some callers correctly call `SaveChanges`, while the hidden-history maintenance path does not. This is the root cause of finding 1.

Relevant code:

- `backend/Database/DavDatabaseClient.cs`, history helper methods
- `backend/Services/ArrMonitoringService.cs`, correct caller-save usage for failed/archived cleanup
- `backend/Services/DatabaseMaintenanceService.cs`, missing save after hidden cleanup

Impact:

- Every new caller must know whether a helper is query-only, staged mutation, bulk mutation, or fully persisted.
- Bulk EF methods (`ExecuteDeleteAsync`, raw SQL) bypass the normal `SaveChangesAsync` path and VFS invalidation behavior.
- The same helper class mixes all of those patterns without making the transaction boundary explicit.

Recommendation: split helper naming or behavior into explicit categories, for example `StageHistoryRemoval`, `RemoveHistoryItemsAndSaveAsync`, and `BulkDelete...AndInvalidateAsync`, or make each command method own its save/transaction boundary.

### 3. Dead Arr queue-deletion code can reintroduce the regression that was just fixed

Severity: High  
Status: Confirmed

`ArrReplacementSearchService.NotifyQueueItemFailedAsync` now correctly calls `RefreshMonitoredDownloads` and lets Sonarr/Radarr apply their own failed-download settings. However, the old direct-removal path still exists as private dead code: `NotifySonarrQueueItemFailedAsync`, `NotifyRadarrQueueItemFailedAsync`, and `DeleteArrQueueRecordAsync` still delete Arr queue records with `RemoveAndBlocklistAndSearch`.

Relevant code:

- `backend/Services/ArrReplacementSearchService.cs`, `NotifyQueueItemFailedAsync`
- `backend/Services/ArrReplacementSearchService.cs`, unused queue-item-failed methods and direct Arr queue deletion helper

Impact:

- Runtime behavior is currently fixed, but the stale code documents the old wrong model and is easy to call by accident in a later refactor.
- It conflicts with the architectural decision that Arr remains the authority for failed-download removal/blocklist/search handling.

Recommendation: remove the unused whole-download direct delete methods, or quarantine them behind comments/tests that make the intended boundary explicit. Partial deleted-file replacement search is a different path and should remain separate.

### 4. `QueueItemProcessor` still contains unused delayed failed-history removal code

Severity: Medium-High  
Status: Confirmed

`RemoveFailedHistoryItemAfterDelay` is private and has no call sites. If revived, it would again remove failed history rows independently of Arr's settings, which is exactly the behavior the recent regression fix moved away from.

Relevant code:

- `backend/Queue/QueueItemProcessor.cs`, `RemoveFailedHistoryItemAfterDelay`

Impact:

- No current runtime effect, but it is stale code in a sensitive area.
- It increases the odds of future work accidentally bypassing Arr's failed-download lifecycle.

Recommendation: delete it once the team is comfortable, or leave an explicit comment explaining why failed history retention is handled by Arr polling/cleanup instead.

### 5. Sonarr/Radarr path caches are static, unsynchronized, and cross-instance

Severity: High  
Status: Confirmed

`SonarrClient` and `RadarrClient` keep static `Dictionary` caches for path-to-ID lookups. They are shared across all configured Arr instances and are read/written from async paths without synchronization.

Relevant code:

- `backend/Clients/RadarrSonarr/SonarrClient.cs`, static dictionaries and cache writes
- `backend/Clients/RadarrSonarr/RadarrClient.cs`, static movie cache and writes

Impact:

- Concurrent health checks or deletion callbacks can read/write the dictionaries at the same time. `Dictionary<TKey,TValue>` is not safe for concurrent read/write.
- Multiple Sonarr/Radarr instances can contaminate each other's caches because the cache key is only a path, not host plus path.
- A stale or cross-instance ID can cause deletion/search actions against the wrong Arr object.

Recommendation: use `ConcurrentDictionary` and include Arr host/API identity in the key, or make caches instance-scoped with a bounded lifetime.

## Queue And Arr Review

### What looks sound

- Whole-download failures now call Arr `RefreshMonitoredDownloads` instead of directly deleting Arr queue records. That fits Sonarr/Radarr's normal failed-download authority model.
- `GetQueueController` checks whether the in-memory active queue item still exists in the database before presenting it as `Downloading`, preventing stale failed/completed jobs from looking queued.
- Partial queue validation deletions use a different model: identify affected Sonarr episodes/Radarr movie, mark matching history failed where possible, and trigger targeted search. That is appropriate because a season pack can partially fail while the whole queue item may not map cleanly to one item.
- Step 3 queue probes are now bounded with `allowFullScan: false`, so expensive full fanout is avoided in the queue path.
- Retryable provider/header/RAR failures now pause the queue item instead of moving directly to failed history.

### Risks

- Arr matching is still heuristic-heavy. Matching by normalized title and scanning up to 1000 history records can miss weird release names, duplicate grabs, nonstandard season-pack naming, or old records beyond the page window.
- Episode extraction covers common `SxxEyy`, ranges, and multi-episode tokens, but absolute numbering, specials, anime-style numbering, and unconventional season-pack names may still fall back to broader history-derived episode IDs.
- `RefreshMonitoredDownloads` calls are fire-and-forget from `MarkQueueItemCompleted`; failures are logged at debug level in the helper. That is probably fine operationally, but there is no durable retry if Arr is temporarily unavailable.

## Streaming And Health Review

### What looks sound

- `SharedStreamManager` and `SharedStreamEntry` have a clear intent: one producer stream per DavItem with multiple readers and backpressure. The race where a losing entry should not evict the winner is handled explicitly.
- RAR header timeout handling now converts provider-side cancellation into retryable queue pauses, which is a good fit for transient provider instability.
- Graceful degradation now has a cap and container-aware behavior, which is safer than unlimited zero-fill for media containers.
- Provider error flushing on shutdown is better than the agents initially suspected: `ProviderErrorService.Dispose` cancels the loop, waits briefly, then flushes buffered events.

### Risks

- `BufferedSegmentStream` still launches important database updates in fire-and-forget tasks when marking corruption or scheduling urgent health checks. If the process exits or the task fails after logging, the stream behavior and database state can diverge.
- `BufferedSegmentStream` reads and writes `DavDatabaseContext` directly from several places instead of going through a scoped service/factory. That makes testing hard and spreads SQLite retry/lifecycle policy into the stream layer.
- `SharedStreamEntry.Dispose` cleans resources without awaiting the pump task. The pump catches most errors, but lifecycle is still implicit: the owner cannot know when the pump fully stopped.
- `ExceptionMiddleware` is doing request error handling plus health-check trigger orchestration. The logic is useful, but it couples HTTP exception handling to database mutation and background health scheduling.

## Database And VFS Lifecycle Review

### What looks sound

- `HistoryCleanupService` handles the normal history removal queue carefully: it gathers affected paths, deletes or unlinks DavItems, triggers VFS forget, removes the cleanup item, and saves.
- Queue Step 5 deletion records `HealthCheckResult.RepairAction.Deleted` inside an explicit transaction with the DavItem bulk delete.
- The recent use of `HistoryItemId` is not vestigial. It is used by cleanup, health check fallback, and history/DavItem lifecycle flows.

### Risks

- VFS invalidation is split between EF `SaveChangesAsync` tracking and manual `TriggerVfsForget` calls for bulk deletes/raw SQL. That is a hidden contract and is easy to miss.
- `DavDatabaseContext.SaveChangesAsync` triggers the VFS callback fire-and-forget. If rclone forget fails, the database commit still succeeds and the failure is not propagated to the caller.
- Several startup schema compatibility routines live in `Program.cs`. They may be necessary for existing deployments, but the application startup path is carrying a lot of one-time migration recovery responsibility.
- Old hidden-history cleanup currently bypasses `HistoryCleanupService` due to the missing save described above.

## Architecture And Code Quality Review

### Static hooks and service-locator patterns

The backend mixes DI with static runtime hooks:

- `DavDatabaseContext.VfsForgetCallback`
- `BufferedSegmentStream.SetMissingArticleLedgerHooks`
- `BufferedSegmentStream.SetMediaAnalysisServiceAccessor`
- `OrganizedLinksUtil.StartRefreshService`

This does work, but it hides dependencies from constructors and makes startup order important. In `Program.cs`, the buffered stream hooks are wired inside a delayed background task after provider-error backfills. That means early streams can run before optional hooks are set.

Recommendation: gradually replace static hooks with small injected facades/factories. The goal is not a big rewrite; it is to make lifetime and dependencies visible in the type system.

### Config lifecycle

`ConfigManager` is a singleton around a mutable dictionary. Updates invoke `OnConfigChanged` while holding the dictionary lock. `GetStaticDownloadKey` also has a side effect: it generates a key and starts an async save from a getter.

Risks:

- One throwing config-change subscriber can prevent later subscribers from running.
- Event handlers run while the config lock is held.
- The static download key can be generated by multiple callers racing before the async save finishes.

Recommendation: move toward immutable config snapshots and async command-style updates for generated settings.

### Frontend/backend contract

The frontend proxy injects `FRONTEND_BACKEND_API_KEY` for authenticated API/metrics requests, and many frontend backend-client methods manually add the same key. The backend includes `Microsoft.AspNetCore.OpenApi`, but no OpenAPI/Swagger mapping is wired.

Risks:

- API contracts are implicit and can drift silently.
- Error shapes differ between SAB controllers, API controllers, and middleware text responses.
- WebSocket message payloads are stringly typed and undocumented.

Recommendation: start with a small contract document or generated OpenAPI for the non-WebDAV API surface, then generate or validate frontend types where practical.

### Frontend auth

The frontend session key persistence is better than the agents initially suspected: it creates `/config/data-protection/frontend-session.key` when possible. However:

- Session max age is one year.
- Login redirect does not preserve the original URL.
- CSRF protection is not obvious around form posts and state-changing routes.

Recommendation: preserve return URLs first because it is low-risk UX improvement. Treat CSRF/session lifetime as a security hardening track.

### Docker/runtime operations

The entrypoint correctly waits for backend readiness before starting frontend and shuts down the other process if one exits. The generated internal API key is process-local; because backend and frontend are started by the same entrypoint, it does not break communication within a normal restart. It is still operationally fragile if either process is ever split, supervised independently, or hot-reloaded without the other.

Recommendation: persist the generated internal API key under `/config` or require it explicitly when running frontend/backend separately.

## Testing And CI

Status: Confirmed gap

The repository has no application test project and no frontend test script. The only test project found is inside vendored SharpCompress. CI builds and pushes Docker images but does not run backend unit tests, frontend typecheck, or targeted integration tests.

Impact:

- Recent regressions around Arr handoff and queue visibility would have been good candidates for focused tests.
- Database lifecycle bugs like hidden-history cleanup are easy to miss because they require specific multi-step state.
- Stream cancellation and provider timeout behavior is currently validated mostly by manual Docker builds/runtime observation.

Recommended first tests:

1. `DavDatabaseClient.CleanupOldHiddenHistoryItemsAsync` persists cleanup queue rows and removes/unlinks DavItems through `HistoryCleanupService`.
2. Failed queue item appears in SAB history but not SAB queue after `MarkQueueItemCompleted`.
3. Whole-download failure calls `RefreshMonitoredDownloads` and does not call Arr queue delete.
4. Partial deleted season-pack files resolve only affected Sonarr episode IDs for common multi-episode/range naming.
5. RAR header timeout throws a retryable queue pause path rather than fatal history failure.
6. Step 3 queue analysis calls `AnalyzeNzbAsync` with `allowFullScan: false`.

## Redundant Or Outdated Code

- `QueueItemProcessor.RemoveFailedHistoryItemAfterDelay` is unused and conflicts with the current Arr-authority model.
- `ArrReplacementSearchService` still contains unused whole-download direct Arr queue delete methods.
- Several optimization/performance documents overlap in the repository root. They may contain useful history, but they are not all active architecture docs.
- `Program.cs` contains extensive schema compatibility and recovery routines. These may be required for existing installations, but they should be treated as production migration code and eventually moved behind clearer startup/migration boundaries.

## Suitability Of Recent Solutions

The recent fixes are suitable in direction and mostly well-scoped:

- Deleted-file stats: good. Queue Step 5 now records deleted health results inside the delete transaction.
- Arr replacement search: good split between whole-download failure and partial deleted-file search. Whole-download failure should remain Arr-authoritative.
- Season-pack handling: reasonable for common naming and queue metadata, but should get regression tests and more examples.
- RAR timeout handling: good. Treating provider/header cancellation as retryable avoids false fatal failures.
- Step 3 fanout fix: good. Queue import probes should stay cheap; full validation belongs to health/Step 5 paths.
- Failed-history handoff fix: good. Filtering stale in-memory queue rows from SAB queue avoids Arr thinking failed items are still downloading.

The main cleanup needed around those fixes is removing dead old behavior and adding tests that lock in the intended boundaries.

## Recommended Work Plan

### Phase 1: Fix confirmed lifecycle bugs

- Fix old hidden-history cleanup so cleanup queue rows are persisted and VFS cleanup runs.
- Remove or quarantine unused failed-history auto-removal code.
- Remove or quarantine unused direct Arr queue-delete whole-failure methods.
- Make Sonarr/Radarr caches thread-safe and instance-aware.

### Phase 2: Add regression coverage

- Add a backend test project focused on queue/history/Arr/cleanup lifecycle.
- Add frontend `typecheck` to CI and a small smoke test for auth/proxy behavior.
- Add fake Arr clients and fake Usenet analysis clients to cover recent regressions without real services.

### Phase 3: Reduce hidden lifecycle coupling

- Replace static stream/database hooks with injected facades or factories.
- Make database helper transaction/save ownership explicit.
- Move health-check trigger logic out of `ExceptionMiddleware` into a service.
- Centralize bulk delete plus VFS invalidation patterns.

### Phase 4: Contract and observability

- Document or generate API contracts for frontend/backend calls.
- Add request/correlation IDs through frontend proxy, backend logs, queue jobs, and health triggers.
- Define WebSocket message schemas or typed payload wrappers.

## Bottom Line

The system has a strong practical architecture for its domain: virtual WebDAV, streaming NZB content, SAB-compatible queue/history, Arr integration, health checks, and Rclone cache invalidation are all pointed in the right direction. The code also shows real operational learning from provider failures, SQLite contention, and media-container behavior.

The next reliability gains should come from tightening lifecycle boundaries rather than adding more recovery code. Start with the confirmed hidden-history cleanup bug and stale Arr cleanup paths, then add tests around the exact queue/history/Arr handoffs that caused the recent regression. That gives the project a firmer floor before larger architecture cleanup begins.
