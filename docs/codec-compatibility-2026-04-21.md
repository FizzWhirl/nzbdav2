# Codec Compatibility Research (2026-04-21)

## Goal
Define a practical playback strategy that maximizes real-world browser/device compatibility for preview playback across mixed source formats.

## Sources Reviewed
- MDN: Web video codec guide
- MDN: Web audio codec guide
- MDN: Media container formats
- MDN: Codecs parameter guide
- MDN: Handling media support issues
- Apple: HLS Authoring Specification for Apple devices
- hls.js README/compatibility notes
- caniuse: MPEG-4/H.264, AAC, AV1

## Key Findings

### 1) Most Compatible Baseline
- Video: H.264/AVC in 8-bit 4:2:0 (`yuv420p`)
- Audio: AAC-LC (`mp4a.40.2`), stereo (2.0), 48 kHz
- Container: MP4 or HLS (TS/fMP4)

Why:
- H.264 is broadly supported across all major browsers and platforms.
- AAC is broadly supported, with caveats in Firefox where decoding can depend on OS codec availability.
- Apple HLS guidance explicitly includes H.264 compatibility recommendations and stereo AAC requirements for broad compatibility scenarios.

### 2) Runtime/Environment Dependency Is Real
- hls.js explicitly states codec support depends on runtime environment.
- Safari has native HLS support and may behave differently than MSE/hls.js playback on Chromium/Firefox.
- AV1 and HEVC support can be partial or hardware-dependent (especially Apple ecosystem and some Windows paths).

Implication:
- Do not assume one codec/container path works uniformly across all browsers.
- A robust solution needs runtime fallback behavior, not just a single static encoding decision.

### 3) Multi-Channel Audio Is Valid but Riskier for Browser Decode
- AAC can carry multi-channel audio, but cross-browser reliability is lower than AAC-LC stereo baseline.
- Apple HLS allows multichannel codecs, but stereo AAC remains the compatibility anchor.

Implication:
- For preview reliability, downmix to stereo even when source is 5.1/7.1.

### 4) HLS Authoring Interop Rules Matter
- Keep segment timestamps/continuity monotonic and boundaries aligned.
- Avoid codec switching mid-stream/discontinuities unless required and signaled correctly.
- Ensure proper MIME types and HLS playlist correctness.

## Recommended Strategy for NzbDav Preview

### Playback Order (Client)
1. Native HLS when browser supports it (`canPlayType(application/vnd.apple.mpegurl)`).
2. hls.js (MSE path).
3. Compatibility remux/transcode fallback (MP4 endpoint).

### Preview Transcode Targets (Server)
Use the same conservative decode target for both HLS segments and remux fallback:
- `-c:v libx264`
- `-pix_fmt yuv420p`
- `-profile:v high`
- `-level:v 4.1`
- `-c:a aac`
- `-profile:a aac_low`
- `-ac 2`
- `-ar 48000`

### Source Codec Handling Policy
- Source video codecs (HEVC/AV1/VP9/MPEG2/others): transcode to H.264 for preview.
- Source audio codecs (E-AC-3/AC-3/DTS/FLAC/Vorbis/Opus/multichannel AAC/etc): transcode/downmix to AAC-LC stereo.
- Container differences (MKV/MOV/TS/AVI/etc): normalize through preview transcode path.

## Acceptance Criteria for "Max Compatibility" Preview
- A sample HLS segment probes as H.264 High + AAC-LC stereo.
- Remux fallback probes as H.264 High + AAC-LC stereo.
- Playback auto-recovers by trying next mode when runtime-specific decode fails.

## Known Caveats (Expected)
- No approach guarantees 100% decode success on every obsolete browser/device.
- HEVC/AV1 native playback remains environment-dependent; therefore they are not safe single-format baselines.
- Firefox AAC behavior can still depend on host OS codec stack.

## Future Enhancements (Optional)
- Add multi-rendition ABR preview ladder (still H.264/AAC baseline) for weak networks.
- Add explicit codec metadata in API/UI so users can see source codec -> preview codec normalization.
- Add telemetry on fallback mode usage to identify problematic client environments.
