# Upstream Sync Report — 2026-05-27

## Summary
- **Sync date:** 2026-05-28
- **Previous sync:** commit `6482ca1` (v0.6.3, 2026-04-08)
- **New upstream HEAD:** `dca490e6` (v0.8.0, 2026-05-25)
- **Upstream remote:** https://github.com/dgherman/nzbdav2
- **Total upstream commits since last sync:** 50 (47 non-merge + 3 merge)
- **Already cherry-picked:** 28 commits (v0.6.23, v0.7.0, v0.7.1 partial)
- **True delta:** ~22 commits from v0.7.2+ and v0.8.0
- **Files integrated:** 25 (+1,325 / −127 lines)

## Version Span

v0.6.23 → v0.7.0 → v0.7.1 → v0.7.2 → v0.8.0

## Category Breakdown
- 22 features (44%)
- 15 bug fixes (30%)
- 9 docs (18%)
- 3 merges (6%)
- 1 CI (2%)
- 1 refactor (2%)
- No breaking changes, security patches, or dependency updates

## New Files Added (4)

| File | Purpose |
|------|---------|
| [`backend/Database/LegacyRarFileMigration.cs`](backend/Database/LegacyRarFileMigration.cs) | Migrates pre-v0.8.0 `DavRarFile` rows to `DavMultipartFile` |
| [`backend/Streams/SegmentOffsetTable.cs`](backend/Streams/SegmentOffsetTable.cs) | Builds O(log N) segment offset lookup tables |
| [`backend/WebDav/SegmentSizePopulation.cs`](backend/WebDav/SegmentSizePopulation.cs) | Validates and manages lazy segment-size population |
| [`docs/superpowers/plans/2026-05-25-rar-segment-size-and-legacy-cleanup.md`](docs/superpowers/plans/2026-05-25-rar-segment-size-and-legacy-cleanup.md) | Design doc for segment-size feature |

Files already present in fork from previous cherry-picks (skipped):
- `ArticleCachingNntpClient.cs`, `PrioritizedSemaphore.cs` (+2 siblings)
- `SharedStreamEntry.cs`, `SharedStreamHandle.cs`, `SharedStreamManager.cs`
- All plans/specs docs and test files

## Clean Merges (11 files, no fork modifications)

| File | Upstream Changes |
|------|-----------------|
| [`backend/Api/Controllers/DownloadNzb/DownloadNzbController.cs`](backend/Api/Controllers/DownloadNzb/DownloadNzbController.cs) | Minor updates |
| [`backend/Api/Controllers/ListWebdavDirectory/ListWebdavDirectoryController.cs`](backend/Api/Controllers/ListWebdavDirectory/ListWebdavDirectoryController.cs) | Minor cleanup |
| [`backend/Api/Controllers/Maintenance/RepairClassificationController.cs`](backend/Api/Controllers/Maintenance/RepairClassificationController.cs) | Minor updates |
| [`backend/Api/Controllers/ProviderBenchmark/ProviderBenchmarkController.cs`](backend/Api/Controllers/ProviderBenchmark/ProviderBenchmarkController.cs) | Minor updates |
| [`backend/Api/Controllers/SearchWebdav/SearchWebdavController.cs`](backend/Api/Controllers/SearchWebdav/SearchWebdavController.cs) | Minor cleanup |
| [`backend/Api/Controllers/TestDownload/TestDownloadController.cs`](backend/Api/Controllers/TestDownload/TestDownloadController.cs) | Minor updates |
| [`backend/Api/SabControllers/AddUrl/AddUrlRequest.cs`](backend/Api/SabControllers/AddUrl/AddUrlRequest.cs) | User-agent support |
| [`backend/Database/Models/DavItem.cs`](backend/Database/Models/DavItem.cs) | Minor model changes |
| [`backend/Queue/PostProcessors/BlacklistedExtensionPostProcessor.cs`](backend/Queue/PostProcessors/BlacklistedExtensionPostProcessor.cs) | Cleanup |
| [`backend/WebDav/DatabaseStoreIdFile.cs`](backend/WebDav/DatabaseStoreIdFile.cs) | Minor cleanup |
| [`frontend/app/routes/settings/sabnzbd/sabnzbd.tsx`](frontend/app/routes/settings/sabnzbd/sabnzbd.tsx) | User-agent UI field |

## Hybridized Files (8 files, manual merge)

| File | Upstream Change | Our Fork Change | Resolution |
|------|----------------|-----------------|------------|
| [`DavMultipartFile.cs`](backend/Database/Models/DavMultipartFile.cs) | Upgraded `SegmentSizes` doc comment | Already had `SegmentSizes` property | Adopted upstream's detailed doc comment |
| [`DavDatabaseContext.cs`](backend/Database/DavDatabaseContext.cs) | Switched to explicit constructors + test constructor | Primary constructor with fork-specific config | Adopted explicit constructors, preserved all fork members (VFS, config, cascade FK). Skipped LEGACY comment on `DavRarFile` DbSet |
| [`DavRarFile.cs`](backend/Database/Models/DavRarFile.cs) | Added LEGACY deprecation comment | We still use `DavRarFile`/`DatabaseStoreRarFile` | Added LEGACY comment for documentation; NOT removing RarFiles |
| [`RarProcessor.cs`](backend/Queue/FileProcessors/RarProcessor.cs) | Added `partSegmentSizes` precomputation; simplified connection handling | Custom `MaxGlobalRarHeaderConnections` (6), `RarHeaderConnectionSlots` semaphore | Added precomputation gated by `SegmentSizes == null`. Preserved our global connection cap and abort-on-timeout logic |
| [`SevenZipProcessor.cs`](backend/Queue/FileProcessors/SevenZipProcessor.cs) | Added `ComputePartSegmentSizes` method + call; updated `GetDavMultipartFileMeta` | Already had `SegmentSizes` propagation | Adopted full `ComputePartSegmentSizes`; updated to use precomputed sizes (more reliable than propagation) |
| [`DatabaseStoreMultipartFile.cs`](backend/WebDav/DatabaseStoreMultipartFile.cs) | Added `SegmentSizePopulation.NeedsPopulation` lazy compute + persist block | Custom affinity key, analysis mode, preview mode handling | Inserted `SegmentSizePopulation` block between null check and preview mode detection |
| [`ConfigManager.cs`](backend/Config/ConfigManager.cs) | Added `GetUserAgent()` + `DefaultUserAgent` constant | Already has streaming priority, download connections, shared stream config | Added `GetUserAgent()` and `DefaultUserAgent` after `GetManualUploadCategory()` |
| [`settings-config.ts`](frontend/app/routes/settings/settings-config.ts) | Added `api.user-agent` key; defaults: `max-concurrent-buffered-streams` 2→8, `stream-buffer-size` 100→20 | Our fork had `stream-buffer-size` at 100 | Applied all three upstream changes |

