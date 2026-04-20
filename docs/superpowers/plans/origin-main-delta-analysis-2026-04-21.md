# Origin/Main Delta Analysis (2026-04-21)

## Scope and Method

Comparison target: `origin/main...HEAD` on branch `feature/media-analysis-optimization`.

Observed branch state at review time:
- Ahead/behind: `10 ahead`, `13 behind`
- Commits reviewed: `f3d90c7` through `718e635`
- Files changed vs `origin/main`: backend queue/streaming/auth/preview controllers, frontend preview+queue UI, Dockerfile, changelog/docs, plus `config/db.sqlite`

Review method:
1. Commit-level and file-level diff review.
2. Targeted validation of high-risk paths (queue processing, preview ffmpeg/ffprobe, auth bypass, limiter tuning).
3. Local verification commands:
- Frontend: `npm run typecheck` (fails; details below)
- Backend: `dotnet build` unavailable in this shell (`dotnet: command not found`), so backend compile was not executed locally here.

## Executive Summary

The branch contains strong functional progress (queue analysis pipeline hardening, preview fallback architecture, compatibility transcoding), but there are several issues that should be addressed before further feature implementation:
1. A production debug marker log is still present.
2. Runtime database file changes (`config/db.sqlite`) are part of branch history.
3. Preview transcoding endpoints can spawn unbounded ffmpeg processes.
4. Frontend TypeScript currently has broad typecheck failures in this branch state.
5. A few code paths appear redundant or legacy after later fixes.

## Findings (Validated)

### Critical

1. Production debug marker still in queue processor.
- File: `backend/Queue/QueueItemProcessor.cs:43`
- Evidence: `"!!! DEBUG: QueueItemProcessor STARTING ..."`
- Risk: noisy logs, operational confusion, and accidental leftover debug state in release behavior.

2. Branch history includes `config/db.sqlite` modifications.
- Evidence: `git log origin/main..HEAD -- config/db.sqlite` includes commits `11a201d`, `9451712`, `9d92392`, `40b3dbb`.
- Risk: environment-specific runtime state in VCS, merge churn, and non-deterministic history.

### High

3. No global concurrency guard for preview ffmpeg process spawning.
- Files: `backend/Api/Controllers/PreviewHls/PreviewHlsController.cs`, `backend/Api/Controllers/PreviewRemux/PreviewRemuxController.cs`
- Evidence: controllers directly spawn ffmpeg per request (`FileName = "ffmpeg"`) at `PreviewHlsController.cs:117` and `PreviewRemuxController.cs:67`; no semaphore/pool limiting request-level process fan-out.
- Risk: resource spikes under seek-heavy playback or concurrent previews.

4. Auth bypass trusts loopback + header only.
- File: `backend/Auth/WebApplicationAuthExtensions.cs:33-37`
- Evidence: requests with `X-Analysis-Mode` and loopback IP are granted an internal identity.
- Risk: likely acceptable for internal tooling, but brittle if any local proxying/SSRF path can inject loopback requests and arbitrary header values.

### Medium

5. Preview stderr collection uses full `ReadToEndAsync`.
- Files: `backend/Api/Controllers/PreviewHls/PreviewHlsController.cs:136`, `backend/Api/Controllers/PreviewRemux/PreviewRemuxController.cs:102`
- Evidence: full stderr buffering in background task.
- Risk: elevated memory usage for noisy ffmpeg stderr; less resilient than line-streamed handling.

6. Timeout floor increased to 45s for NNTP operations.
- File: `backend/Clients/Usenet/MultiConnectionNntpClient.cs:82`
- Evidence: `const int MinTimeoutMs = 45000`.
- Risk: fewer false negatives, but can also increase stall time and mask provider degradation without additional observability.

7. Limiter defaults increase QueueAnalysis and Analysis budgets significantly.
- File: `backend/Clients/Usenet/Connections/GlobalOperationLimiter.cs:39-42`
- Evidence:
  - `maxQueueAnalysisConnections = Math.Max(12, maxQueueConnections)`
  - `maxAnalysisConnections = Math.Max(2, maxHealthCheckConnections * 4)`
- Risk: operationally helpful for heavy analysis, but easier to overcommit resources in smaller deployments if not tuned.

8. Markdown ignore policy conflicts with current documentation workflow.
- File: `.gitignore:24`
- Evidence: `*.md` blanket ignore with only selective allow-list exceptions.
- Risk: new docs are silently ignored unless force-added; this is already affecting workflow consistency.

### Low

9. Redundant parallel throttling in Step 5 analysis loop.
- File: `backend/Queue/QueueItemProcessor.cs:559-563`
- Evidence: `Parallel.ForEachAsync(... MaxDegreeOfParallelism = 10 ...)` plus an additional `SemaphoreSlim(10,10)` inside the same loop.
- Risk: unnecessary complexity; does not add effective control beyond the existing parallel setting.

10. Probe-failed files are marked corrupted immediately before deletion.
- File: `backend/Queue/QueueItemProcessor.cs:522-542`
- Evidence: set `IsCorrupted`/`CorruptionReason`, then `ExecuteDeleteAsync` the same entries.
- Risk: unnecessary write path and extra DB operations for records that are deleted immediately.

11. Preview button gating still extension-list based despite stronger fallback stack.
- File: `frontend/app/routes/health/components/file-details-modal/file-details-modal.tsx:764, 945`
- Evidence: `UNSUPPORTED_PREVIEW_EXTENSIONS` controls beta preview exposure.
- Risk: potential false negatives for playable formats now that HLS/remux fallback exists.

## Verification Notes

Frontend typecheck result: failing (multiple errors across routes/components/types). Representative examples:
- `app/routes/queue/route.tsx`: `QueueTableProps` mismatch (`totalCount` not in props)
- `app/routes/health/components/file-details-modal/file-details-modal.tsx`: `fileDetails.fileName` property mismatch
- Multiple existing route/style/type errors outside this latest preview-only scope

This branch is not currently typecheck-clean end-to-end, so additional fixes should include a typecheck stabilization pass.

## Changes That Look Good and Should Stay

1. Queue completion moved after Step 5 verification (Step 6 pattern) in `QueueItemProcessor`.
2. Smart-analysis/DMCA handling improvements in `UsenetStreamingClient` and `BufferedSegmentStream`.
3. Preview fallback architecture (native HLS -> HLS.js -> remux) plus compatibility transcoding profile in preview controllers.
4. Connection usage context propagation fixes and QueueAnalysis visibility improvements in stats/UI.

## Recommended Pre-Implementation Action Order

1. Remove debug log marker and similar temporary diagnostics from production paths.
2. Remove `config/db.sqlite` from branch history going forward policy (at minimum stop future commits; optional history cleanup decision by maintainer).
3. Add process concurrency guard for preview ffmpeg endpoints.
4. Decide and harden auth bypass model for internal analysis requests.
5. Simplify redundant Step 5 loop controls and remove pre-delete corruption writes.
6. Resolve TypeScript branch-wide typecheck failures to re-establish a reliable CI baseline.
7. Revisit `.gitignore` markdown policy so analysis/research docs do not require force-add.

## Implementation Status

No code changes were implemented from this analysis document. This is a review-only checkpoint as requested, to approve before making additional fixes.
