# nzbdav2 README Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the existing README.md in `dgherman/nzbdav2` with a developer-focused document that accurately describes provenance, original features, architectural divergences from upstream, and how to contribute.

**Architecture:** Single file replacement (`README.md`). All content is written section-by-section following the spec, assembled into one file, then pushed via `gh` CLI using the GitHub Contents API. No worktree needed — this is a documentation-only change.

**Tech Stack:** gh CLI (GitHub API), Markdown, base64 (macOS)

**Spec:** `docs/superpowers/specs/2026-03-19-nzbdav2-readme-design.md` in `dgherman/nzbdav2`

---

## File Map

| File | Action | Purpose |
|------|--------|---------|
| `README.md` | Replace | Public-facing developer README |

---

## Before You Start

Read these files from `dgherman/nzbdav2` to understand the current state:

```bash
# Current README (to extract changelog section verbatim)
gh api repos/dgherman/nzbdav2/contents/README.md --jq '.content' | base64 -d

# Spec (the blueprint for this plan)
gh api repos/dgherman/nzbdav2/contents/docs/superpowers/specs/2026-03-19-nzbdav2-readme-design.md --jq '.content' | base64 -d

# Upstream sync doc (for rationale details used in sections 3-4)
gh api repos/dgherman/nzbdav2/contents/docs/upstream-sync-2026-03-10.md --jq '.content' | base64 -d
```

---

## Task 1: Write Section 1 — Header + Provenance

**Files:**
- Modify: `README.md` (start fresh, write first section)

- [ ] **Step 1.1: Start the README file locally**

Create `/tmp/nzbdav2-README.md` with the header and provenance statement. Use the exact provenance wording from the spec verbatim:

```markdown
# nzbdav2

nzbdav2 is a WebDAV server that allows you to mount and stream NZB content as a virtual file system without downloading. It integrates with Sonarr and Radarr via a SABnzbd-compatible API and enables streaming directly from Usenet providers through Plex or Jellyfin — using no local storage.

> **Provenance:** nzbdav2 is an independent project based on [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav). During early development, some changes were incorporated from [johoja12/nzbdav](https://github.com/johoja12/nzbdav) (now private). nzbdav2 is not a continuation of that project and is developed and maintained independently.
```

- [ ] **Step 1.2: Verify the provenance statement contains all three required sentences**

Check that the text includes:
1. "independent project based on nzbdav-dev/nzbdav" (not a fork)
2. "johoja12/nzbdav (now private)" (historical attribution)
3. "not a continuation of that project" (key independence statement)

---

## Task 2: Write Section 2 — What nzbdav2 Adds

**Files:**
- Modify: `/tmp/nzbdav2-README.md` (append)

- [ ] **Step 2.1: Append the "What nzbdav2 Adds" section**

This section lists features **original to nzbdav2 only** — not adopted from upstream. Each entry must include: name, what it does, and why it matters or how it differs from upstream.

