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
- Document what is original to nzbdav2 vs adopted from upstream
- Document what upstream features were deliberately skipped and why
- Give developers enough architecture context to contribute, pointing to CLAUDE.md for deep dives
- Include a minimal quick-start (Docker) for completeness
- Keep upstream sync process discoverable
- Retain the detailed changelog (it is the primary release history)

---

## README Structure

### 1. Header + One-liner

Project name and brief description of what the project does (WebDAV server for streaming NZBs without downloading). Immediately followed by the provenance statement.

**Provenance statement (exact wording):**
> nzbdav2 is an independent project based on [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav). During early development, some changes were incorporated from [johoja12/nzbdav](https://github.com/johoja12/nzbdav) (now private). nzbdav2 is not a continuation of that project and is developed and maintained independently.

The johoja12 reference is a historical attribution note only — it does not imply any ongoing relationship or shared direction. The README should not describe which specific changes came from there (the repo is private and the changes are now embedded in the codebase history). The sentence "nzbdav2 is not a continuation of that project" is the key statement.

### 2. What nzbdav2 Adds

Features that are original to nzbdav2 and not present in upstream (nzbdav-dev/nzbdav):

1. **`BufferedSegmentStream`** — Producer-consumer RAM jitter buffer with parallel out-of-order segment fetching and straggler detection. Upstream uses a sequential `MultiSegmentStream`. This is the core streaming engine difference; the RAM buffer isolates the player from network jitter and eliminates stutter at high bitrates.
2. **Persistent Seek Cache** — Segment byte offsets are cached in the database during health checks and analysis, enabling O(log N) instant seeking without NNTP round-trips for previously accessed files. Upstream has no equivalent.
3. **Priority Queuing via `GlobalOperationLimiter`** — Statically partitions connections between active streaming and background tasks (health checks, queue processing). Upstream uses `PrioritizedSemaphore` (dynamic probability-based); nzbdav2's static approach is simpler and sufficient for the target use case.
4. **Audio file support** — Recognizes audio file extensions, accepts audio-only NZBs. `EnsureImportableMediaValidator` validates for both video and audio. Default SABnzbd categories include `audio`. Upstream is video-only.
5. **Provider Stats UI** — Real-time per-provider performance tracking: throughput, success rate, connection usage, file being served. Accessible from the Stats page. No upstream equivalent.
6. **Media Analysis via ffprobe** — Deep media verification triggered on demand or automatically during health checks. Codec info (video/audio streams) displayed in the File Details modal. ffmpeg/ffprobe is bundled in the Docker image. No upstream equivalent.
7. **Rich `FileDetailsModal`** — Per-file actions panel: health check, repair, analyze, test download, provider stats. Upstream's equivalent is a simpler dropdown (Preview/Download/Export NZB), which was skipped in favour of this modal.

### 3. Adopted Upstream Features with Architectural Differences

Features that came from upstream (nzbdav-dev/nzbdav) but are implemented differently in nzbdav2. The README should briefly describe each, why the approach differs, and where to look for implementation details.

- **Zstd NZB compression (in-DB instead of blobstore)** — Upstream's blobstore migration moves NZB XML content to filesystem blobs. nzbdav2 skipped the blobstore and instead applies Zstd compression to NZB content stored in-DB via EF Core value converters (~31% DB size reduction). This was a deliberate tradeoff: no non-reversible schema migration, simpler rollback, same space savings. Rationale documented in `docs/upstream-sync-2026-03-10.md`.
- **`RcloneRcService` (DI-injected vs. static)** — Upstream uses a static `RcloneClient` with flat config keys. nzbdav2 uses a DI-injected `RcloneRcService` singleton with `IHttpClientFactory` and a single JSON config blob. Also adds `DeleteFromDiskCache` which upstream does not have.

### 4. Deliberately Skipped Upstream Features

Features evaluated and intentionally not adopted. Table columns: **Feature | Why Skipped | Re-evaluate If**. Each row must include a "re-evaluate if" condition drawn from `docs/upstream-sync-*.md` so future maintainers know when to revisit.

| Feature | Why Skipped | Re-evaluate If |
|---|---|---|
| Blobstore migration | Non-reversible, high-risk schema change. In-DB Zstd compression achieves the same space savings safely. | DB size becomes a problem or upstream makes blobstore reversible. |
| Export NZB from Explore | Depends on blobstore (`nzbBlobId`). | Could be reimplemented to read from in-DB NzbContents if the feature is wanted. |
| User-Agent configuration | NNTP protocol does not support user-agent headers; the setting has no effect. | N/A — not a real feature. |
| Explore page actions dropdown | Replaced by the richer `FileDetailsModal` which provides a superset of actions. | N/A — already covered. |
| `PrioritizedSemaphore` | `GlobalOperationLimiter` meets current needs. Requires significant refactor. | Contention issues arise with the current static partitioning. |
| `UnbufferedMultiSegmentStream` | Not needed. Would be a low-memory fallback. | Low-memory deployment scenario becomes relevant. |

### 5. Architecture Overview

This section describes how the system components fit together structurally. It is NOT a repeat of the feature capabilities listed in Section 2. Features already described in Section 2 (e.g., `BufferedSegmentStream`, `GlobalOperationLimiter`) should be referenced by name in context, not re-described.

Cover the following, briefly (2–3 sentences each):

- **Dual-service setup** — Backend (.NET 10.0 ASP.NET Core, port 8080) and Frontend (React Router v7 + SSR + Express, port 3000). `entrypoint.sh` health-gates frontend startup on backend readiness.
- **Streaming pipeline** — How NZB segment IDs flow from DB through `BufferedSegmentStream` to the WebDAV response. Range requests enable seeking. Archive contents (RAR/7z) extracted via `SharpCompress` streaming API without local storage.
- **WebDAV virtual filesystem** — NZB contents exposed as a virtual directory. Completed items expose `.rclonelink` files translated to symlinks by RClone, making content addressable to Sonarr/Radarr without storage use.
- **Health and repair** — Background `HealthCheckService` validates article availability per provider. Triggers Par2 recovery or Sonarr/Radarr re-search as needed. Priority queuing (`GlobalOperationLimiter`) ensures health checks never starve active streams.
- **SABnzbd-compatible API** — Drop-in replacement for sabnzbd, used by Sonarr/Radarr as the download client.

End section with: *For full architecture, DB schema, development commands, and configuration details, see [`CLAUDE.md`](./CLAUDE.md).*

### 6. Quick Start (Docker)

Use the exact command below (port and volume path verified against `Dockerfile` and existing README):

```bash
mkdir -p $(pwd)/nzbdav2 && \
docker run --rm -it \
  -v $(pwd)/nzbdav2:/config \
  -e PUID=1000 \
  -e PGID=1000 \
  -p 3000:3000 \
  ghcr.io/dgherman/nzbdav2:latest
```

Follow with one sentence: navigate to Settings to configure Usenet providers. One sentence pointing to `CLAUDE.md` for all environment variables and configuration options. Do not duplicate the full setup guide.

For end users wanting full setup and integration walkthroughs (RClone, Sonarr/Radarr, Docker Compose), add an explicit line in this section: "For a complete deployment guide, see the [upstream README](https://github.com/nzbdav-dev/nzbdav#readme)."

### 7. Upstream Sync

One paragraph covering: (a) nzbdav2 tracks `nzbdav-dev/nzbdav` and periodically cherry-picks relevant upstream changes manually; (b) each sync documents what was adopted, what was skipped, and the rationale; (c) the sync history lives in `docs/upstream-sync-*.md`. Do not prescribe a cadence or imply automation.

### 8. Changelog

Retain the existing changelog verbatim, inline at the bottom of README.md. The changelog is long and this makes the file large, but it is the primary release history and the audience (developers) will navigate directly to it. A separate CHANGELOG.md is not needed; GitHub renders anchor links to headings, making it navigable.

---

## What the README Does NOT Include

- Full RClone setup, configuration, and mount options
- Sonarr/Radarr integration walkthrough
- Docker Compose examples
- Screenshots

Rationale: the audience is developers/contributors, not end users deploying for the first time. End-user setup is well-documented in the upstream README. A link to upstream for deployment setup is appropriate.

---

## Files Changed

- `README.md` — full replacement

## Files Referenced (not changed)

- `CLAUDE.md` — deep architecture reference (linked from README)
- `docs/upstream-sync-*.md` — upstream sync history (linked from README)
