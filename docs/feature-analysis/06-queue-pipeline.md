# Feature Report — Queue Pipeline (Step 0–6)

**File:** [backend/Queue/QueueItemProcessor.cs](../../backend/Queue/QueueItemProcessor.cs) (~1,100 LOC, +475 vs upstream)

**Backing design:** [docs/superpowers/plans/2026-04-13-queue-processing-speed-design.md](../../docs/superpowers/plans/2026-04-13-queue-processing-speed-design.md)

## Summary
Refactor of upstream's monolithic per-item processor into six discrete,
parallelisable steps with adaptive concurrency, batched DB writes, and
explicit failure routing.

## Step Layout

| Step | Purpose | Key Behaviour |
|---|---|---|
| 0 | Article existence pre-check against cached health-check data | Skip already-known-bad items early |
| 1a–d | Deobfuscation: first-segment fetches, par2 descriptors, file sizing | Parallel per-file with `MaxDownloadConnections + 5` |
| 2 | RAR / 7z / multipart decomposition | Streaming archive parsing |
| 3 | Smart article probe (~3 segments per file, DMCA detection) | 30 s per-file deadline, 180 s overall budget, retry-once |
| 4 | Aggregators + post-processors + STRM creation | Bulk DB updates after `ChangeTracker.Clear()` |
| 5 | ffprobe + decode validation; remove corrupt files | `Parallel.ForEachAsync` with adaptive throttle |
| 6 | Move surviving items to history, mark health-checked | Single transaction |

## Key Changes vs Upstream

### Discrete Step Processors (commit `1bdc60f`)
- Steps 3, 4, 5 extracted into independently testable units.
- Each step receives its own EF scope and fails locally without polluting
  upstream state.

### Parallel Step 4/5 (commit `8bc607e`)
- Files that pass Step 3 stream straight into Step 4/5 without waiting for
  the rest of the queue item to finish probing.
- ~1.5–2× throughput on multi-file items.

### Batched DB Updates (commit `100868f`)
- Replaces per-file `SaveChangesAsync` (which upstream did for every file)
  with one batch per step.
- Cuts SQLite write amplification dramatically.

### Adaptive Concurrency (commit `6984e7a`)
- `MaxConcurrentAnalyses` scales down under CPU/memory pressure.
- Avoids GC thrashing during heavy analysis bursts.

### Step 3 Smart Probe (this session: 15 s → 30 s per-file)
- Per-file `CancellationTokenSource.CreateLinkedTokenSource` with
  `CancelAfter(TimeSpan.FromSeconds(30))`.
- Retry-once on transient failures.
- Overall 180 s backstop in case the probe loop wedges.
- Uses lighter `QueueAnalysis` connection-usage type (different limits
  than user-facing streaming).

### Step 6 Ordering
- Queue is **not** marked complete until Step 5 has verified at least one
  importable media file.
- Eliminates upstream's race where Sonarr/Radarr could start importing a
  symlink before the file's health was confirmed.

### Explicit Failure Routing
- Three catch blocks: cancellation, retryable, fatal.
- `GetFailureReason()` maps exceptions to user-friendly strings (`Missing
  Articles`, `Password Protected`, `Connection Error`, etc.) surfaced via
  `HistoryItem.FailureReason`.

## Configuration

| Knob | Default | Effect |
|---|---|---|
| `usenet.max-download-connections` | varies | Step 1 + 2 concurrency (+5 hardcoded) |
| `usenet.max-concurrent-analyses` | 1 | Step 3 + 5 parallelism (adaptive) |
| `api.ensure-article-existence` | true | Gates Step 3 + 5 |
| `api.ensure-importable-media` | true | Step 4d validation |
| Step 3 per-file timeout | 30 s (hardcoded) | This session's bump from 15 s |
| Step 3 overall timeout | 180 s (hardcoded) | Backstop |

## Possible Issues / Edge Cases

| # | Issue | Severity |
|---|---|---|
| 1 | Step 3 hardcoded timeouts (30 s + 180 s) — distant providers under load may still spuriously time out. | Medium |
| 2 | EF change tracker not explicitly cleared between Step 1 sub-steps; could grow if a single NZB has thousands of files. Step 4 clears it before aggregation, mitigating most cases. | Low |
| 3 | `ConcurrentBag<string>` for probe results — unbounded; pathological NZB with 100k+ files could grow large. | Low |
| 4 | Magic number `+5` in `MaxDownloadConnections + 5` — undocumented rationale, not configurable. | Cosmetic |
| 5 | Step 5 has both `Parallel.ForEachAsync` and an inner `SemaphoreSlim(10,10)` — the semaphore is redundant given the parallel option. (Flagged in `origin-main-delta-analysis-2026-04-21.md` finding #9.) | Cosmetic |
| 6 | `IsCorrupted` write immediately followed by `ExecuteDeleteAsync` — wasted DB ops (finding #10 in same review). | Cosmetic |
| 7 | DMCA-and-probe-failed file: only the DMCA path runs, probe path is a no-op for that file. Inconsistent counters but harmless. | Cosmetic |
| 8 | `Parallel.ForEachAsync` aborts remaining work on uncaught exceptions — current catch filters cover the expected cases but not future-proof. | Low |
| 9 | Production debug marker `"!!! DEBUG: QueueItemProcessor STARTING ..."` (line 43) was flagged in earlier review — verify it has been removed. | High (cleanup) |

## Graceful Degradation
- Step 3 100% DMCA → explicit failure with reason "No files passed probe".
- Step 3 partial fail → trigger Step 5 (ffprobe) for definitive answer.
- Step 3 overall timeout → log warning, continue to Step 5.
- Step 5 timeout / provider error → keep file with "Pending" status (not
  removed, will be revisited by health check).
- Step 5 hard fail → file removed with logged reason.
- All media fail Step 5 → explicit failure ("All files failed analysis").

## Recommended Improvements
1. Promote 15s → 30 s and the 180 s backstop to config knobs (e.g.
   `queue.probe.per-file-timeout-seconds`, `queue.probe.overall-timeout-seconds`).
2. Remove the inner `SemaphoreSlim(10,10)` in Step 5; rely on
   `MaxDegreeOfParallelism`.
3. Skip the `IsCorrupted` write when the next op deletes the row.
4. Replace the debug marker on line 43 with `Log.Debug`.
5. Document the `+5` connection buffer or make it `usenet.queue.cpu-buffer`.
6. Add a Prometheus counter for "files probed", "files DMCA", "files
   transient-failed", "files corrupt".