```markdown
## What nzbdav2 Adds

These features are original to nzbdav2 and not present in [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav).

### BufferedSegmentStream

A producer-consumer RAM jitter buffer that pre-fetches NZB segments in parallel, handles out-of-order arrival, and re-orders them before writing to the output stream. Includes straggler detection: if the head-of-line segment stalls for more than 1.5s, a duplicate fetch races it on another connection. Upstream uses a sequential `MultiSegmentStream` with no buffering. The RAM buffer isolates the player from network jitter and eliminates stutter at high bitrates.

### Persistent Seek Cache

Segment byte offsets are cached in the database during health checks and media analysis. This enables O(log N) instant seeking for previously accessed files — no NNTP round-trips needed. Without the cache, each seek requires interpolation searches across the provider. Upstream has no equivalent.

### Priority Queuing via `GlobalOperationLimiter`

Connections are statically partitioned between active streaming requests and background tasks (health checks, queue processing). This prevents background work from starving playback connections. Upstream uses `PrioritizedSemaphore` with dynamic probability-based allocation; nzbdav2's static partitioning is simpler and sufficient for the target use case.

### Audio File Support

nzbdav2 recognizes audio file extensions and accepts audio-only NZBs as valid imports. `EnsureImportableMediaValidator` validates for both video and audio files. Default SABnzbd categories include `audio`. Upstream is video-only — audio NZBs would be rejected.

### Provider Stats UI

Real-time per-provider performance tracking visible from the Stats page: throughput (MB/s), success rate, active connection usage, and the file currently being served per connection. No upstream equivalent.

### Media Analysis via ffprobe

Deep media verification triggered on demand (from File Details modal) or automatically during health checks when a file lacks media metadata. Displays video and audio codec information in the File Details modal. ffmpeg and ffprobe are bundled in the Docker image. No upstream equivalent.

### Rich File Details Modal

A per-file action panel accessible from Health, Stats, and Explore pages. Provides: run health check, trigger repair (delete + Sonarr/Radarr re-search), run media analysis, test download, and view per-provider stats. Upstream's equivalent is a simpler dropdown with Preview, Download, and Export NZB.
```

- [ ] **Step 2.2: Verify all 7 features are present**

Count: BufferedSegmentStream, Persistent Seek Cache, GlobalOperationLimiter, Audio support, Provider Stats UI, Media Analysis, FileDetailsModal. If any are missing, add them before continuing.

---

## Task 3: Write Section 3 — Adopted Upstream Features with Architectural Differences

**Files:**
- Modify: `/tmp/nzbdav2-README.md` (append)

- [ ] **Step 3.1: Append Section 3**

This section covers features that **came from upstream but were implemented differently**. Do not list features that were simply adopted as-is (e.g., .NET 10 upgrade, bug fixes, duplicate segment fallback).

```markdown
## Adopted Upstream Features — Architectural Differences

These features originated in [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav) but are implemented differently in nzbdav2. Implementation rationale is documented in [`docs/upstream-sync-2026-03-10.md`](./docs/upstream-sync-2026-03-10.md).

### Zstd NZB Compression (In-DB Instead of Blobstore)

Upstream's v0.6.0 migration moves NZB XML content to a filesystem blobstore. nzbdav2 skipped the blobstore and instead applies Zstd compression to NZB content stored in-DB via an EF Core value converter. This achieves approximately 31% database size reduction (856MB → 592MB after VACUUM) without a non-reversible schema migration. The blobstore migration was evaluated and skipped; see the sync doc for the full rationale.

### RcloneRcService (DI-Injected vs. Static)

Upstream uses a static `RcloneClient` class with flat config keys (`rclone.host`, `rclone.user`, `rclone.pass`). nzbdav2 uses a DI-injected `RcloneRcService` singleton backed by `IHttpClientFactory` with a single JSON config blob at the `rclone.rc` key. nzbdav2 also adds `DeleteFromDiskCache` — explicit RClone VFS cache invalidation — which upstream does not have.
```

- [ ] **Step 3.2: Verify both entries reference the sync doc and include rationale**

Each entry must explain *why* the approach differs, not just *that* it differs.

---

## Task 4: Write Section 4 — Deliberately Skipped Upstream Features

**Files:**
- Modify: `/tmp/nzbdav2-README.md` (append)

- [ ] **Step 4.1: Append Section 4 with the full table**

