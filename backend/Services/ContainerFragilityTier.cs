namespace NzbWebDAV.Services;

/// <summary>
/// How tolerant a media container is to zero-filled (missing) segments mid-stream. Drives both
/// the streaming graceful-degradation cap and the health-check tolerance so "unplayable" means
/// the same thing everywhere.
/// </summary>
public enum ContainerFragilityTier
{
    /// <summary>MKV/WebM/MPEG-TS/fragmented MP4 — use the full configured cap.</summary>
    Resilient = 0,
    /// <summary>Plain MP4/MOV/AVI/WMV/FLV — hard-cap at min(configured, 2).</summary>
    Standard = 1,
    /// <summary>Unknown container or non-video file — hard-cap at 0 (truncate immediately).</summary>
    Unknown = 2,
}

public static class ContainerFragilityTierResolver
{
    /// <summary>
    /// Resolves the fragility tier from ffprobe JSON (DavItem.MediaInfo). Pure — no DB access —
    /// so both <see cref="Streams.BufferedSegmentStream"/> and the health check share one rule.
    /// </summary>
    public static ContainerFragilityTier Resolve(string? mediaInfoJson)
    {
        if (string.IsNullOrWhiteSpace(mediaInfoJson)) return ContainerFragilityTier.Unknown;

        // Extract format_name from the ffprobe JSON. We avoid full JSON parsing to keep this cheap.
        // ffprobe emits e.g. "format_name": "matroska,webm" or "format_name": "mov,mp4,m4a,3gp,3g2,mj2".
        var lower = mediaInfoJson.ToLowerInvariant();
        const string key = "\"format_name\"";
        var keyIdx = lower.IndexOf(key, StringComparison.Ordinal);
        if (keyIdx < 0) return ContainerFragilityTier.Unknown;
        var colonIdx = lower.IndexOf(':', keyIdx + key.Length);
        if (colonIdx < 0) return ContainerFragilityTier.Unknown;
        var quoteStart = lower.IndexOf('"', colonIdx + 1);
        if (quoteStart < 0) return ContainerFragilityTier.Unknown;
        var quoteEnd = lower.IndexOf('"', quoteStart + 1);
        if (quoteEnd < 0) return ContainerFragilityTier.Unknown;
        var formatName = lower.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);

        // Resilient containers — all explicitly designed for streaming or with strong
        // resync semantics (cluster boundaries, fragment boxes, packet sync bytes).
        if (formatName.Contains("matroska") || formatName.Contains("webm")
            || formatName.Contains("mpegts") || formatName.Contains("mpegtsraw"))
        {
            return ContainerFragilityTier.Resilient;
        }

        // Standard tier — recoverable in the best case, catastrophic if structural
        // metadata is hit. We don't try to detect moov-at-end vs moov-at-start here
        // (that would require deeper parsing); we just lower the ceiling.
        if (formatName.Contains("mp4") || formatName.Contains("mov")
            || formatName.Contains("m4a") || formatName.Contains("3gp")
            || formatName.Contains("avi") || formatName.Contains("asf")
            || formatName.Contains("wmv") || formatName.Contains("flv"))
        {
            // Fragmented MP4 (moof boxes per fragment) is highly resilient — promote to Resilient tier.
            // moov-at-end MP4/MOV is catastrophic — losing the moov box means the whole file is
            // unplayable. Demote to Unknown tier (cap=0). The "__nzbdav_mp4_layout" field is
            // injected by MediaAnalysisService.TryAddMp4LayoutAnnotationAsync.
            const string layoutKey = "\"__nzbdav_mp4_layout\"";
            var layoutKeyIdx = lower.IndexOf(layoutKey, StringComparison.Ordinal);
            if (layoutKeyIdx >= 0)
            {
                var lc = lower.IndexOf(':', layoutKeyIdx + layoutKey.Length);
                if (lc >= 0)
                {
                    var lq = lower.IndexOf('"', lc + 1);
                    var le = lq >= 0 ? lower.IndexOf('"', lq + 1) : -1;
                    if (lq >= 0 && le >= 0)
                    {
                        var layout = lower.Substring(lq + 1, le - lq - 1);
                        if (layout == "moov-at-end") return ContainerFragilityTier.Unknown;
                        if (layout == "fragmented") return ContainerFragilityTier.Resilient;
                        // "faststart" or "unknown" → fall through to Standard.
                    }
                }
            }
            return ContainerFragilityTier.Standard;
        }

        return ContainerFragilityTier.Unknown;
    }
}
