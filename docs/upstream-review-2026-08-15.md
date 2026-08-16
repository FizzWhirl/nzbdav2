# Upstream Review — v0.11.12 → v0.12.2

**Date:** 2026-08-15
**Author:** FizzWhirl fork maintainer (from completed read-only investigation)
**Previous sync:** [`cd32db3c`](https://github.com/dgherman/nzbdav2/commit/cd32db3c) (v0.11.12, 2026-07-21)
**Target upstream:** [`d17c8acf`](https://github.com/dgherman/nzbdav2/commit/d17c8acf) (v0.12.2)
**Fork HEAD:** `d20ab9fd` (local `main`)

---

## 1. Scope and References

| Item | Value |
|---|---|
| Upstream remote | `upstream` → https://github.com/dgherman/nzbdav2.git (default branch `main`) |
| Fork remote | `origin` → https://github.com/FizzWhirl/nzbdav2.git (branch `main`) |
| Fork local `main` | `d20ab9fd` |
| Last reviewed/synced upstream point | `cd32db3c` (tag `v0.11.12`), per [docs/upstream-review-2026-07-21.md](docs/upstream-review-2026-07-21.md) |
| New upstream HEAD after fetch | `d17c8acf` (tagged `v0.12.2`) |
| New upstream tags | `v0.12.0`, `v0.12.2` |
| Range reviewed | `cd32db3c..d17c8acf` |
| Commits / files / diffstat | 7 commits, 43 files changed, +2,674 / −217 |
| Commit authorship | All 7 commits by upstream maintainer Dumitru Gherman-Lad |
| Working tree impact | None — `git fetch` only updated remote-tracking refs and pruned deleted upstream branches |

---

## 2. Executive Summary

Upstream moved from `v0.11.12` to `v0.12.2` across 7 commits (43 files, +2,674 / −217). The delta is small but technically significant: two streaming memory-budget fixes (`v0.12.0`) that correct a fork-present prefetch defect and introduce heap-derived memory sizing, and two queue fixes (`v0.12.1`–`v0.12.2` era) that correct RAR volume grouping and remove a global RAR-header concurrency cap. Two commits are docs-only and one is a merge commit.

| Decision | Commits | Notes |
|---|---|---|
| **ADAPT** | 3 | `4ebe10ff`, `6c028580`, `880c1bc8` — real fixes, but touch fork-divergent files |
| **ADAPT / INVESTIGATE** | 1 | `52c6c7cb` — removes the deliberately preserved `MaxGlobalRarHeaderConnections = 6` cap |
| **SKIP** | 2 | `e3259e57`, `d17c8acf` — docs only; fork keeps its own docs/versioning |
| **n/a** | 1 | `5095199a` — merge commit, no direct file changes |

No security or breaking schema changes were found, and there is no continuation of the blobstore/RarFile-removal migration that would threaten the fork's Zstd in-DB storage. The three technically valuable changes are all ADAPT-level because they land in files the fork has also modified.

---

## 3. Known Deliberate Fork Divergences (Reconfirmed)

1. **Zstd in-DB NZB/RAR storage** (vs upstream filesystem blobstore) — [`QueueNzbContents.cs`](backend/Database/Models/QueueNzbContents.cs), [`DatabaseStoreRarFile.cs`](backend/WebDav/DatabaseStoreRarFile.cs).
2. **DI-injected `RcloneRcService` singleton** (vs upstream static `RcloneClient`).
3. **Cascade FK delete on `DavItems`** in [`DavDatabaseContext.cs`](backend/Database/DavDatabaseContext.cs).
4. **Custom [`ConnectionPool.cs`](backend/Clients/Usenet/Connections/ConnectionPool.cs) circuit breaker + reserve mechanism.**
5. **Custom parallel queue pipeline** in `QueueItemProcessor.cs` with adaptive concurrency.
6. **Fork GC/env tuning** in [`Dockerfile`](Dockerfile): `DOTNET_GCServer=1`, `DOTNET_GCConcurrent=1`, `DOTNET_GCHeapHardLimit=0x80000000` (2 GB), vs upstream `DOTNET_GCServer=0` + `0x20000000` (512 MB).
7. **Preview / HLS / ffmpeg features** absent upstream.

---

## 4. New Commits (cd32db3c..d17c8acf)

| # | Commit | Date | Summary |
|---|---|---|---|
| 1 | `4ebe10ff` | 2026-07-28 | fix(streaming): derive memory sizing from the heap ceiling, filter samples |
| 2 | `6c028580` | 2026-07-28 | fix(streaming): bound the prefetch window by the byte budget on large segments |
| 3 | `e3259e57` | 2026-08-03 | docs: set the v0.12.0 changelog date to the release date |
| 4 | `5095199a` | 2026-08-03 | Merge fix/memory-budget-and-sample-filter (v0.12.0) |
| 5 | `880c1bc8` | 2026-08-04 | fix(queue): group RAR volumes by magic and verify volume coverage |
| 6 | `52c6c7cb` | 2026-08-05 | perf(queue): read RAR headers unbuffered at derived concurrency |
| 7 | `d17c8acf` | 2026-08-05 | docs: name the sibling checkouts and their roles (tag v0.12.2) |

### 4.1 `4ebe10ff` — fix(streaming): derive memory sizing from the heap ceiling, filter samples

- **35 files.**
- Adds `MemoryBudget.cs`: heap-ceiling-derived concurrent-stream count, per-stream prefetch budget, pool retention cap.
- Adds `FileFilterUtil.cs`: sample detection + glob matching.
- Adds sample-video filtering during queue post-processing.
- Adds multi-file NZB dropzone fix.
- Adds OOM diagnostics (`LogOomHeapState`, `RUNAWAY SEGMENT READ` warnings).
- Adds a `docker/repro` harness.

### 4.2 `6c028580` — fix(streaming): bound the prefetch window by the byte budget on large segments

- **4 files.**
- Fixes a segments-vs-bytes defect in `BufferedSegmentStream.ComputePrefetchWindow()`: the window was a segment count (`bufferSegmentCount + connections`), so 4 MB-segment releases held ~360 MB against a 96 MB budget.
- Now the window is the byte budget converted to segments, raised only to a parallelism floor (one segment per connection).

### 4.3 `e3259e57` — docs: set the v0.12.0 changelog date to the release date

- **1 file** (README date only).

### 4.4 `5095199a` — Merge fix/memory-budget-and-sample-filter (v0.12.0)

- Merge commit; no direct file changes.

### 4.5 `880c1bc8` — fix(queue): group RAR volumes by magic and verify volume coverage

- **11 files.**
- Fixes (a) RAR volumes grouped by `GetMultipartBaseName` splitting a 71-volume archive into 71 one-volume archives due to random per-volume subjects; (b) `RarAggregator` mounting whatever volumes parsed with no coverage check, publishing silently-short files.
- Adds `ValidateVolumeCoverage` and `StoredFileSegment.FileUncompressedSize` / `IsUncompressedSizeUnknown`.

### 4.6 `52c6c7cb` — perf(queue): read RAR headers unbuffered at derived concurrency

- **8 files.**
- Removes the static global RAR-header semaphore (`MaxGlobalRarHeaderConnections = 6`) and buffered header reads.
- Header parsing now unbuffered (~1 article per volume) at a heap-derived concurrency budget (`MemoryBudget.MaxConcurrentRarHeaderParts`), cutting a 71-volume import from ~240 s.

### 4.7 `d17c8acf` — docs: name the sibling checkouts and their roles

- **1 file** (`CLAUDE.md` documentation only). Tag `v0.12.2`.

---

## 5. Cross-Reference / Conflict Highlights

### Group A — Streaming memory budget (v0.12.0: `4ebe10ff` + `6c028580`)

| File | Conflict | Notes |
|---|---|---|
| `MemoryBudget.cs` | None (net-new) | Adopt as-is |
| `FileFilterUtil.cs` | None (net-new) | Adopt as-is |
| [`BufferedSegmentStream.cs`](backend/Streams/BufferedSegmentStream.cs) | **HIGH** | Fork still has old 3-arg `ComputePrefetchWindow` + 256 MB `MinPrefetchWindowBytes` floor and its own OOM guards; lacks `SetPrefetchBudgetBytes` |
| [`SegmentBufferPool.cs`](backend/Streams/SegmentBufferPool.cs) | **LOW–MED** | Fork has fixed 512 MB `DefaultMaxIdleBytes` |
| [`ConfigManager.cs`](backend/Config/ConfigManager.cs) | **MEDIUM** | Fork returns `"8"` default; custom config keys |
| [`Program.cs`](backend/Program.cs) | **HIGH** | Fork's most-divergent file (~1,643 lines); no `MemoryBudget` wiring — manual port only |
| [`ConnectionPool.cs`](backend/Clients/Usenet/Connections/ConnectionPool.cs) | **LOW** | Single log-line change |
| `BlacklistedExtensionPostProcessor.cs` | **MEDIUM** | Fork lacks sample filter |
| [`DatabaseStoreCollection.cs`](backend/WebDav/DatabaseStoreCollection.cs) / [`DatabaseStoreSymlinkCollection.cs`](backend/WebDav/DatabaseStoreSymlinkCollection.cs) | **MEDIUM** | Port sample check; preserve fork RarFile handling |
| [`Dockerfile`](Dockerfile) | **SKIP** | Comment only — fork GC env differs |
| Frontend queue/settings files | **MEDIUM** | Fork customized |
| `docker/repro` + upstream `MockUsenetServer` | **SKIP / INVESTIGATE** | Fork has its own `MockNntpServer.cs` |
| Tests | **LOW (adopt)** | 6 new absent, 3 modified present |

### Group B — RAR volume coverage + concurrency (`880c1bc8` + `52c6c7cb`)

| File | Conflict | Notes |
|---|---|---|
| Queue ingest path (`RarProcessor` → `RarAggregator`) | Portable | Mirrors upstream; does not touch Zstd in-DB storage. Fork's RarFile divergence is in the WebDAV serving layer ([`DatabaseStoreRarFile.cs`](backend/WebDav/DatabaseStoreRarFile.cs)), which these commits do not touch |
| `QueueItemProcessor.cs` | **MEDIUM** | Fork has old name-based grouping + old `connectionsPerRar`; must preserve parallel-step/adaptive-concurrency architecture |
| [`RarProcessor.cs`](backend/Queue/FileProcessors/RarProcessor.cs) | **HIGH** | Fork still has `MaxGlobalRarHeaderConnections = 6`, `MaxRarHeaderConnectionsPerPart = 2`, `SemaphoreSlim RarHeaderConnectionSlots`, buffered reads, abort-on-timeout `headerCts`; `StoredFileSegment` lacks new fields — the static global cap was a deliberately preserved fork customization per the 2026-05-27 sync |
| `RarAggregator.cs` | **LOW–MED** | Fork lacks `ValidateVolumeCoverage`, otherwise near-identical |
| `RarUtil.cs` | **LOW** | `GetRarHeaders` private → internal test seam |
| `MemoryBudget.MaxConcurrentRarHeaderParts` | Dependency | Depends on Group A |
| `NzbFromDbTester.cs` / `FullNzbTester.cs` | **LOW** | Tooling alignment |

---

## 6. Adoption Recommendations

| Commit | Recommendation | Rationale |
|---|---|---|
| `4ebe10ff` heap-derived memory sizing + sample filter | **ADAPT** | Net-new utils are low-risk; fixes real production OOM. Fork's 2 GB heap reduces exposure but fixed defaults are still the same wrong constants. Manually wire into [`Program.cs`](backend/Program.cs), [`BufferedSegmentStream.cs`](backend/Streams/BufferedSegmentStream.cs), [`ConfigManager.cs`](backend/Config/ConfigManager.cs), [`SegmentBufferPool.cs`](backend/Streams/SegmentBufferPool.cs) while preserving fork OOM guards. Sample filter is behavior-gated; decide independently whether to default it on |
| `6c028580` prefetch byte-budget fix | **ADAPT** (high value) | Corrects a fork-present defect; requires `PrefetchBudgetBytes` plumbing from `4ebe10ff`. Keep fork's range-bounded prefetch and OOM guards |
| `880c1bc8` RAR group-by-magic + coverage validation | **ADAPT** (high value) | Genuine correctness fixes (silently-short mounts, wrong volume grouping) missing in fork; port into `QueueItemProcessor.GetFileProcessors()` and `RarAggregator`, preserving fork adaptive-concurrency model; add the two `StoredFileSegment` fields in [`RarProcessor.cs`](backend/Queue/FileProcessors/RarProcessor.cs) |
| `52c6c7cb` unbuffered RAR headers + derived concurrency | **ADAPT / INVESTIGATE** (careful) | Clear measured win and compatible with fork's larger heap, but removes the deliberately preserved static `MaxGlobalRarHeaderConnections = 6` cap and depends on `MemoryBudget`. Port unbuffered-read + derived-budget approach, preserve fork abort-on-timeout logic, re-validate against fork's custom connection-pool accounting before shipping |
| `e3259e57` docs | **SKIP** | Fork keeps its own docs/versioning |
| `5095199a` | **n/a** | Merge commit |
| `d17c8acf` docs | **SKIP** | Fork keeps its own docs/versioning |

---

## 7. Bottom Line & Recommended Adoption Order

- 7 new commits; new tags `v0.12.0` / `v0.12.2`.
- No security or breaking schema changes; no blobstore/RarFile-removal continuation threatening Zstd in-DB storage.
- The three technically valuable changes are all **ADAPT**-level due to fork-divergent files.
- `52c6c7cb` is the only item requiring a deliberate **INVESTIGATE** decision.

**Recommended adoption order:**

1. `MemoryBudget` foundation (`4ebe10ff`).
2. `6c028580` prefetch fix.
3. `880c1bc8` RAR fixes.
4. Evaluate `52c6c7cb` separately.

**Standing rule:** follow the manual-port pattern for [`Program.cs`](backend/Program.cs) and never auto-merge it.
