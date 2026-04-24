# Feature Report — V1 → V2 Migration

**Files:**
- [backend/Program.cs](../../backend/Program.cs) (~1,250 LOC added; migration orchestration around line 880–1400)
- [backend/Database/BlobStoreCompat/BlobStoreReader.cs](../../backend/Database/BlobStoreCompat/BlobStoreReader.cs) (~210 LOC, new)
- [backend/Database/BlobStoreCompat/UpstreamBlobModels.cs](../../backend/Database/BlobStoreCompat/UpstreamBlobModels.cs) (~80 LOC, new)

## Summary
Survives drifted v1 databases that vanilla v0.6.3 refuses to start on.
Reads upstream's Zstd + MemoryPack-`VersionTolerant` blobs, reconstructs
the v2 row schema (DavNzbFile / DavRarFile / DavMultipartFile), self-heals
five distinct schema-drift conditions, and degrades gracefully (orphans
get marked `IsCorrupted` rather than blocking startup).

## Five Self-Healing Layers

| # | Layer | Trigger | Code |
|---|---|---|---|
| 1 | **Index drift recovery** — recover `DavItem.Id` from blob if `FileBlobId` column was dropped | DavItems table exists but no FileBlobId | `BlobStoreReader.TryReadEmbeddedIdAsync()` |
| 2 | **SubType nullability** — drop+recreate DavItems with nullable SubType | Detect NOT NULL SubType from v1 | `EnsureSchemaCompatibilityAsync()` (Program.cs:369–422) |
| 3 | **AddHistoryCleanup migration compat** — pre-populate `__EFMigrationsHistory` if missing | EF migration table missing | `EnsureAddHistoryCleanupMigrationCompatibilityAsync()` (Program.cs:461–481) |
| 4 | **Foreign-key violation cleanup** — cascade-delete orphans before EF migration runs | Pre-migration sanity check | `EnsureDavItemsHistoryItemIdForeignKeyCompatibilityAsync()` (Program.cs:540–562) |
| 5 | **Force-promoted recovery** — re-deserialize items where prior v2 builds left wrong/missing metadata | On-demand during migration | `MigrateDavItemsFromBlobstoreAsync()` (Program.cs:882–1377) |

## MemoryPack Compatibility
- Upstream uses `[MemoryPackable(GenerateType.VersionTolerant)]` for blob
  types (commit `81f2978` on this branch). The fork's shim POCOs in
  `UpstreamBlobModels.cs` mirror this byte-for-byte, including
  `UpstreamLongRange` declared as `partial record` (NOT struct), because
  `record` and `struct` produce different on-the-wire layouts under
  `VersionTolerant`.
- Without this exact fidelity, deserialization either throws or produces
  garbage — the reason the migration was initially failing.

## Memory Discipline (commit `dbac627`)
- `BlobStoreReader.TryReadAsync<T>` uses `MemoryStream.GetBuffer()` +
  length slice instead of `ToArray()` — eliminates a transient byte[]
  copy in the read path.
- Pre-sizes the `MemoryStream` to `min(fileLen × 4, 8 MB)` to avoid
  allocator churn.
- Migration loop calls `databaseContext.ChangeTracker.Clear()` after each
  100-item batch to stop EF from accumulating tracked entities.
- Calls `GC.Collect(2, GCCollectionMode.Optimized, blocking: false,
  compacting: true)` after each batch to compact the LOH (large blobs ≥
  85 KB are LOH-allocated and don't auto-compact).
- Batch size lowered from 500 → 100 for headroom on slow disks.

## Possible Issues / Edge Cases

| # | Issue | Severity |
|---|---|---|
| 1 | V1 backup file required at `/config/v1-backup.sqlite` or `/config/backup/db.sqlite`; if missing, items silently orphan. Should prompt at startup. | High |
| 2 | Bulk delete inside the migration loop has no transaction boundaries — partial deletion possible on crash. | Medium |
| 3 | First 10 deserialization failures log at Warning with hex dump; subsequent failures at Debug. If 1000 fail, you see 10 — pattern detection is hard. Should add a final summary count. | Medium |
| 4 | Index-drift recovery does NOT try to read FileBlobId from current DB; relies on backup. If user has drifted current DB but no backup, they're stuck. | Medium |
| 5 | No checksum/version header in blobs — if upstream ever changes its layout (e.g. v0.7), deserialization could silently produce garbage. | Low (upstream churn risk) |
| 6 | Force-promoted SubType fallback uses Type as proxy (201→3, 202→4, 203→6); if a prior bug recorded the wrong Type, we pick the wrong deserializer. | Low |
| 7 | Migration is single-threaded — 100 k items × 100 ms per item ≈ 3 hours. No parallelisation. | Medium |
| 8 | Five-layer heal logic lives in one `Program.cs` and is non-trivial to unit-test. | Tech debt |

## Failure Visibility
- Item-level: `IsCorrupted = 1` + `CorruptionReason` string, surfaced in
  the health page UI.
- Migration-level: warnings in startup log; first 10 verbose, rest debug.

## Code Quality
- Defensive: every layer has a "skip if condition not met" early-return.
- Idempotent: rerunning migration on an already-migrated DB is safe.
- Progress logging is throttled (every batch) — not noisy.
- Uses `ExecuteUpdateAsync` / `ExecuteDeleteAsync` to bypass change
  tracker for bulk ops (good).

## Recommended Improvements
1. **Interactive backup prompt at startup** when v1 schema detected and no
   backup found.
2. **Wrap each batch in an explicit `BeginTransactionAsync`** so partial
   batches roll back cleanly.
3. **Final summary log line**: `"Migration complete: 23,491 ok, 1,742
   corrupt, 84 orphaned"`.
4. **Add a checksum / format-version header** to blobs going forward so
   future divergence is detectable.
5. **Parallelise migration** (e.g. 4-way `Parallel.ForEachAsync` on
   batches) with per-thread `DbContext`.
6. **Extract `MigrateDavItemsFromBlobstoreAsync` into a dedicated
   `Migration/` namespace** so it is unit-testable.
