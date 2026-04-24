# Feature Report — Preview HLS + Remux

**Files:**
- [backend/Api/Controllers/PreviewHls/PreviewHlsController.cs](../../backend/Api/Controllers/PreviewHls/PreviewHlsController.cs) (~360 LOC, new)
- [backend/Api/Controllers/PreviewRemux/PreviewRemuxController.cs](../../backend/Api/Controllers/PreviewRemux/PreviewRemuxController.cs) (~220 LOC, new)
- [backend/Services/PreviewProcessLimiter.cs](../../backend/Services/PreviewProcessLimiter.cs) (new)
- [frontend/app/routes/health/components/file-details-modal/file-details-modal.tsx](../../frontend/app/routes/health/components/file-details-modal/file-details-modal.tsx) (~950 LOC, +317 vs upstream)

## Summary
In-browser playback of arbitrary NZB-mounted media via three modes:
1. **Native HLS** (Safari/iOS/Edge) — direct `<video>` `application/vnd.apple.mpegurl`.
2. **HLS.js** — JS polyfill for browsers without native HLS.
3. **Remux** (fallback) — single-shot ffmpeg pipe to fragmented MP4.

Frontend tries them in order and falls through automatically on failure.

## Endpoints

| Endpoint | Returns |
|---|---|
| `GET /api/preview/hls/{id}/index.m3u8` | M3U8 playlist (12 s segments) |
| `GET /api/preview/hls/{id}/segment/{n}.ts` | MPEG-TS segment (always transcoded) |
| `GET /api/preview/remux/{id}?start=N` | Fragmented MP4 stream (transcoded H.264 + AAC) |

## Value
- Browse the entire NZB-mounted library directly from the health UI
  without spinning up Plex / Jellyfin.
- Lets a user verify a file plays end-to-end before letting Sonarr/Radarr
  consume it.
- Provides a low-friction debugging surface ("does this file actually
  play?") that complements ffprobe (which only proves it parses, not that
  it plays).

## Behavioural Model

### HLS Controller
- Always transcodes (`-c:v libx264 -preset veryfast -pix_fmt yuv420p`).
  Rationale: stream-copy seeks to nearest keyframe, which can produce
  segments longer than the playlist's `EXTINF`, breaking HLS.js seeking.
- Per-segment, per-request ffmpeg invocation pulled from
  `PreviewProcessLimiter` (default 4 concurrent, env var
  `PREVIEW_MAX_FFMPEG_PROCESSES`).
- Reads source via the **internal-view URL** (loopback bypass with
  `X-Analysis-Mode` header) — avoids re-traversing the WebDAV stack.
- 12 s hardcoded segment duration.

### Remux Controller
- Always transcodes; same H.264/AAC profile.
- Reads source from the WebDAV store directly (no internal HTTP), with
  `HttpContext.Items["PreviewMode"] = true` flagging the store to take
  the direct-stream path.
- Optional `?start=N` for seeking; uses ffmpeg post-input `-ss N`.
- Output is fragmented MP4 (`movflags +frag_keyframe+empty_moov+default_base_moof+faststart`).

### Frontend Decision Tree
```
extension in UNSUPPORTED_PREVIEW_EXTENSIONS  (.mkv, .avi, .mov, .ts, .wmv, .flac, .wma)
  → "Preview (beta)" — try HlsVideoPlayer (HLS modes) then remux
extension in VIDEO_EXTENSIONS  (.mp4, .webm, .m4v)
  → "Preview" — try HlsVideoPlayer modes
extension in AUDIO_EXTENSIONS  (.mp3, .aac, .ogg, .wav, .m4a, .opus)
  → raw HTML5 <audio> with direct WebDAV URL
```

### HLS.js Configuration (frontend)
```js
{
  maxBufferLength: 90, maxMaxBufferLength: 180, backBufferLength: 30,
  fragLoadingTimeOut: 60000, fragLoadingMaxRetry: 4,
  fragLoadingRetryDelay: 2000, manifestLoadingMaxRetry: 2,
  enableWorker: true, capLevelToPlayerSize: true,
}
```
On `MEDIA_ERROR`: retry up to 2× via `recoverMediaError()`, then
`swapAudioCodec()`. On `NETWORK_ERROR`: `startLoad()` retry. Fatal of any
other type: fall through to remux.

## Possible Issues / Edge Cases

### HLS Controller
| # | Issue | Severity |
|---|---|---|
| 1 | Hardcoded 12 s segment duration — no tuning for slow networks (smaller better) or premium connections (larger more efficient). | Low |
| 2 | If `MediaInfo.DurationSeconds` is missing, `ProbeDurationSecondsAsync()` runs *per segment request*. 100 segments = 100 probes. Should cache. | Medium |
| 3 | Stderr collected with `ReadToEndAsync` (flagged in earlier review #5) — unbounded for noisy ffmpeg builds. | Low |
| 4 | If client requests segments out of order (rare with HLS.js), each spawns a fresh ffmpeg. No cross-request reuse. | Medium |
| 5 | No diagnostic on ffmpeg start failure (line 129) — generic 500. | Cosmetic |

### Remux Controller
| # | Issue | Severity |
|---|---|---|
| 1 | No timeout on ffmpeg process — a 2-hour movie ties a slot for 2 hours. | Medium |
| 2 | If WebDAV stream closes mid-frame, ffmpeg may hang on stdin until killed. Mitigated by the finally-block close. | Low |
| 3 | Partial output discarded on late ffmpeg failure (502) — browser already has a partial MP4 header. `faststart` helps but not perfectly. | Low |
| 4 | No range-request semantics — start-only seek; can't request a window. | Cosmetic (acceptable for fallback) |

### Frontend Modal
| # | Issue | Severity |
|---|---|---|
| 1 | `mediaRecoveryAttempts` only resets on `FRAG_CHANGED` — pathological streams could trigger many codec swaps per session. | Low |
| 2 | `swapAudioCodec()` called without first checking `audioTracks.length` — silent no-op if only one track. | Cosmetic |
| 3 | No `currentTime` persistence per file — switching files restarts at 0. | UX |
| 4 | 60 s segment timeout is OK for typical Usenet, but cold-start ffmpeg on a slow provider can overshoot. Consider 120 s. | Low |
| 5 | Extension whitelist gates the "Preview (beta)" surface — false negatives now that HLS.js + remux give broader coverage (flagged in earlier review #11). | Medium |
| 6 | No buffering spinner on stall — user sees frozen frame. | UX |

## Code Quality
- Process spawning is correctly limited via `PreviewProcessLimiter`.
- Cancellation propagated through `HttpContext.RequestAborted`.
- Auth bypass (loopback + `X-Analysis-Mode` header) on the HLS controller
  is documented and acceptable for internal calls but is a thin guard;
  any SSRF-like vector inside the container could trip it. (Earlier
  review finding #4.)
- Frontend HLS.js wiring is conventional and correct; recovery sequence
  follows the library's recommended pattern.

## Recommended Improvements
1. **Cache duration probes** in DB once produced.
2. **Make segment duration a config knob** (`preview.hls.segment-seconds`).
3. **Cap stderr per-process** (e.g. last 32 KB rolling).
4. **Add ffmpeg-process timeout for remux** (e.g. 4 h).
5. **Persist player `currentTime` per `davItemId`** in `localStorage`.
6. **Replace the extension whitelist** with a probe-based capability hint
   (does the source's primary video codec play in this browser? if no →
   force remux).
7. **Surface a buffering spinner** on `WAITING` HTML5 events.
