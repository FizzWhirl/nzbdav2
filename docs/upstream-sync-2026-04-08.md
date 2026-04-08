# Upstream Sync — 2026-04-08

Comparison of upstream [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav) releases
v0.6.2 and v0.6.3 against our fork [dgherman/nzbdav2](https://github.com/dgherman/nzbdav2).

Previous sync documented in [`docs/upstream-sync-2026-03-10.md`](./upstream-sync-2026-03-10.md).

**Sync completed: 2026-04-08**
**Last upstream commit reviewed:** `75adf75` (v0.6.3 release, 2026-04-08)
**Previous sync cutoff:** v0.6.0 (2026-03-09)

---

## Upstream Releases Since Last Sync

| Release | Tag | Date | Type |
|---------|-----|------|------|
| 0.6.1 | v0.6.1 | 2026-03-11 | Dependency bumps (Dependabot) |
| 0.6.2 | v0.6.2 | 2026-03-24 | Auth fix, FLAC content-type |
| **0.6.3** | **v0.6.3** | **2026-04-08** | Features + fixes |

---

## Status Summary

| # | Item | Status | Upstream Commit |
|---|------|--------|-----------------|
| 1 | FLAC content-type + ContentTypeUtil | DONE | `5253fe3` |
| 2 | Frontend auth middleware | DONE | `eb71ebf` |
| 3 | WebDAV range request 416 fix | DONE | `a43d5d7` |
| 4 | Remove "Delete mounted files" for failed items | DONE | `dfbc411` |
| 5 | Organize /nzbs by category | DONE | `404d418` |
| 6 | QueueItems (Category, FileName) index | DONE | `9116bfc` |
| 7 | Table padding fix | DONE | `2e83dc7` |
| 8 | MimeType -> ContentType rename | DONE | `5253fe3` |
| 9 | Export NZB from history table | **SKIPPED** | `7928d4b` |
| 10 | Disabled action opacity | **SKIPPED** | `0b82f48` |
| 11 | NZB backup feature | **SKIPPED** | `c2b3692`, `55260d4` |
| 12 | Dependabot bumps | **SKIPPED** | various |
| 13 | CI/Docker publishing changes | **SKIPPED** | `e039ef6`, `4129e98`, `ba9b37e` |

---

## What Was Deliberately Skipped (and Why)

### 9. Export NZB from History Table

**Upstream commit:** `7928d4b`

**Decision:** Skipped. This commit introduces a "..." menu on history rows with Export NZB
and Remove options. Export NZB depends on `nzb_blob_id` (blobstore), which our fork doesn't
have. The commit also rewrites action-button CSS and introduces DropdownOptions in the
history table — significant UI changes that conflict with our simpler button layout.

**Our equivalent:** FileDetailsModal already provides NZB download from in-DB NzbContents,
plus health check, repair, analyze, and test download actions.

### 10. Disabled Action Opacity

**Upstream commit:** `0b82f48`

**Decision:** Skipped. Adds `opacity: 0.1` to a `.disabled` CSS class that exists only
in upstream's reworked action-button CSS (from commit `7928d4b`). Our fork uses React
Bootstrap `<Button disabled>` which has native disabled styling.

### 11. NZB Backup Feature

**Upstream commits:** `c2b3692` (backend), `55260d4` (frontend)

**Decision:** Skipped. Saves a copy of incoming NZBs to disk organized by category.
Upstream reads from BlobStore; our fork would need adaptation to read from request stream
or in-DB QueueNzbContents. Feature is redundant for us because we already have:
1. NZB content stored in-DB with Zstd compression
2. NZB fallback generation from stored segment metadata
These provide two recovery paths that upstream doesn't have.

---

## What Was Adopted (with Architectural Differences from Upstream)

### ContentTypeUtil (Item 1)

Adopted identically to upstream. New `ContentTypeUtil.cs` centralizes MIME type resolution
and adds FLAC mapping. Replaces per-class `FileExtensionContentTypeProvider` instances in
`GetWebdavItemController` and `BaseStoreItemPropertyManager`.

### /nzbs Category Organization (Item 5)

**Upstream approach:** `DatabaseStoreWatchFolder` extends `BaseStoreReadonlyCollection`
directly, lists categories via `configManager.GetApiCategories()` which returns a collection.

**Our approach:** Same structure, but `GetApiCategories()` returns a comma-separated string,
so we split it. Also, our `DatabaseStoreCategoryWatchFolder` reads NZB content as string
(matching our in-DB storage) rather than using BlobStore streams.

### Auth Middleware (Item 2)

Adopted identically to upstream. Auth checks moved from per-route loaders to Express
middleware in `app.ts`. Removed auth checks from `root.tsx`, `logout/route.tsx`,
and `settings.update/route.tsx`.

---

## Pickup Point for Next Sync

**Last upstream commit reviewed:** `75adf75` (v0.6.3, 2026-04-08)

**To start the next sync:**
1. Check commits after `75adf75` in the upstream repo
2. Filter out Dependabot/dependency bumps
3. Check this doc's "Deliberately Skipped" section before re-evaluating those areas
4. Check [`docs/upstream-sync-2026-03-10.md`](./upstream-sync-2026-03-10.md) for older
   architectural differences that still apply (blobstore, rclone, cleanup)
