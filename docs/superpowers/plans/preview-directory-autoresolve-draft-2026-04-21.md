# Preview Directory Auto-Resolve Draft (2026-04-21)

## Goal
Allow preview endpoints to accept a directory DavItemId and automatically resolve it to the best media file child, instead of rejecting with 400.

## Current Behavior
- HLS/remux now explicitly reject non-file DavItem IDs.
- This is safe and explicit, but users may still pass release/folder IDs from tooling.

## Proposed Behavior
- If DavItem is already a file type (NzbFile, RarFile, MultipartFile), use it directly.
- If DavItem is a directory, find the best preview candidate in descendants:
  - Prefer known video extensions first (mkv, mp4, avi, mov, m4v, ts, m2ts, webm).
  - Prefer largest file size among candidates.
- If no previewable descendant is found, return 404 with a clear message.

## Candidate Implementation
1. Add helper in DavDatabaseClient:
   - FindBestPreviewDescendantAsync(Guid rootId)
2. Helper query:
   - Filter to item types: NzbFile, RarFile, MultipartFile
   - Restrict to descendants by Path prefix (root.Path + "/")
   - Optional extension scoring in SQL or in-memory post-filter
3. Update preview controllers:
   - HLS and Remux call a shared resolver method:
     - ResolvePreviewItemAsync(Guid requestedId)
   - Keep existing zero-output 502 behavior unchanged.

## Minimal API Change
No API contract changes needed. Existing route remains:
- /api/preview/hls/{davItemId}/index.m3u8
- /api/preview/hls/{davItemId}/segment/{segIndex}.ts
- /api/preview/remux/{davItemId}

## Logging Additions
- Log when auto-resolve occurs:
  - requested DavItemId
  - resolved child DavItemId
  - resolved child path/name

## Risks
- Path prefix scans can be expensive in very large trees.
- Mitigation: add LIMIT + index-aware predicate (Path starts with prefix) and keep query bounded.

## Suggested Test Matrix
- Directory with one media child -> resolves and streams.
- Directory with multiple media children -> picks preferred/largest.
- Directory with no media children -> 404.
- Existing file DavItemId path -> unchanged behavior.
- ffmpeg error before output -> still returns 502.
