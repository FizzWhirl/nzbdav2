# Upstream Sync Analysis — 2026-03-10

Comparison of upstream [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav) releases
v0.5.38 and v0.6.0 against our fork [dgherman/nzbdav2](https://github.com/dgherman/nzbdav2).

Previous sync documented in `/UPSTREAM_ANALYSIS.md` (adopted: repair retry logic,
context propagation, RAR exception unwrapping).

## Upstream Releases Since Last Sync

| Release | Tag | Date | Type |
|---------|-----|------|------|
| 0.5.4 | v0.5.4 | 2025-12-10 | Feature (multiple providers, .strm imports, UsenetSharp switch) |
| 0.5.38 | v0.5.38 | 2026-03-09 | Feature + compat fixes |
| **0.6.0** | **v0.6.0** | **2026-03-09** | **Breaking release** (blobstore, compression, non-reversible) |

Upstream has also moved from .NET 9.0 to **.NET 10.0**.

---

## Status Summary

| # | Item | Status | Branch |
|---|------|--------|--------|
| 1 | Zstd payload compression | DONE | `upstream/zstd-compression` (merged) |
| 2 | History-aware health checks | DONE (partial — see 2b) | `upstream/history-health-checks` (merged) |
| 2b | History-aware cleanup (schema + services) | DONE | `upstream/history-cleanup` (merged) |
| 3 | Duplicate NZB segment fallback | DONE | `upstream/duplicate-segment-fallback` (merged) |
| 4 | `/content` recovery after restart | DONE | `upstream/content-recovery` (merged) |
| 5 | Blobstore migration | SKIPPED (adopted HistoryItem compression instead) | `upstream/historyitem-compression` (merged) |
| 6a | SQLite VACUUM on startup | DONE | `upstream/quick-wins` (merged) |
| 6b | Archive passwords from NZB filenames | — | Already present in fork |
| 6c | Category-specific health checks | DONE | `upstream/quick-wins` (merged) |
| 6d | Export NZB from Dav Explore | SKIPPED (depends on blobstore) | — |
| 6e | User-agent configuration | SKIPPED (NNTP doesn't support it) | — |
| 7 | Compatibility fixes (rclone, AddUrl, Arr log) | DONE | `upstream/dotnet10-upgrade` (merged) |
| 8 | .NET 10.0 upgrade | DONE | `upstream/dotnet10-upgrade` |
| 9 | Bug fixes (Mar 1-10 commits) | DONE | `upstream/bug-fixes` |
| 10 | Rclone vfs/forget integration | DONE | `upstream/bug-fixes` |
| 11 | Frontend UI improvements | DONE (11a skipped — explore page already has FileDetailsModal) | `upstream/bug-fixes` |

---

## Implementation Plan

Items ordered by value and risk. Each section includes enough context to
implement without re-researching upstream.

---

### 1. Brotli/Zstandard Payload Compression — DONE

**Branch:** `upstream/zstd-compression` (merged to main)

Implemented Zstd compression via EF Core value converters. `CompressionUtil.cs`
with `ZSTD:` prefix + base64 encoding. Auto-detects legacy uncompressed text on read.

---

### 2. History-Aware Health Checks & Orphan Cleanup — PARTIAL

**Source:** v0.6.0 release notes
**Status:** Health check filtering done; full schema + services not yet adopted

#### 2a. Health Check Filtering — DONE

Our `HealthCheckService` filters by item type and excludes pending history items.
Category-specific filtering also implemented (item 6c).

#### 2b. Full History-Aware Cleanup (Schema + Services) — TODO

**Source:** Upstream commits `1409a75`, `bf6bb11`, `212653e`, `7e3dcab`, `12c5cec`
**Risk:** Medium — schema changes, new services, FK restructuring
**Effort:** Medium-large

**What upstream did (5 commits):**

1. **`HistoryItemId` FK on DavItems** (`1409a75`): Tracks which DavItems belong to
   which HistoryItem. New indices on `(Type, HistoryItemId, NextHealthCheck, ReleaseDate, Id)`
   and `(HistoryItemId)`. New EF migration.

2. **`HistoryCleanupItem` table + `HistoryCleanupService`** (`212653e`): After a
   HistoryItem is removed, a cleanup item is queued. The service either nulls out
   `HistoryItemId` (soft delete) or deletes DavItems (hard delete) based on
   `DeleteMountedFiles` flag.

3. **`DavCleanupItem` + `DavCleanupService`** (`12c5cec`): Replaced cascade delete
   with batched `ExecuteDeleteAsync` for faster directory deletion.

4. **History-aware orphan protection** (`7e3dcab`): `RemoveUnlinkedFilesTask` now
   filters by `HistoryItemId IS NULL` — items still in History are protected. Also
   fixed `using` → `await using` on DbContext instances (resource leak fix). Removed
   parent-child FK cascade on DavItems entirely.

5. **Simplified history removal** (`bf6bb11`): Replaced `ExecuteDeleteAsync` with
   entity-tracked removal.

**Dependencies:** Requires `DavItem.SubType` column from blobstore commit `e9f2464`
(can cherry-pick independently without adopting blobstore).

**Implementation notes:**
- New EF migration for `HistoryItemId`, `SubType` columns on DavItems
- New `HistoryCleanupItem` and `DavCleanupItem` tables
- New `HistoryCleanupService` and `DavCleanupService` background services
- Refactor `RemoveUnlinkedFilesTask` with history protection filter
- Fix `await using` on all DbContext instances
- Remove cascade delete FK on DavItems parent-child relationship

---

### 3. Duplicate NZB Segment Fallback — DONE

**Branch:** `upstream/duplicate-segment-fallback` (merged to main)

---

### 4. `/content` Recovery After Restart — DONE

**Branch:** `upstream/content-recovery` (merged to main)

---

### 5. Blobstore Migration — SKIPPED

**Decision:** Skipped the blobstore migration (high-risk, non-reversible). Instead
adopted HistoryItem NzbContents compression via Zstd value converter, which achieved
31% DB size reduction (856MB → 592MB after VACUUM).

**Branch:** `upstream/historyitem-compression` (merged to main)

**Upstream blobstore commits (10):** `e9f2464`, `eb9486c`, `fa9e637`, `d2cee00`,
`1bd18f1`, `678afde`, `114e570`, `b6c6258`, `345465f`, `5b2e949`, `26eabae`,
`82fb5f9`, `9cc788d`, `6b9fd43`, `6b43e82`

Note: `DavItem.SubType` from `e9f2464` is needed for item 2b (can cherry-pick
independently).

---

### 6. Quick Wins — PARTIAL

#### 6a. SQLite VACUUM on Startup — DONE

**Branch:** `upstream/quick-wins` (merged to main)

Added `api.startup-vacuum` setting + UI toggle in Settings → Repairs.

#### 6b. Archive Passwords from NZB Filenames — ALREADY PRESENT

Already in our fork.

#### 6c. Category-Specific Health Checks — DONE

**Branch:** `upstream/quick-wins` (merged to main)

Added `api.health-check-categories` setting + UI text input in Settings → Repairs.

#### 6d. Export NZB from Dav Explore — SKIPPED

Depends on blobstore. Could reimplemented reading from in-DB NzbContents if needed later.

#### 6e. User-Agent Configuration — SKIPPED

NNTP protocol doesn't support user-agent headers.

---

### 7. Compatibility Fixes — DONE

**Branch:** `upstream/dotnet10-upgrade` (Arr log fix committed there; rclone + AddUrl
merged via `upstream/quick-wins`)

Adopted:
- RcloneRcService improvements (batch operations, timeout handling, auth fix)
- AddUrl filename fallback from URL
- ArrMonitoringService: SocketException catch → Debug log level

---

### 8. .NET 10.0 Upgrade — DONE

**Branch:** `upstream/dotnet10-upgrade`

- `net9.0` → `net10.0` (main project, UsenetSharp, SharpCompress)
- EF Core + OpenApi: 9.x → 10.0.4
- Serilog.AspNetCore: 9.0.0 → 10.0.0
- Serilog.Sinks.Console: 6.0.1-dev → 6.1.1
- System.IO.Hashing: 9.0.0 → 10.0.0
- Dockerfile: `sdk:10.0` + `aspnet:10.0-alpine`

---

### 9. Bug Fixes (from upstream March 1-10 commits) — DONE

**Branch:** `upstream/bug-fixes`

**Risk:** Low — isolated fixes
**Effort:** Small

#### 9a. Duplicate NZB Files Processing Fix

**Source:** Upstream commit `053a596`

- Changed `GetHashToFileDescMap` from `Dictionary<string, FileDesc>` to
  `Dictionary<string, LinkedList<FileDesc>>` to handle NZBs where multiple files
  share the same 16KB hash
- Removed `.DistinctBy(x => x.FileName)` from `QueueItemProcessor`, allowing
  duplicate filenames
- Added WebSocket keepalive (30-second interval) and improved WebSocket error
  handling on shutdown

#### 9b. URL-Encoded Request Proxying (rclone v1.73.2 compat)

**Source:** Upstream commit `c37be6f`

- `decodeURIComponent(req.path)` in `server.ts` (compression filter) and
  `server/app.ts` (proxy routing)
- Required for rclone v1.73.2 which sends URL-encoded paths

#### 9c. Special Characters in Filename Passwords

**Source:** Upstream commit `df8b845`

- Changed `PasswordRegex` from `(?<pw>\w+)` to `(?<pw>.+)`
- Passwords with special characters would silently fail to match

#### 9d. Suppress TaskCanceledException on SIGTERM

**Source:** Upstream commit `b5c8a7d`

- Added `SigtermUtil.IsSigtermTriggered()` helper method
- Added `catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())`
  blocks to `HealthCheckService`, cleanup services
- WebSocket manager: sends proper close frame on shutdown instead of logging error
- Reduces log noise during clean shutdown

---

### 10. Rclone vfs/forget Integration — DONE

**Branch:** `upstream/bug-fixes`

**Source:** Upstream commits `3155158`, `28afc26`, `29189d3`, `e399877`, `af58795`,
`db73e96`, `0db4bb3`, `e81ef52`, `86c631f`
**Risk:** Low-medium — touches multiple services
**Effort:** Medium

**What upstream did:**

Upstream created a new static `RcloneClient` class. Our fork already has
`RcloneRcService` (DI-injected, with batch operations and timeout handling) which
is architecturally better. We should adopt the **call sites**, not the client class.

**New vfs/forget trigger points:**
- `DavDatabaseContext.SaveChangesAsync()` — whenever DavItems are added/removed,
  affected content dirs, id dirs, and completed-symlink dirs are forgotten
- `DavCleanupService` — after directory deletion
- `HistoryCleanupService` — after history cleanup
- `RemoveUnlinkedFilesTask` — after orphan removal
- `AddFileController` — after file addition
- `RemoveFromQueueController` — after queue removal
- `QueueItemProcessor` — for `/nzbs` folder

**Also includes:**
- `TestConnection` API endpoint (`api/test-rclone-connection`) — tests rclone RC
  connectivity with separate host/user/pass parameters
- Frontend: test-connection button in Rclone Server settings

**Implementation notes:**
- Keep our `RcloneRcService` architecture
- Wire `vfs/forget` calls into the services/controllers listed above
- Add `TestConnection` API endpoint using our existing service
- Add test button to frontend rclone settings

---

### 11. Frontend UI Improvements — DONE (11a skipped)

**Branch:** `upstream/bug-fixes`

**Source:** Upstream commits `db33830`, `0499cdc`, `983a213`, `39affab`, `3a97ee0`
**Risk:** Low — additive UI changes
**Effort:** Small-medium

#### 11a. Explore Page Actions Dropdown

**Source:** `db33830`

- Added dropdown options menu to items on the Explore page
- New `item-menu` and `dropdown-options` components
- Note: may reference `nzbBlobId` for NZB download — strip blobstore-specific UI

#### 11b. Download Support for `/view` Route

**Source:** `0499cdc`

- Added `?download=true` query parameter to `GetWebdavItemController`
- Switches `Content-Disposition` from `inline` to `attachment`

#### 11c. Content-Disposition Header Improvements

**Source:** `983a213`

- Proper `Content-Disposition` header with ASCII fallback and RFC 5987 UTF-8
  filename encoding
- Handles special characters and control characters in filenames

#### 11d. CSS Fixes

**Source:** `39affab`, `3a97ee0`

- Video/image icon `flex-shrink: 0` fix on Explore page
- `application/mp4` MIME type detection for video icon
- Rounded corners on simple-dropdown (`border-radius: 0 5px 5px 5px`)

---

### 12. Deferred / Evaluate Later

These were noted in the prior UPSTREAM_ANALYSIS.md as "evaluate for future":

| Feature | Source | Status |
|---------|--------|--------|
| PrioritizedSemaphore (connection fairness) | Upstream `7af47c6` | Our GlobalOperationLimiter works; revisit if contention issues arise |
| 7z progress tracking (MultiProgress) | Upstream `20b69b0` | Adopt if 7z streaming becomes a priority |
| UnbufferedMultiSegmentStream | Upstream | Potential fallback for low-memory; not needed currently |

---

## Reference: Upstream PRs and Commits

| ID | Title | Relevant To |
|----|-------|-------------|
| PR#199 | Database optimization (compression, retention, maintenance) | Items 1, 6a |
| PR#215 | Archive passwords from NZB filenames | Item 6b |
| PR#248 | Kodi scrubbing fixes | Item 7 |
| PR#265 | Save disabled providers without testing | Item 6 (minor) |
| PR#271 | Infuse WebDAV compatibility | Item 7 |
| PR#310 | Duplicate NZB segment fallback | Item 3 |
| PR#311 | `/content` recovery after restart | Item 4 |
| `053a596` | Duplicate NZB files processing fix | Item 9a |
| `c37be6f` | URL-encoded request proxying | Item 9b |
| `df8b845` | Special chars in filename passwords | Item 9c |
| `b5c8a7d` | Suppress TaskCanceledException on SIGTERM | Item 9d |
| `1409a75`–`12c5cec` | History-aware cleanup (5 commits) | Item 2b |
| `3155158`–`86c631f` | Rclone vfs/forget integration (9 commits) | Item 10 |
| `db33830`–`3a97ee0` | Frontend UI improvements (5 commits) | Item 11 |

## Reference: Key Upstream Files

```
backend/Database/BlobStore.cs                    — Blobstore implementation (SKIPPED)
backend/Utils/CompressionUtil.cs                 — Zstd compression helpers
backend/Utils/FilenameUtil.cs                    — Password extraction from filenames
backend/Services/HealthCheckService.cs           — History-aware filtering
backend/Tasks/RemoveUnlinkedFilesTask.cs         — Orphan cleanup with History filter
backend/Api/Controllers/DownloadNzb/             — NZB export endpoint (SKIPPED)
backend/Database/DavDatabaseContext.cs           — Value converters for compression
backend/Services/DatabaseMaintenanceService.cs   — Background compaction + retention
backend/Config/ConfigManager.cs                  — New settings (vacuum, user-agent, etc.)
backend/Clients/Rclone/RcloneClient.cs           — Upstream rclone client (we use RcloneRcService)
backend/Services/HistoryCleanupService.cs        — History cleanup with DavItem unlinking
backend/Services/DavCleanupService.cs            — Async directory cleanup
```