```markdown
## Deliberately Skipped Upstream Features

These upstream features were evaluated and intentionally not adopted. The "Re-evaluate If" column documents when to revisit each decision. Full rationale is in [`docs/upstream-sync-2026-03-10.md`](./docs/upstream-sync-2026-03-10.md).

| Feature | Why Skipped | Re-evaluate If |
|---|---|---|
| Blobstore migration | Non-reversible schema change. In-DB Zstd compression achieves the same size savings safely. | DB size becomes a problem again, or upstream makes blobstore reversible. |
| Export NZB from Explore | Depends on blobstore (`nzbBlobId`). Not applicable without blobstore. | Could be reimplemented to read from in-DB `NzbContents` if the feature is wanted. |
| User-Agent configuration | NNTP protocol does not support user-agent headers. The setting has no effect. | N/A — not a real feature. |
| Explore page actions dropdown | Superseded by the richer `FileDetailsModal`, which provides a superset of actions. | N/A — already covered. |
| `PrioritizedSemaphore` | `GlobalOperationLimiter` meets current needs. Adopting this requires a significant refactor of `GlobalOperationLimiter` and `ConnectionPool`. | Static partitioning causes observed contention issues under load. |
| `UnbufferedMultiSegmentStream` | Not needed. Would serve as a low-memory fallback for `BufferedSegmentStream`. | A low-memory deployment scenario becomes relevant. |
```

- [ ] **Step 4.2: Verify all 6 rows are present and the "Re-evaluate If" column is populated for every row**

---

## Task 5: Write Section 5 — Architecture Overview

**Files:**
- Modify: `/tmp/nzbdav2-README.md` (append)

- [ ] **Step 5.1: Append Section 5**

This section describes system structure — how components fit together. **Do not re-describe features already listed in Section 2.** Reference `BufferedSegmentStream` and `GlobalOperationLimiter` by name in context only.

```markdown
## Architecture Overview

For full architecture, DB schema, development commands, and configuration details, see [`CLAUDE.md`](./CLAUDE.md).

### Dual-Service Setup

The application runs two processes managed by `entrypoint.sh`:

- **Backend** (`/backend`) — .NET 10.0 ASP.NET Core on port 8080. WebDAV server, SABnzbd-compatible API, Usenet client, streaming engine, SQLite database via EF Core.
- **Frontend** (`/frontend`) — React Router v7 with server-side rendering, Express proxy on port 3000. Proxies API requests to backend, serves SSR pages, WebSocket connection for real-time updates.

`entrypoint.sh` health-gates frontend startup: it waits for the backend health endpoint before starting the frontend, and shuts down both processes if either exits.

### Streaming Pipeline

NZB segment IDs are stored in the database when an NZB is queued. When a WebDAV request arrives, the backend streams segment data on demand via `BufferedSegmentStream` — no content is stored locally. Range requests are supported, enabling seeking. Archive contents (RAR/7z) are extracted via the `SharpCompress` streaming API without writing to disk.

### WebDAV Virtual Filesystem

NZB contents are exposed as a virtual directory hierarchy. Completed items expose `.rclonelink` files that RClone translates to native symlinks when mounting the WebDAV server. Sonarr and Radarr pick up these symlinks, move them to the media library, and Plex/Jellyfin stream through them — all without any local copy of the content.

### Health and Repair

`HealthCheckService` runs in the background, validating article availability on each configured Usenet provider. When segments are missing, it triggers Par2 recovery or a Sonarr/Radarr re-search (blacklist + new grab). `GlobalOperationLimiter` ensures health check connections never starve active streaming connections.

### SABnzbd-Compatible API

The backend exposes a SABnzbd-compatible API subset (`/api?mode=...`). Sonarr and Radarr are configured to use nzbdav2 as their download client. When they send an NZB, nzbdav2 mounts it to the WebDAV filesystem and signals completion — no actual download occurs.
```

- [ ] **Step 5.2: Verify CLAUDE.md is linked at the top of this section**

---

## Task 6: Write Section 6 — Quick Start

**Files:**
- Modify: `/tmp/nzbdav2-README.md` (append)

- [ ] **Step 6.1: Append Section 6 with the exact docker run command**

```markdown
## Quick Start

```bash
mkdir -p $(pwd)/nzbdav2 && \
docker run --rm -it \
  -v $(pwd)/nzbdav2:/config \
  -e PUID=1000 \
  -e PGID=1000 \
  -p 3000:3000 \
  ghcr.io/dgherman/nzbdav2:latest
```

After starting, navigate to `http://localhost:3000` and open Settings to configure your Usenet provider connections.

