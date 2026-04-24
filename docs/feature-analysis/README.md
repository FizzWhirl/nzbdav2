# Feature Analysis Reports — 2026-04-24

Companion analysis to [../fork-vs-upstream-analysis-2026-04-24.md](../fork-vs-upstream-analysis-2026-04-24.md).

## Read order

| # | Document | Purpose |
|---|---|---|
| 00 | [00-meta-review-rclone-plex.md](00-meta-review-rclone-plex.md) | Holistic suitability + rclone tuning recipe |
| 01 | [01-shared-streams.md](01-shared-streams.md) | Multi-reader stream dedup |
| 02 | [02-article-caching.md](02-article-caching.md) | Per-queue-item segment cache |
| 03 | [03-prioritized-semaphore.md](03-prioritized-semaphore.md) | High/Low fairness for connection acquisition |
| 04 | [04-connection-pool-resilience.md](04-connection-pool-resilience.md) | Circuit breaker, backoff, reserve, sweeper |
| 05 | [05-nzbfilestream-bufferedsegmentstream.md](05-nzbfilestream-bufferedsegmentstream.md) | Core streaming engine + seek behaviour |
| 06 | [06-queue-pipeline.md](06-queue-pipeline.md) | Step 0–6 processor refactor |
| 07 | [07-preview-hls-remux.md](07-preview-hls-remux.md) | In-browser playback (HLS + remux fallback) |
| 08 | [08-v1-v2-migration.md](08-v1-v2-migration.md) | Upstream-blob recovery + schema self-heal |

Each per-feature doc follows the same template: Summary → Value →
Behavioural Model → Possible Issues → Code Quality → Recommended
Improvements.
