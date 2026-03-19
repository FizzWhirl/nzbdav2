# nzbdav2 README Design Spec

**Date:** 2026-03-19
**Topic:** Public README for nzbdav2
**Audience:** Developers and contributors
**Approach:** Lean developer-focused README (Option A)

---

## Context

nzbdav2 is an independent public repository whose codebase derives from [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav). It is not set up as a GitHub fork. Some changes were incorporated during early development from [johoja12/nzbdav](https://github.com/johoja12/nzbdav) (now private). nzbdav2 is not a continuation of that fork and is maintained independently.

The existing README is largely a copy of upstream's README with a "Fork Enhancements" section appended. It does not accurately describe the provenance, the architectural differences, or what has and hasn't been adopted from upstream.

---

## Goals

- Clearly state the lineage and independence of nzbdav2
- Document what is original to this fork vs adopted from upstream
- Document what upstream features were deliberately skipped and why
- Give developers enough architecture context to contribute, pointing to CLAUDE.md for deep dives
- Include a minimal quick-start (Docker) for completeness
- Keep upstream sync process discoverable
- Retain the detailed changelog (it is the primary release history)

---

## README Structure

### 1. Header + One-liner

Project name and brief description of what the project does (WebDAV server for streaming NZBs without downloading). Immediately followed by the provenance statement.

**Provenance statement:**
> nzbdav2 is an independent project based on [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav). During early development, some changes were incorporated from [johoja12/nzbdav](https://github.com/johoja12/nzbdav) (now private). nzbdav2 is not a continuation of that fork and is developed and maintained independently.

### 2. What's Different (nzbdav2 Additions)

Features that are original to nzbdav2 and not present in upstream:

1. **`BufferedSegmentStream`** — Producer-consumer RAM jitter buffer with parallel out-of-order segment fetching and straggler detection. Upstream uses a sequential `MultiSegmentStream`. This is the core streaming engine difference and the reason playback is smooth at high bitrates.
2. **Persistent Seek Cache** — Segment byte offsets are cached in the DB during health checks and analysis, enabling O(log N) instant seeking without NNTP round-trips. Upstream has no equivalent.
3. **Priority Queuing via `GlobalOperationLimiter`** — Static slot partitioning reserves connections for active streaming vs background tasks (health checks, queue). Upstream uses `PrioritizedSemaphore` (dynamic probability-based); nzbdav2 uses static partitioning which is simpler and works well for the target use case.
4. **Audio file support** — nzbdav2 recognizes audio file extensions and accepts audio-only NZBs. `EnsureImportableMediaValidator` validates for both video and audio. Default SABnzbd categories include `audio`. Upstream is video-only.
5. **Provider Stats UI** — Real-time per-provider performance tracking: throughput, success rate, active connection usage, file being served. Accessible from the Stats page.
6. **Media Analysis via ffprobe** — Deep media verification triggered on-demand or automatically during health checks. Codec info (video/audio streams) is displayed in the File Details modal. ffmpeg/ffprobe is bundled in the Docker image.
7. **Rich `FileDetailsModal`** — Per-file actions: health check, repair, analyze, test download, provider stats. Upstream's equivalent is a simpler dropdown with Preview/Download/Export NZB.

### 3. Intentional Divergences from Upstream

Upstream features that were evaluated and intentionally implemented differently:

- **Blobstore migration (skipped)** — Upstream moved NZB XML content to filesystem blobs. nzbdav2 instead applies Zstd compression to NZB content stored in-DB via EF Core value converters, achieving ~31% DB size reduction without a non-reversible schema migration. See `docs/upstream-sync-2026-03-10.md` for rationale.
- **`RcloneRcService` (adopted, different architecture)** — Upstream uses a static `RcloneClient` with flat config keys. nzbdav2 uses a DI-injected `RcloneRcService` singleton with `IHttpClientFactory` and a single JSON config blob. Additionally includes `DeleteFromDiskCache` which upstream does not have.

### 4. Deliberately Skipped Upstream Features

Features evaluated but not adopted, with reasons documented in `docs/upstream-sync-*.md`:

| Feature | Reason |
|---|---|
| Blobstore | Non-reversible, high-risk. Replaced with in-DB Zstd compression. |
| Export NZB from Explore | Depends on blobstore. Could be reimplemented from in-DB NzbContents. |
| User-Agent configuration | NNTP protocol does not support user-agent headers. No effect. |
| Explore page actions dropdown | Replaced by the richer `FileDetailsModal`. |
| `PrioritizedSemaphore` | `GlobalOperationLimiter` meets current needs. Revisit if contention issues arise. |
| `UnbufferedMultiSegmentStream` | Potential low-memory fallback. Not needed currently. |

### 5. Architecture Overview

Brief summary of the dual-service architecture:
- **Backend** — .NET 10.0 ASP.NET Core. WebDAV server, SABnzbd-compatible API, Usenet client, SQLite via EF Core. (`/backend`)
- **Frontend** — React Router v7 with SSR, Express proxy, WebSocket for real-time updates. (`/frontend`)
- `entrypoint.sh` starts backend first (health-gated), then frontend.

Reference `CLAUDE.md` for full architecture, development commands, DB schema, and configuration details.

### 6. Quick Start (Docker)

Minimal Docker run command using `ghcr.io/dgherman/nzbdav2:latest` with a config volume and common env vars (`PUID`, `PGID`). No duplication of the full setup guide from upstream.

### 7. Upstream Sync

One paragraph: nzbdav2 tracks `nzbdav-dev/nzbdav` and periodically merges relevant upstream changes. Sync history and rationale for each adoption/skip decision are documented in `docs/upstream-sync-*.md`.

### 8. Changelog

Retain existing changelog verbatim. It is the primary release history.

---

## What the README Does NOT Include

- Full RClone setup instructions (those belong in a setup guide, not the dev-focused README)
- Full Sonarr/Radarr integration walkthrough
- Docker Compose examples
- Screenshots

These are present in upstream's README and are well-documented there. Since nzbdav2's audience is developers/contributors, deep setup docs add noise. A link to upstream's README for end-user setup is appropriate.

---

## Files Changed

- `README.md` — full replacement (existing content is upstream's README with an appended section)

## Files Referenced (not changed)

- `CLAUDE.md` — deep architecture reference (linked from README)
- `docs/upstream-sync-*.md` — upstream sync history (linked from README)
