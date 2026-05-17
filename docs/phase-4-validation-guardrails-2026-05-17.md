# Phase 4 Validation Guardrails - 2026-05-17

This phase adds lightweight automated checks around the highest-risk boundaries identified in the full application review.

## Added Checks

* Frontend contract tests for the shared WebSocket topic-message guard.
* Frontend `react-router typegen` plus `tsc -b` in CI.
* Backend `dotnet restore` and Release build in CI before Docker publish.
* Shell syntax checks for the container entrypoints before Docker publish.
* Docker publish workflow documentation for the GitHub Actions run-number patch offset.

## Remaining High-Value Tests

These are still the next best targets for a fuller test harness:

* Queue-to-history-to-Arr failed-download lifecycle with Arr-authoritative cleanup.
* Hidden-history cleanup and `HistoryCleanupService` VFS invalidation.
* Failed queue items not appearing in SAB queue responses after history transition.
* Partial season-pack deletion resolving affected Sonarr episodes.
* Frontend backend-WebSocket relay survival after malformed backend frames.
* Entrypoint migration failure using an Alpine-compatible smoke harness.
* Docker publish smoke checks that run without pushing for pull-request workflows.