## Key Architectural Changes Integrated

- **SegmentOffsetTable** — O(log N) binary search for segment-to-byte offset mapping, replacing linear scan
- **SegmentSizePopulation** — Lazy population of individual segment sizes from NZB metadata, persisted to DB
- **SharedStreamManager** — (previously cherry-picked) reduces redundant segment downloads across concurrent requests
- **PrioritizedSemaphore** — (previously cherry-picked) priority-based semaphore for streaming vs. background work
- **ArticleCachingNntpClient** — (previously cherry-picked) caches article responses to reduce duplicate NNTP fetches
- **RAR/7z multipart fixes** — Segment size precomputation for more reliable part boundary detection
- **User-Agent config** — Configurable NZB download user-agent via settings UI and API

## Deliberately Skipped

### RarFile Removal Chain (5 files)
Upstream is consolidating RAR handling into `DavMultipartFile`. Our fork needs RarFile support for Zstd in-DB compression:

| File | Skipped Change | Reason |
|------|---------------|--------|
| [`DavDatabaseClient.cs`](backend/Database/DavDatabaseClient.cs) | Remove `RarFile` from type filter | We still have RarFiles |
| [`DatabaseStoreCollection.cs`](backend/WebDav/DatabaseStoreCollection.cs) | Remove `RarFile` case from resolver | We still need `DatabaseStoreRarFile` |
| [`DatabaseStoreSymlinkCollection.cs`](backend/WebDav/DatabaseStoreSymlinkCollection.cs) | Remove `RarFile` case from symlinks | We still need RarFile symlinks |
| [`GetFileDetailsController.cs`](backend/Api/Controllers/GetFileDetails/GetFileDetailsController.cs) | Remove RarFile check | We still check RarFile first |
| [`HealthCheckService.cs`](backend/Services/HealthCheckService.cs) | Remove RarFile from health checks | We still have RarFiles to check |

### Fork-Divergent Files
- [`Program.cs`](backend/Program.cs) — 123 fork commits, too divergent
- [`README.md`](README.md) — 138 fork commits, fork-specific
- [`Dockerfile`](Dockerfile) — 7 fork commits
- [`.github/workflows/docker-publish.yml`](.github/workflows/docker-publish.yml) — fork-specific CI
- [`.gitignore`](.gitignore) — 4 fork commits

### Test Files
- All `backend.Tests/*` — not needed for our fork

## Fork-Protected Features (Verified Untouched)

| Feature | Files Protected | Reason |
|---------|----------------|--------|
| **Zstd in-DB compression** | [`DavDatabaseContext.cs`](backend/Database/DavDatabaseContext.cs), `CompressionUtil` | Upstream uses blobstore; we compress in DB |
| **DI-injected RcloneRcService** | [`RcloneRcService.cs`](backend/Services/RcloneRcService.cs) | Upstream uses static client |
| **Cascade FK** | [`DavDatabaseContext.cs`](backend/Database/DavDatabaseContext.cs) OnModelCreating | Upstream may not have cascade deletes |
| **DatabaseStoreRarFile.cs** | [`DatabaseStoreRarFile.cs`](backend/WebDav/DatabaseStoreRarFile.cs) | Upstream removed it; we still need it |
| **GlobalOperationLimiter** | [`GlobalOperationLimiter.cs`](backend/Clients/Usenet/Connections/GlobalOperationLimiter.cs) | Uses PrioritizedSemaphore (already adopted) |
| **ConnectionPool circuit breaker** | [`ConnectionPool.cs`](backend/Clients/Usenet/Connections/ConnectionPool.cs) | Custom resilience logic |

## Conflict Risk Assessment (Post-Integration)
- All conflicts resolved. No 🔴 HIGH risk items remaining.
- `DatabaseStoreRarFile.cs` intentionally kept (upstream deleted it)
- `GlobalOperationLimiter` kept (upstream uses PrioritizedSemaphore — our fork already had both)

## Next Sync Preparation
- Monitor upstream for further RarFile → MultipartFile migration steps
- Watch for any blobstore-related changes (we'll need to skip or adapt)
- Upstream may continue removing RarFile references — verify each against our Zstd storage model
- `DavDatabaseClient.cs` line 21: If we ever stop creating new RarFiles, update the type filter
- `Program.cs` at 123 fork commits should never be blindly merged; cherry-pick individual features

---

*Sync executed 2026-05-28. New sync point: `dca490e6` (v0.8.0). Previous: `6482ca1` (2026-04-08).*