For all environment variables and configuration options, see [`CLAUDE.md`](./CLAUDE.md).

For a complete deployment guide including RClone mount configuration, Sonarr/Radarr integration, and Docker Compose setup, see the [upstream README](https://github.com/nzbdav-dev/nzbdav#readme).
```

- [ ] **Step 6.2: Verify the image tag is `ghcr.io/dgherman/nzbdav2:latest` (not the upstream `ghcr.io/johoja12/nzbdav:latest` present in the current README)**

---

## Task 7: Write Section 7 — Upstream Sync

**Files:**
- Modify: `/tmp/nzbdav2-README.md` (append)

- [ ] **Step 7.1: Append Section 7**

```markdown
## Upstream Sync

nzbdav2 tracks [nzbdav-dev/nzbdav](https://github.com/nzbdav-dev/nzbdav) and periodically cherry-picks relevant upstream changes manually. Each sync documents which changes were adopted, which were skipped, and the rationale for each decision. Sync history is in [`docs/upstream-sync-*.md`](./docs/). The most recent file contains the last reviewed upstream commit and a table of all items evaluated.
```

---

## Task 8: Append Changelog (Verbatim from Existing README)

**Files:**
- Modify: `/tmp/nzbdav2-README.md` (append)

- [ ] **Step 8.1: Extract the changelog from the current README**

```bash
gh api repos/dgherman/nzbdav2/contents/README.md --jq '.content' | base64 -d | sed -n '/^# Changelog/,$p'
```

- [ ] **Step 8.2: Append the extracted changelog to `/tmp/nzbdav2-README.md`**

Paste the output of the above command (everything from `# Changelog` to end of file) verbatim onto the end of the working README file. Do not edit any changelog content.

- [ ] **Step 8.3: Verify the changelog is present and non-empty**

```bash
gh api repos/dgherman/nzbdav2/contents/README.md --jq '.content' | base64 -d | grep "^## v"
```

Expected: one or more version lines (e.g. `## v0.1.29 (2026-01-14)`). If no output, the changelog was not appended.

---

## Task 9: Push to GitHub and Verify

**Files:**
- Replace: `README.md` in `dgherman/nzbdav2`

- [ ] **Step 9.1: Get the current SHA of README.md (required for the PUT)**

```bash
sha=$(gh api repos/dgherman/nzbdav2/contents/README.md --jq '.sha')
echo $sha
```

- [ ] **Step 9.2: Push the new README via GitHub API**

```bash
gh api repos/dgherman/nzbdav2/contents/README.md \
  --method PUT \
  --field message="docs: replace README with developer-focused version" \
  --field content="$(base64 -i /tmp/nzbdav2-README.md)" \
  --field sha="$sha" \
  --jq '.commit.sha'
```

Expected: a commit SHA is printed. If you get a 422 error, re-fetch the SHA in Step 9.1 and retry.

- [ ] **Step 9.3: Verify the push succeeded by fetching and spot-checking the new README**

```bash
gh api repos/dgherman/nzbdav2/contents/README.md --jq '.content' | base64 -d | head -20
```

Expected output: starts with `# nzbdav2` and the provenance blockquote is visible within the first 20 lines.

- [ ] **Step 9.4: Verify the upstream image reference is gone**

```bash
gh api repos/dgherman/nzbdav2/contents/README.md --jq '.content' | base64 -d | grep "johoja12/nzbdav:latest"
```

Expected: no output (the old upstream `docker pull ghcr.io/johoja12/nzbdav:latest` command must not appear).

- [ ] **Step 9.5: Verify all 8 sections are present**

```bash
gh api repos/dgherman/nzbdav2/contents/README.md --jq '.content' | base64 -d | grep "^## "
```

Expected output (in order):
```
## What nzbdav2 Adds
## Adopted Upstream Features — Architectural Differences
## Deliberately Skipped Upstream Features
## Architecture Overview
## Quick Start
## Upstream Sync
## Changelog
```
