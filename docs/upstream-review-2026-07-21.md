# Upstream Merge Plan — v0.8.0 → v0.11.12

**Date:** 2026-07-21
**Author:** FizzWhirl fork maintainer (via automated analysis)
**Previous sync:** [`dca490e6`](https://github.com/dgherman/nzbdav2/commit/dca490e6) (v0.8.0, 2026-05-27)
**Target upstream:** [`cd32db3c`](https://github.com/dgherman/nzbdav2/commit/cd32db3c) (v0.11.12)
**Fork HEAD:** 201 commits ahead of upstream

---

## 1. Executive Summary

| Metric | Value |
|---|---|
| Upstream range | `dca490e6` (v0.8.0) → `cd32db3c` (v0.11.12) |
| Non-doc upstream commits | **95** |
| Files changed | **163** |
| Lines added | **+14,403** |
| Lines deleted | **−1,383** |
| New files | **45** (incl. 14 docs, 13 tests) |
| Deleted files | **3** (ActiveStreaming.tsx replaced, 2 provider-stats files relocated) |

### Overall Assessment

| Decision | Count | % of Changes |
|---|---|---|
| **ADOPT** — safe to merge as-is | ~42 commits | ~44% |
| **ADAPT** — merge with modifications | ~28 commits | ~29% |
| **SKIP** — deliberately omit | ~12 commits | ~13% |
| **REVIEW** — needs deeper code-level review | ~13 commits | ~14% |

The upstream delta is substantial but largely additive. Approximately **44% of changes** are low-risk: new isolated files (tests, docs, frontend panels, Prometheus metrics infrastructure) that don't touch fork-divergent code. **~29%** touch shared files where both forks have modified the same code (requiring careful manual reconciliation). **~13%** are doc-only commits that can be cherry-picked or skipped. The remaining **~14%** need deeper code review before a final decision can be made — these are the streaming/memory changes that interact with our custom memory management and connection pooling.

The highest-risk files are [`Program.cs`](backend/Program.cs) (1,643 lines in our fork), [`Dockerfile`](Dockerfile), and [`NzbWebDAV.csproj`](backend/NzbWebDAV.csproj), which have diverged significantly and must be manually reconciled rather than merged.

---

## 2. Upstream Changes by Category with Adoption Decision

### 2.1 Streaming / Memory (30 commits, 31.6%)

| Change Area | Upstream Commits | Adoption Decision | Rationale |
|---|---|---|---|
| **SegmentBufferPool** | ~8 commits | **REVIEW** | Our fork has [`BufferedSegmentStream`](backend/Streams/BufferedSegmentStream.cs) with sliding-window buffers, memory-reuse resizing, and predictive prefetch. Upstream's `SegmentBufferPool` is a different approach (pooled buffer segments). Need to assess: does it complement our sliding-window approach or conflict? If complementary, ADAPT to integrate; if conflicting, SKIP. See [Section 6.1](#61-segmentbufferpool-vs-existing-memory-management). |
| **Prefetch bounding** | ~5 commits | **ADAPT** | Our fork already has range-bounded prefetch in [`BufferedSegmentStream`](backend/Streams/BufferedSegmentStream.cs) (commit `e7cef65`). Upstream's bounding logic may be more refined. Merge the upstream improvements into our existing prefetch path. Low conflict risk; our prefetch is self-contained. |
| **Multi-region SharedStreamEntry** | ~6 commits | **REVIEW** | Our [`SharedStreamEntry`](backend/Streams/SharedStreamEntry.cs) (410 lines) implements reference-counted ring-buffer shared streams. Upstream's multi-region support extends this concept. Need code-level review to determine if upstream's approach is compatible with our ring-buffer design. Likely ADAPT — merge the region-tracking logic while preserving our buffer management. |
| **ITouchableStream** | ~3 commits | **ADOPT** | New interface for stream liveness tracking. No conflict with our streaming code — this is a new abstraction layer. Our [`NzbFileStream`](backend/Streams/NzbFileStream.cs) and [`SharedStreamHandle`](backend/Streams/SharedStreamHandle.cs) can implement it. |
| **OOM guards** | ~5 commits | **ADAPT** | Upstream OOM guards may overlap with our existing memory safeguards: GC heap hard limit env vars in [`Dockerfile`](Dockerfile:62-83), adaptive concurrency throttling in [`QueueManager.cs`](backend/Queue/QueueManager.cs), and sliding-window buffers. Adopt the guards that don't conflict; skip any that duplicate our existing protections. |
| **GcDiagnosticsController** | ~3 commits | **ADOPT** | New diagnostics endpoint. No conflict — this is a net-new file that reads GC stats. Safe to merge as-is. |

### 2.2 Documentation (20 commits, 21.1%)

| Change Area | Upstream Commits | Adoption Decision | Rationale |
|---|---|---|---|
| OOM investigation handoff | ~7 commits | **SKIP** | Upstream's OOM investigation notes are context-specific to their deployment. Our fork has its own memory management story. Skip these docs. |
| Production verification | ~5 commits | **SKIP** | Upstream production deployment notes. Not applicable to our fork. |
| Sync records | ~8 commits | **ADOPT** | Upstream sync/merge records. Useful for tracking future upstream changes. Low-risk — docs only. |

### 2.3 Rclone / WebDAV (12 commits, 12.6%)

| Change Area | Upstream Commits | Adoption Decision | Rationale |
|---|---|---|---|
| PROPFIND cleanup | ~4 commits | **ADOPT** | Our fork already has [`PropFindResponseCleanupMiddleware`](backend/Middlewares/PropFindResponseCleanupMiddleware.cs) (noted as already implemented in the 2026-06-21 review). Upstream's additional PROPFIND changes should be compatible. |
| Ranged-read hardening | ~5 commits | **ADAPT** | May touch [`NzbFileStream`](backend/Streams/NzbFileStream.cs) and WebDAV range handling. Merge the hardening while preserving our fork's range-bounded prefetch logic. |
| Arr replacement search | ~3 commits | **REVIEW** | Our fork has a comprehensive [`ArrReplacementSearchService`](backend/Services/ArrReplacementSearchService.cs) (319 lines) with Sonarr/Radarr integration, retry logic, episode resolution from file names, and queue/history matching. Upstream's implementation may differ in scope or approach. See [Section 6.3](#63-arrreplacementsearchservice-comparison). |

### 2.4 Connection Pool / NNTP (10 commits, 10.5%)

| Change Area | Upstream Commits | Adoption Decision | Rationale |
|---|---|---|---|
| **ProviderCircuitBreaker** | ~4 commits | **REVIEW** | Our [`ConnectionPool`](backend/Clients/Usenet/Connections/ConnectionPool.cs) (562 lines) already has a circuit breaker: `CircuitBreakerFailureThreshold = 5`, `_consecutiveConnectionFailures` tracker, and trip/reset logic. Upstream's `ProviderCircuitBreaker` is a separate class. Need to assess: are these complementary (different scopes) or competing? See [Section 6.2](#62-providercircuitbreaker-vs-existing-connectionpool-circuit-breaker). |
| **LatencyCheckGate** | ~3 commits | **ADOPT** | New isolated class for latency-based gating. No conflict with our connection pool — our pool can consume this as an additional guard. |
| Connection accounting | ~3 commits | **ADAPT** | Our pool already tracks `_activeConnections` by usage type. Upstream's accounting may add more granularity. Merge the improvements into our existing tracking. |

### 2.5 Prometheus / Metrics (6 commits, 6.3%)

| Change Area | Upstream Commits | Adoption Decision | Rationale |
|---|---|---|---|
| **AppMetrics.cs** | ~3 commits | **ADAPT** | Our fork already has [`AppMetrics.cs`](backend/Metrics/AppMetrics.cs) (145 lines) with shared stream counters, pool gauges, seek latency histogram, and pool snapshot registration. Upstream's version may add new metrics or change existing ones. Merge new metrics; preserve our existing metric names and labels to avoid breaking dashboards. The 2026-06-21 review flagged two gaps: (1) high-cardinality path label on shared-stream counters, (2) active-readers gauge not wired. Check if upstream fixes these. |
| **PoolMetricsCollector** | ~1 commit | **ADAPT** | Our fork already has [`PoolMetricsCollector`](backend/Metrics/PoolMetricsCollector.cs) (40 lines) as a `BackgroundService`. Upstream's may differ in collection interval or scope. Merge improvements; keep our 5-second interval. |
| `/metrics` endpoint | ~2 commits | **ADOPT** | Our fork already serves `/metrics` via `prometheus-net.AspNetCore`. Upstream's endpoint wiring should be compatible. Check auth middleware compatibility — our fork has API-key-gated metrics (noted in 2026-06-21 review). |

### 2.6 Frontend / Dashboard (6 commits, 6.3%)

| Change Area | Upstream Commits | Adoption Decision | Rationale |
|---|---|---|---|
| **ActiveStreams panel** | ~3 commits | **ADAPT** | Our fork has [`ActiveStreaming.tsx`](frontend/app/routes/_index/components/dashboard/ActiveStreaming.tsx) (107 lines) with per-provider grouping, speed formatting, and progress bars. Upstream's `ActiveStreams.tsx` replaces `ActiveStreaming.tsx`. Need to compare feature sets. Our component has provider-level bandwidth display and responsive breakpoints that upstream's may lack. Merge upstream improvements into our existing component rather than replacing wholesale. See [Section 6.4](#64-activestreams-panel-vs-existing-activestreaming). |
| Provider stats relocation | ~3 commits | **ADOPT** | Upstream moved provider stats files. Our fork's provider stats are in [`frontend/app/routes/queue/components/provider-stats/`](frontend/app/routes/queue/components/provider-stats/). If the relocation is compatible, adopt it. Otherwise, skip the file moves and only adopt logic changes. |

### 2.7 Config / Infrastructure (6 commits, 6.3%)

| Change Area | Upstream Commits | Adoption Decision | Rationale |
|---|---|---|---|
| **Program.cs (+45 lines)** | ~2 commits | **ADAPT** | Our [`Program.cs`](backend/Program.cs) (1,643 lines) is the most divergent file — 123 fork commits. Upstream's +45 lines likely add new service registrations (StreamSessionRegistry, GcDiagnosticsController, etc.). Must be manually integrated, not merged. Extract the new service registrations and add them to our `Program.cs` individually. |
| **Dockerfile (+22 lines)** | ~2 commits | **ADAPT** | Our [`Dockerfile`](Dockerfile) (86 lines) has fork-specific: multi-stage build, .NET 10.0-alpine runtime, ffmpeg/rclone/fuse3/tzdata packages, GC tuning env vars (`DOTNET_GCServer`, `DOTNET_GCHeapHardLimit`). Upstream's +22 lines may add new packages or config. Manually integrate any beneficial additions. |
| **.csproj updates** | ~2 commits | **ADAPT** | Our [`NzbWebDAV.csproj`](backend/NzbWebDAV.csproj) (40 lines) has fork-specific: .NET 10.0, ZstdSharp.Port, UsenetSharp, SharpCompress references. Upstream may have version bumps or new package references. Manually merge; never overwrite our package set. |

### 2.8 Tests (3 commits, 3.2%)

| Change Area | Upstream Commits | Adoption Decision | Rationale |
|---|---|---|---|
| 13 new test files (+2,139 lines) | 3 commits | **ADOPT** | New test files under `backend.Tests/`. No conflict with our fork. Adopt all tests — they validate upstream behavior and may catch regressions in our adaptations. |

---

## 3. Fork Divergence Impact Matrix

### 3.1 NZB Storage: Zstd-compressed in-DB vs Filesystem Blobstore

| Aspect | Detail |
|---|---|
| **Our approach** | Zstd-compressed NZB contents stored in [`QueueNzbContents`](backend/Database/Models/QueueNzbContents.cs) column; [`CompressionUtil`](backend/Utils/CompressionUtil.cs) for Zstd codec |
| **Upstream approach** | Filesystem blobstore with `nzbBlobId` references |
| **Files affected** | [`HistoryItem.cs`](backend/Database/Models/HistoryItem.cs), [`DavDatabaseContext.cs`](backend/Database/DavDatabaseContext.cs), `DatabaseStoreNzbFile.cs` |
| **Upstream touches this area?** | Likely — any blobstore-related changes in the 95 commits |
| **Merge strategy** | **MANUAL RECONCILIATION.** Never accept upstream blobstore changes that modify the NZB storage path. If upstream adds new blobstore features, evaluate whether they can be adapted to our in-DB model. Changes to `DavDatabaseContext.cs` must preserve our `NzbContents` column and Zstd compression pipeline. |
| **Risk level** | **HIGH** — overwriting storage model would break all existing NZB data |

### 3.2 Rclone Integration: DI-injected Singleton vs Static Class

| Aspect | Detail |
|---|---|
| **Our approach** | [`RcloneRcService`](backend/Services/RcloneRcService.cs) registered as a DI singleton in `Program.cs` |
| **Upstream approach** | Static `RcloneClient` class |
| **Files affected** | [`Program.cs`](backend/Program.cs), [`RcloneRcService`](backend/Services/RcloneRcService.cs) |
| **Upstream touches this area?** | Possibly — WebDAV/rclone changes (12 commits in category 2.3) |
| **Merge strategy** | **CHERRY-PICK with adaptation.** If upstream modifies its static `RcloneClient`, manually port the logic changes to our `RcloneRcService` singleton. Never let upstream's static class replace our DI registration. |
| **Risk level** | **MEDIUM** — DI vs static is an architectural choice; logic changes are portable |

### 3.3 FK Cascade: Cascade Delete vs Removed

| Aspect | Detail |
|---|---|
| **Our approach** | Cascade delete on `DavItems` in [`DavDatabaseContext.cs`](backend/Database/DavDatabaseContext.cs) `OnModelCreating` |
| **Upstream approach** | Removed cascade (conflicts with blobstore references) |
| **Files affected** | [`DavDatabaseContext.cs`](backend/Database/DavDatabaseContext.cs) |
| **Upstream touches this area?** | Possibly — any EF Core model changes |
| **Merge strategy** | **MANUAL RECONCILIATION.** Always preserve our cascade delete configuration. If upstream adds new entity relationships, ensure cascade behavior is applied where appropriate for our in-DB model. |
| **Risk level** | **MEDIUM** — losing cascade would cause orphaned child rows on delete |

### 3.4 RarFile Handling: DatabaseStoreRarFile vs Removing RarFile→MultipartFile

| Aspect | Detail |
|---|---|
| **Our approach** | [`DatabaseStoreRarFile`](backend/WebDav/DatabaseStoreRarFile.cs) for Zstd in-DB RAR storage; `DavRarFile` model still active |
| **Upstream approach** | Removing `RarFile` support, consolidating into `DavMultipartFile` |
| **Files affected** | [`DatabaseStoreRarFile.cs`](backend/WebDav/DatabaseStoreRarFile.cs), [`RarProcessor.cs`](backend/Queue/FileProcessors/RarProcessor.cs), [`DavDatabaseContext.cs`](backend/Database/DavDatabaseContext.cs), `DavDatabaseClient.cs`, `DatabaseStoreCollection.cs`, `HealthCheckService.cs` |
| **Upstream touches this area?** | **Very likely** — upstream continues RarFile→MultipartFile migration |
| **Merge strategy** | **SELECTIVE SKIP + MANUAL RECONCILIATION.** The 2026-05-27 sync already established the pattern (see [upstream-sync-2026-05-27.md](docs/upstream-sync-2026-05-27.md) § "Deliberately Skipped"). Continue the same approach: skip any upstream commits that remove RarFile references from `DavDatabaseClient.cs`, `DatabaseStoreCollection.cs`, `DatabaseStoreSymlinkCollection.cs`, `GetFileDetailsController.cs`, and `HealthCheckService.cs`. For `RarProcessor.cs`, merge upstream improvements to segment size precomputation while preserving our global connection cap (`MaxGlobalRarHeaderConnections = 6`) and abort-on-timeout logic. |
| **Risk level** | **HIGH** — accepting RarFile removal would break Zstd in-DB RAR storage |

### 3.5 Connection Pool: Custom Circuit Breaker + Reserve vs Standard Pool

| Aspect | Detail |
|---|---|
| **Our approach** | [`ConnectionPool`](backend/Clients/Usenet/Connections/ConnectionPool.cs) (562 lines) with circuit breaker (`CircuitBreakerFailureThreshold = 5`), connection accounting, doomed connections tracking, stuck detection, reserve mechanism, `CombinedSemaphoreSlim` gate, `AppMetrics.IPoolSnapshotProvider` integration |
| **Upstream approach** | Standard pool; now has `ProviderCircuitBreaker` as a separate class |
| **Files affected** | [`ConnectionPool.cs`](backend/Clients/Usenet/Connections/ConnectionPool.cs), [`Program.cs`](backend/Program.cs) |
| **Upstream touches this area?** | **Yes** — `ProviderCircuitBreaker`, `LatencyCheckGate`, connection accounting (10 commits) |
| **Merge strategy** | **ADAPT with caution.** Our `ConnectionPool.cs` is already heavily customized. Never replace it with upstream's version. Instead: (a) evaluate `ProviderCircuitBreaker` as a complementary layer (it may operate at a different scope — provider-level vs pool-level); (b) adopt `LatencyCheckGate` as an additional guard; (c) merge upstream connection accounting improvements into our existing tracking. See [Section 6.2](#62-providercircuitbreaker-vs-existing-connectionpool-circuit-breaker). |
| **Risk level** | **HIGH** — connection pool is mission-critical; regressions cause streaming failures |

### 3.6 Queue Pipeline: Parallel Steps + Adaptive Concurrency vs Serial Processing

| Aspect | Detail |
|---|---|
| **Our approach** | [`QueueManager.cs`](backend/Queue/QueueManager.cs) and `QueueItemProcessor.cs` with discrete step processors, parallel step execution (Steps 4/5), adaptive concurrency, batched DB updates, smart probe (first/last segment), per-file deadlines |
| **Upstream approach** | Serial processing (may have evolved) |
| **Files affected** | [`QueueManager.cs`](backend/Queue/QueueManager.cs), `QueueItemProcessor.cs` |
| **Upstream touches this area?** | Possibly — streaming/memory changes may affect queue processing |
| **Merge strategy** | **MANUAL RECONCILIATION.** Our queue pipeline is one of the fork's core differentiators. Any upstream changes to queue processing must be carefully ported to our parallel architecture. Most likely outcome: upstream queue changes are minor and can be adapted; if they're structural, skip them. |
| **Risk level** | **MEDIUM** — queue pipeline is complex but upstream is unlikely to have matching changes |

### 3.7 Preview / HLS: ffmpeg + HLS.js + Transcoding vs Not Present

| Aspect | Detail |
|---|---|
| **Our approach** | [`PreviewHlsController`](backend/Api/Controllers/PreviewHls/PreviewHlsController.cs) (344 lines), [`PreviewRemuxController`](backend/Api/Controllers/PreviewRemux/PreviewRemuxController.cs) (217 lines), [`PreviewProcessLimiter`](backend/Services/PreviewProcessLimiter.cs), HLS.js frontend player, codec compatibility detection |
| **Upstream approach** | Not present in upstream |
| **Files affected** | (Various new files unique to our fork) |
| **Upstream touches this area?** | **No** — upstream doesn't have preview/HLS |
| **Merge strategy** | **NO CONFLICT.** These are net-new files in our fork. No upstream changes will touch them. Verify after merge that no shared dependencies (e.g., `DavItem` model, `NzbFileStream`) were changed in ways that break preview. |
| **Risk level** | **LOW** — isolated feature, but verify integration points |

---

## 4. Prioritized Merge Order (Batch Plan)

### Batch 1: Safe, Low-Risk Changes

**Goal:** Quick wins with zero conflict risk. Build momentum and verify the merge workflow.

| Item | Upstream Commits | Est. Files | Strategy |
|---|---|---|---|
| Documentation (sync records) | ~8 commits | 14 `.md` files | `git cherry-pick` |
| Test files | 3 commits | 13 test files | `git cherry-pick` (new files only) |
| `.gitignore` updates | 1 commit | 1 file | Manual review then cherry-pick |
| Frontend provider stats relocation | ~3 commits | 2-3 `.tsx` files | Cherry-pick; verify paths match our structure |

**Conflict resolution:** None expected. These are all new files or trivial edits.

**Verification:** `dotnet build` succeeds; frontend `npm run build` succeeds.

---

### Batch 2: New Isolated Features

**Goal:** Adopt net-new features that don't touch fork-divergent code. These are self-contained additions.

| Item | Upstream Commits | Key Files | Strategy |
|---|---|---|---|
| `AppMetrics.cs` (new metrics) | ~3 commits | [`backend/Metrics/AppMetrics.cs`](backend/Metrics/AppMetrics.cs) | ADAPT — merge new metric definitions into our existing file; preserve our metric names |
| `SegmentBufferPool` | ~8 commits | `backend/Streams/SegmentBufferPool.cs` (new) | ADOPT file as-is, then evaluate integration (see [Section 6.1](#61-segmentbufferpool-vs-existing-memory-management)) |
| `ProviderCircuitBreaker` | ~4 commits | `backend/Clients/Usenet/Connections/ProviderCircuitBreaker.cs` (new) | ADOPT file as-is, then evaluate integration (see [Section 6.2](#62-providercircuitbreaker-vs-existing-connectionpool-circuit-breaker)) |
| `LatencyCheckGate` | ~3 commits | `backend/Clients/Usenet/Connections/LatencyCheckGate.cs` (new) | ADOPT — new isolated class |
| `StreamSessionRegistry` | ~2 commits | `backend/Streams/StreamSessionRegistry.cs` (new) | ADOPT — new isolated class |
| `PropFindResponseCleanupMiddleware` | ~1 commit | [`backend/Middlewares/PropFindResponseCleanupMiddleware.cs`](backend/Middlewares/PropFindResponseCleanupMiddleware.cs) | ADOPT — already present but may have upstream updates |
| `GcDiagnosticsController` | ~3 commits | `backend/Api/Controllers/GcDiagnosticsController.cs` (new) | ADOPT — new isolated controller |
| `ITouchableStream` | ~3 commits | `backend/Streams/ITouchableStream.cs` (new) | ADOPT — new interface |
| `ArrReplacementSearchService` | ~3 commits | [`backend/Services/ArrReplacementSearchService.cs`](backend/Services/ArrReplacementSearchService.cs) | REVIEW first (see [Section 6.3](#63-arrreplacementsearchservice-comparison)), then ADAPT |
| `ActiveStreams.tsx` | ~3 commits | `frontend/app/routes/_index/components/dashboard/ActiveStreams.tsx` (new) | ADAPT — compare with our `ActiveStreaming.tsx`, merge improvements (see [Section 6.4](#64-activestreams-panel-vs-existing-activestreaming)) |

**Conflict resolution:** New files should merge cleanly. For `AppMetrics.cs`, `PropFindResponseCleanupMiddleware.cs`, and `ArrReplacementSearchService.cs` (files that exist in both forks), use `git merge` with manual conflict resolution, favoring our implementation where they differ.

**Verification:** Full build; metrics endpoint returns expected shape; frontend dashboard renders without regressions.

---

### Batch 3: Modified Shared Files (Moderate Risk)

**Goal:** Merge improvements to files that both forks have modified. Each file needs individual attention.

| Item | Files | Conflict Areas | Strategy |
|---|---|---|---|
| HealthCheckService | [`backend/Services/HealthCheckService.cs`](backend/Services/HealthCheckService.cs) | Upstream may remove RarFile checks; we need them | ADAPT — merge non-RarFile changes; skip RarFile removals |
| StreamingConnectionLimiter | [`backend/Services/StreamingConnectionLimiter.cs`](backend/Services/StreamingConnectionLimiter.cs) | Our fork has custom pool-based limiter (382 lines) | ADAPT — merge upstream improvements that don't conflict with our pooling model |
| ExceptionMiddleware | [`backend/Middlewares/ExceptionMiddleware.cs`](backend/Middlewares/ExceptionMiddleware.cs) | Both forks may have error-handling changes | ADAPT — merge improvements; test error response formats |
| WebsocketManager | [`backend/Websocket/WebsocketManager.cs`](backend/Websocket/WebsocketManager.cs) | Our fork has custom websocket topics | ADAPT — merge upstream improvements; preserve our topic structure |
| ConfigManager | [`backend/Config/ConfigManager.cs`](backend/Config/ConfigManager.cs) | Our fork has streaming priority, download connections, shared stream config | ADAPT — merge new config keys; preserve our custom configuration |
| RarProcessor | [`backend/Queue/FileProcessors/RarProcessor.cs`](backend/Queue/FileProcessors/RarProcessor.cs) | Upstream continues RarFile→MultipartFile migration | SKIP RarFile removals; ADAPT segment-size improvements (pattern from 2026-05-27 sync) |
| DatabaseStoreMultipartFile | [`backend/WebDav/DatabaseStoreMultipartFile.cs`](backend/WebDav/DatabaseStoreMultipartFile.cs) | Our fork has custom affinity key, analysis mode, preview mode | ADAPT — merge upstream improvements; preserve our custom paths |
| Multi-region SharedStreamEntry | [`backend/Streams/SharedStreamEntry.cs`](backend/Streams/SharedStreamEntry.cs) | Our fork has ring-buffer design (410 lines) | REVIEW first, then ADAPT (see [Section 6.1](#61-segmentbufferpool-vs-existing-memory-management)) |

**Conflict resolution:** For each file:
1. `git merge` the upstream commit
2. Resolve conflicts manually, favoring our fork's custom logic
3. Apply upstream improvements that don't conflict
4. Build and run relevant tests after each file

**Verification:** Full build; streaming smoke test; health check endpoint; WebSocket connectivity; config readback.

---

### Batch 4: High-Conflict Files (Manual Reconciliation)

**Goal:** Integrate upstream changes to the most divergent files. These cannot be merged — they must be manually ported.

| Item | File | Upstream Delta | Strategy |
|---|---|---|---|
| Program.cs | [`backend/Program.cs`](backend/Program.cs) | +45 lines | **MANUAL PORT.** Extract upstream's new service registrations, middleware wiring, and endpoint mappings. Add them individually to our `Program.cs`. Never use `git merge` on this file. Key additions to look for: `StreamSessionRegistry` DI registration, `GcDiagnosticsController` endpoint mapping, `ProviderCircuitBreaker` DI registration, any new `UseMetrics()` or `/metrics` endpoint configuration. |
| Dockerfile | [`Dockerfile`](Dockerfile) | +22 lines | **MANUAL PORT.** Review upstream's additions line-by-line. Likely changes: new `apk add` packages, new ENV vars, build-stage changes. Add only what benefits our deployment. Preserve our: multi-stage build, ffmpeg/rclone/fuse3/tzdata packages, GC tuning env vars (`DOTNET_GCServer`, `DOTNET_GCHeapHardLimit`). |
| NzbWebDAV.csproj | [`backend/NzbWebDAV.csproj`](backend/NzbWebDAV.csproj) | Unknown delta | **MANUAL PORT.** Review upstream's package reference changes. Adopt version bumps for shared packages (prometheus-net, Serilog, EF Core). Never remove our fork-specific packages: `ZstdSharp.Port`, `UsenetSharp`, `SharpCompress`. Check for new upstream package references that may be needed by new features. |

**Conflict resolution:** These files should never be auto-merged. Use `git show` on upstream commits to see the diff, then manually apply relevant changes to our files.

**Verification:** Full build; Docker image build; container startup; all APIs respond; streaming works end-to-end.

---

## 5. Risk Register

| # | Risk | Severity | Affected Batch | Mitigation |
|---|---|---|---|---|
| R1 | **Blobstore changes overwrite in-DB NZB storage** — Upstream blobstore modifications in `DavDatabaseContext.cs`, `HistoryItem.cs`, or `DatabaseStoreNzbFile.cs` could corrupt our Zstd compression pipeline | **HIGH** | Batch 3, 4 | Never auto-merge these files. Manually review every line of upstream diff. Reject any change that modifies the NZB storage path. |
| R2 | **RarFile removal breaks Zstd in-DB RAR storage** — Upstream continues consolidating RAR into MultipartFile, removing `DavRarFile` references | **HIGH** | Batch 3 | Continue the established pattern from 2026-05-27 sync: skip RarFile removal commits. Preserve `DatabaseStoreRarFile.cs` and all RarFile references in `DavDatabaseClient.cs`, `DatabaseStoreCollection.cs`, `HealthCheckService.cs`. |
| R3 | **ConnectionPool regression** — Upstream pool changes could introduce bugs in our heavily customized `ConnectionPool.cs` | **HIGH** | Batch 2, 3 | Never replace our `ConnectionPool.cs`. Adopt `ProviderCircuitBreaker` and `LatencyCheckGate` as separate classes. Test connection acquisition, circuit breaker trip/reset, and reserve mechanism after each change. |
| R4 | **Program.cs merge conflict** — Auto-merging `Program.cs` would create an unresolvable conflict (123 fork commits) | **HIGH** | Batch 4 | Never `git merge` this file. Always manually port individual service registrations. |
| R5 | **Dockerfile regression** — Losing our GC tuning env vars could cause OOM in production | **MEDIUM** | Batch 4 | Manually review upstream Dockerfile diff. Preserve `DOTNET_GCServer`, `DOTNET_GCConcurrent`, `DOTNET_GCHeapHardLimit`. |
| R6 | **Metrics cardinality regression** — The 2026-06-21 review flagged a high-cardinality path label on shared-stream counters. Upstream changes may reintroduce or fail to fix this | **MEDIUM** | Batch 2 | Verify after merge that `SharedStreamMisses` counter has only bounded label values (`no_entry`, `position_out_of_range`, `existing_entry_unattachable`), not unbounded path labels. |
| R7 | **Active-readers gauge not wired** — The 2026-06-21 review found `SharedStreamActiveReaders` is declared but not set. Upstream may or may not fix this | **MEDIUM** | Batch 2 | Verify after merge that `SharedStreamActiveReaders` is updated in `PoolMetricsCollector` or `SharedStreamManager`. If not, wire it ourselves. |
| R8 | **SegmentBufferPool conflicts with BufferedSegmentStream** — Two competing buffer management strategies could cause double-buffering or memory bloat | **MEDIUM** | Batch 2 | Review before integrating. If complementary, use SegmentBufferPool for article-level buffers and keep BufferedSegmentStream for file-level sliding windows. If conflicting, skip SegmentBufferPool. |
| R9 | **Frontend ActiveStreams replaces our richer ActiveStreaming** — Upstream's `ActiveStreams.tsx` may lack our provider-level bandwidth display and responsive breakpoints | **LOW** | Batch 2 | Compare components side-by-side. Merge upstream improvements into our `ActiveStreaming.tsx` rather than replacing it. |
| R10 | **NuGet version conflicts** — Upstream `.csproj` changes may introduce package version mismatches with our fork-specific packages | **LOW** | Batch 4 | Build and test after `.csproj` changes. Run `dotnet list package --outdated` to check compatibility. |
| R11 | **Auth middleware incompatibility with /metrics** — Upstream's `/metrics` endpoint configuration may bypass our API-key gate | **LOW** | Batch 2, 4 | Verify `/metrics` respects our auth configuration. The 2026-06-21 review confirmed our metrics are API-key-gated. |
| R12 | **Preview/HLS breakage from shared dependency changes** — Upstream changes to `DavItem`, `NzbFileStream`, or `SharedStreamManager` could break our preview pipeline | **LOW** | Batch 2, 3 | Run preview smoke test (HLS playback, remux) after batches 2 and 3. |

---

## 6. Items Requiring Deeper Code Review Before Merge

These are the **REVIEW** items from Section 2 that require examining the actual upstream code before a final adoption decision can be made.

### 6.1 SegmentBufferPool vs Existing Memory Management

**Files to compare:**
- Upstream: `backend/Streams/SegmentBufferPool.cs` (new)
- Our fork: [`backend/Streams/BufferedSegmentStream.cs`](backend/Streams/BufferedSegmentStream.cs) (269 lines), [`backend/Streams/NzbFileStream.cs`](backend/Streams/NzbFileStream.cs) (291 lines), [`backend/Streams/SharedStreamEntry.cs`](backend/Streams/SharedStreamEntry.cs) (410 lines)

**Questions to answer:**
1. Does `SegmentBufferPool` operate at the article-segment level (individual NNTP article buffers) or at the file-stream level (multi-megabyte streaming buffers)?
2. If article-level: it complements our sliding-window file-level buffers — **ADOPT** and wire into the article fetch path.
3. If file-level: it may conflict with our sliding-window approach — evaluate whether it's better than our current memory management. If equivalent, **SKIP** to avoid churn. If superior, plan a migration.
4. Does it use `ArrayPool<byte>` or its own pooling? Our fork uses `ArrayPool<byte>` via `MemoryStream.GetBuffer()` reuse (commit `e64906a`).
5. What are the pool size limits? Do they respect `DOTNET_GCHeapHardLimit`?

**Recommendation:** Likely **ADAPT** — adopt as a lower-level article buffer pool that feeds into our existing file-level sliding-window buffers.

### 6.2 ProviderCircuitBreaker vs Existing ConnectionPool Circuit Breaker

**Files to compare:**
- Upstream: `backend/Clients/Usenet/Connections/ProviderCircuitBreaker.cs` (new)
- Our fork: [`backend/Clients/Usenet/Connections/ConnectionPool.cs`](backend/Clients/Usenet/Connections/ConnectionPool.cs) (562 lines, lines 64-67: circuit breaker state, line 67: `CircuitBreakerFailureThreshold = 5`)

**Questions to answer:**
1. What scope does `ProviderCircuitBreaker` operate at? Provider-level (across all pools for a provider) or pool-level (within a single pool)?
2. Our circuit breaker is pool-level (tied to a `ConnectionPool<T>` instance). A provider-level breaker would be complementary — it could trip for ALL pools of a failing provider.
3. What trip/reset logic does it use? Our uses: 5 consecutive failures → trip; any success → reset. Does upstream use time-based decay, half-open states, or different thresholds?
4. Does `ProviderCircuitBreaker` integrate with `AppMetrics` for observability? Our pool-level breaker already exports gauges.
5. Does `Program.cs` wire `ProviderCircuitBreaker` at the provider level? If so, we need to manually port that registration.

**Recommendation:** Likely **ADAPT** — adopt `ProviderCircuitBreaker` as a provider-level circuit breaker that sits above our pool-level breaker. Wire it in `Program.cs` during Batch 4. The 2026-06-21 review noted our threshold magic number should be deduplicated (`CircuitBreakerFailureThreshold`); check if upstream shares this constant.

### 6.3 ArrReplacementSearchService Comparison

**Files to compare:**
- Upstream: `backend/Services/ArrReplacementSearchService.cs`
- Our fork: [`backend/Services/ArrReplacementSearchService.cs`](backend/Services/ArrReplacementSearchService.cs) (319 lines)

**Our implementation (confirmed present):**
- `NotifyQueueItemFailedAsync` — refreshes monitored downloads on queue failure
- `NotifyQueueFilesDeletedAsync` — triggers replacement search when health check deletes files
- Full Sonarr integration: episode resolution from file names via regex (`SonarrEpisodeRangeRegex`, `SonarrEpisodeTokenRegex`, `SonarrEpisodeXRegex`), queue record matching, history record matching, `MarkHistoryFailedAsync`
- Full Radarr integration: movie ID resolution from queue/history, `SearchMovieAsync`
- Retry logic: 3 attempts with 5s/10s/15s exponential backoff, retryable exception filtering
- Multi-instance support: iterates all configured Arr clients

**Questions to answer:**
1. Is upstream's implementation functionally equivalent, a subset, or a superset of ours?
2. If upstream has features we lack (e.g., Lidarr support, additional notification paths), adopt them.
3. If ours has features upstream lacks (e.g., the sophisticated episode-from-filename regex extraction), keep our implementation and only adopt upstream additions.
4. Are the method signatures compatible? If upstream changed the API, we need to adapt our callers.

**Recommendation:** Likely **ADAPT** — our implementation is comprehensive. Adopt any upstream additions we lack; keep our existing logic.

### 6.4 ActiveStreams Panel vs Existing ActiveStreaming

**Files to compare:**
- Upstream: `frontend/app/routes/_index/components/dashboard/ActiveStreams.tsx` (new, replaces ActiveStreaming.tsx)
- Our fork: [`frontend/app/routes/_index/components/dashboard/ActiveStreaming.tsx`](frontend/app/routes/_index/components/dashboard/ActiveStreaming.tsx) (107 lines)

**Our implementation features:**
- Per-provider grouping with provider name, connection count badge, current speed
- Responsive breakpoints: 5 streams on desktop, 2 on mobile
- Stream item: file name truncation, progress percentage, byte position tracking
- Aggregate speed display in header
- Graceful empty state: "No providers configured" / "Nothing streaming"

**Questions to answer:**
1. What does upstream's `ActiveStreams.tsx` add that ours lacks? (e.g., stream duration, transfer progress bar, cancel button)
2. Does it use the same data model (`ConnectionUsageContext[]` grouped by provider)?
3. Does it have responsive design? Mobile support?
4. Is it a rename (ActiveStreaming → ActiveStreams) with improvements, or a complete rewrite?

**Recommendation:** **ADAPT** — merge upstream improvements into our `ActiveStreaming.tsx`. If upstream renamed the file, keep our filename or rename to match (decide based on import consistency). Never lose our provider-level bandwidth display.

### 6.5 Multi-Region SharedStreamEntry

**Files to compare:**
- Upstream: `backend/Streams/SharedStreamEntry.cs` (modified)
- Our fork: [`backend/Streams/SharedStreamEntry.cs`](backend/Streams/SharedStreamEntry.cs) (410 lines)

**Our implementation:**
- Reference-counted ring buffer
- Async lazy initialization with handle-leak protection
- Position-based attachment (second reader attaches at its requested position)
- Eviction on reader disconnect when reference count reaches zero

**Questions to answer:**
1. What does "multi-region" mean? Does it allow multiple disjoint byte ranges to be served from one entry?
2. Does it change the buffer data structure (ring buffer → something else)?
3. Does it affect how `SharedStreamManager` creates and looks up entries?
4. Is our handle-leak protection compatible with the new design?

**Recommendation:** **REVIEW** — needs careful code-level comparison. Multi-region support could be valuable (serving multiple byte ranges from one download) but may require significant adaptation to our ring-buffer model.

---

## 7. Next Steps

### Immediate (Before Starting Merge)

1. **Fetch upstream and create integration branch:**
   ```bash
   git remote add upstream https://github.com/dgherman/nzbdav2.git  # if not already added
   git fetch upstream
   git checkout -b upstream-merge/v0.11.12
   ```

2. **Generate full diff for review:**
   ```bash
   git diff dca490e6..cd32db3c --stat > /tmp/upstream-diff-stat.txt
   git diff dca490e6..cd32db3c -- backend/Program.cs > /tmp/upstream-program.cs.diff
   git diff dca490e6..cd32db3c -- Dockerfile > /tmp/upstream-dockerfile.diff
   git diff dca490e6..cd32db3c -- backend/NzbWebDAV.csproj > /tmp/upstream-csproj.diff
   ```

3. **Complete the REVIEW items in Section 6:**
   - [ ] Compare `SegmentBufferPool` with our `BufferedSegmentStream`
   - [ ] Compare `ProviderCircuitBreaker` with our `ConnectionPool` circuit breaker
   - [ ] Compare upstream `ArrReplacementSearchService` with ours
   - [ ] Compare `ActiveStreams.tsx` with our `ActiveStreaming.tsx`
   - [ ] Review multi-region `SharedStreamEntry` changes

### Batch Execution Order

4. **Execute Batch 1** (safe changes) — cherry-pick documentation, tests, .gitignore, frontend relocations
5. **Build & verify** — `dotnet build`, frontend `npm run build`
6. **Execute Batch 2** (new isolated features) — adopt new files; adapt shared metrics/config files
7. **Build & smoke test** — verify metrics endpoint, frontend dashboard, basic streaming
8. **Execute Batch 3** (modified shared files) — one file at a time with conflict resolution
9. **Regression test** — run streaming smoke test, health check cycle, WebSocket connectivity
10. **Execute Batch 4** (high-conflict files) — manually port `Program.cs`, `Dockerfile`, `.csproj` changes
11. **Full integration test** — Docker build, container startup, end-to-end streaming, preview/HLS playback, metrics verification
12. **Update docs** — record the new sync point in [`docs/upstream-sync-2026-07-21.md`](docs/upstream-sync-2026-07-21.md) and update CLAUDE.md

### Post-Merge Verification Checklist

- [ ] `dotnet build` succeeds with no warnings
- [ ] Frontend `npm run build` succeeds
- [ ] Docker image builds successfully
- [ ] Container starts and all health checks pass
- [ ] `/metrics` endpoint returns expected metrics (verify no high-cardinality path labels)
- [ ] `SharedStreamActiveReaders` gauge is wired and non-zero during streaming
- [ ] `CircuitBreakerFailureThreshold` constant is centralized (not duplicated >5 times)
- [ ] Streaming smoke test: download a known NZB, verify it completes
- [ ] Preview/HLS smoke test: play a video file through the frontend player
- [ ] Connection pool: verify circuit breaker trips and resets correctly
- [ ] Arr replacement: verify queue-failure notification triggers Arr refresh
- [ ] Rclone/WebDAV: verify directory listing and file access work
- [ ] All existing NZB data is readable (Zstd in-DB storage intact)
- [ ] RarFile handling works (verify `DatabaseStoreRarFile` path still functional)

---

## Appendix A: File Categories for Merge

### New Files (Adopt As-Is)
`SegmentBufferPool.cs`, `ProviderCircuitBreaker.cs`, `LatencyCheckGate.cs`, `StreamSessionRegistry.cs`, `ITouchableStream.cs`, `GcDiagnosticsController.cs`, `ActiveStreams.tsx`, 13 test files, 14 doc files

### Existing Files — Both Forks Modified (Manual Merge)
[`AppMetrics.cs`](backend/Metrics/AppMetrics.cs), [`PropFindResponseCleanupMiddleware.cs`](backend/Middlewares/PropFindResponseCleanupMiddleware.cs), [`ArrReplacementSearchService.cs`](backend/Services/ArrReplacementSearchService.cs), [`HealthCheckService.cs`](backend/Services/HealthCheckService.cs), [`StreamingConnectionLimiter.cs`](backend/Services/StreamingConnectionLimiter.cs), [`ExceptionMiddleware.cs`](backend/Middlewares/ExceptionMiddleware.cs), [`WebsocketManager.cs`](backend/Websocket/WebsocketManager.cs), [`ConfigManager.cs`](backend/Config/ConfigManager.cs), [`RarProcessor.cs`](backend/Queue/FileProcessors/RarProcessor.cs), [`DatabaseStoreMultipartFile.cs`](backend/WebDav/DatabaseStoreMultipartFile.cs), [`SharedStreamEntry.cs`](backend/Streams/SharedStreamEntry.cs), [`BufferedSegmentStream.cs`](backend/Streams/BufferedSegmentStream.cs), [`NzbFileStream.cs`](backend/Streams/NzbFileStream.cs)

### Existing Files — Fork-Divergent (Manual Port Only)
[`Program.cs`](backend/Program.cs), [`Dockerfile`](Dockerfile), [`NzbWebDAV.csproj`](backend/NzbWebDAV.csproj), [`DavDatabaseContext.cs`](backend/Database/DavDatabaseContext.cs), [`ConnectionPool.cs`](backend/Clients/Usenet/Connections/ConnectionPool.cs), [`QueueManager.cs`](backend/Queue/QueueManager.cs)

### Existing Files — Upstream Deleted (Preserve Ours)
`ActiveStreaming.tsx` (replaced by `ActiveStreams.tsx` — keep ours or merge), 2 provider-stats files (relocated — adopt relocation)

---

## Appendix B: Design Decision Summary

| # | Decision | Verdict | Rationale |
|---|---|---|---|
| 1 | SegmentBufferPool vs existing memory management | **REVIEW → likely ADAPT** | Adopt as lower-level article buffer pool; keep BufferedSegmentStream for file-level sliding windows |
| 2 | ProviderCircuitBreaker vs ConnectionPool circuit breaker | **REVIEW → likely ADAPT** | Adopt as provider-level breaker above our pool-level breaker; complementary scopes |
| 3 | ArrReplacementSearchService comparison | **REVIEW → likely ADAPT** | Our implementation is comprehensive; adopt upstream additions we lack |
| 4 | ActiveStreams panel vs existing ActiveStreaming | **ADAPT** | Merge upstream improvements into our component; keep our provider bandwidth display |
| 5 | Prometheus /metrics compatibility | **ADOPT with verification** | Our auth middleware should be compatible; verify API-key gate after merge |
