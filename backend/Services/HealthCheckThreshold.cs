namespace NzbWebDAV.Services;

/// <summary>
/// Result of a tolerant segment health scan. Missing segments are accumulated instead of
/// failing on the first one, and the scan stops early (fail-fast) once the verdict is bad.
/// </summary>
public sealed record SegmentHealthOutcome
{
    public int TotalSegments { get; init; }
    public int CheckedSegments { get; init; }
    public int MissingSegments { get; init; }

    /// <summary>Longest run of consecutive missing segments encountered during the scan.</summary>
    public int MaxConsecutiveMissing { get; init; }

    /// <summary>Estimated bytes missing, based on average segment size.</summary>
    public long MissingBytes { get; init; }

    /// <summary>Logical file size in bytes. Zero when unknown.</summary>
    public long TotalBytes { get; init; }

    /// <summary>True when a missing segment landed in the critical header/footer region.</summary>
    public bool CriticalLocationMissing { get; init; }

    /// <summary>Index of the first critical-location missing segment, if any.</summary>
    public int? CriticalIndex { get; init; }

    /// <summary>
    /// Segment sizes from a clean HEAD smart sample. Null when the check fell back to STAT
    /// or when the file has tolerated missing segments.
    /// </summary>
    public long[]? SegmentSizes { get; init; }
}

/// <summary>
/// Pure decision logic for the tolerant health check. Kept static and free of I/O so the
/// rules are unit-testable in isolation.
/// </summary>
public static class HealthCheckThreshold
{
    /// <summary>
    /// Cumulative missing-segment backstop as a multiple of the tier's consecutive cap. Catches
    /// "rolling takedowns" that remove scattered articles (no long run) but leave the file
    /// riddled with holes. Consecutive gaps are the primary signal; this is a lenient safety net.
    /// </summary>
    public const int CumulativeMissingMultiplier = 4;

    /// <summary>
    /// Maps the container fragility tier to the maximum run of consecutive missing segments the
    /// health check tolerates before declaring the file bad. This mirrors the streaming layer's
    /// graceful-degradation cap: resilient containers get the full configured cap, standard
    /// containers are capped at 2, and unknown / non-video / moov-at-end files get 0.
    /// </summary>
    public static int EffectiveMaxMissingSegments(ContainerFragilityTier tier, int configuredCap)
    {
        var cap = Math.Max(0, configuredCap);
        return tier switch
        {
            ContainerFragilityTier.Resilient => cap,
            ContainerFragilityTier.Standard => Math.Min(cap, 2),
            _ => 0,
        };
    }

    /// <summary>
    /// Lenient cumulative tolerance: the tier's consecutive cap times
    /// <see cref="CumulativeMissingMultiplier"/>. Always 0 for unknown/non-video files.
    /// </summary>
    public static int EffectiveCumulativeMissingSegments(ContainerFragilityTier tier, int configuredCap)
        => EffectiveMaxMissingSegments(tier, configuredCap) * CumulativeMissingMultiplier;

    /// <summary>
    /// True when <paramref name="index"/> falls inside the critical header prefix or footer
    /// suffix. For a single NzbFile, prefix/suffix are 1 (first/last segment). For
    /// multipart/RAR files they are the segment counts of the first/last part.
    /// </summary>
    public static bool IsCriticalIndex(int index, int totalSegments, int criticalPrefixCount, int criticalSuffixCount)
    {
        if (totalSegments <= 0 || index < 0) return false;
        var effectivePrefix = Math.Clamp(criticalPrefixCount, 0, totalSegments);
        var effectiveSuffix = Math.Clamp(criticalSuffixCount, 0, totalSegments);
        return index < effectivePrefix || index >= totalSegments - effectiveSuffix;
    }

    /// <summary>Estimated missing bytes using the average segment size of the file.</summary>
    public static long EstimateMissingBytes(int missingCount, long totalBytes, int totalSegments)
    {
        if (missingCount <= 0 || totalBytes <= 0 || totalSegments <= 0) return 0;
        var average = Math.Max(1L, totalBytes / totalSegments);
        return missingCount * average;
    }

    /// <summary>
    /// Length of the contiguous run of true flags in <paramref name="missingFlags"/> that
    /// contains <paramref name="index"/>. Returns 0 when that position is not flagged missing.
    /// Pure so the run-length logic is unit-testable.
    /// </summary>
    public static int ConsecutiveRunAt(bool[] missingFlags, int index)
    {
        if (missingFlags is null || index < 0 || index >= missingFlags.Length || !missingFlags[index]) return 0;
        var run = 1;
        for (var i = index - 1; i >= 0 && missingFlags[i]; i--) run++;
        for (var i = index + 1; i < missingFlags.Length && missingFlags[i]; i++) run++;
        return run;
    }

    /// <summary>
    /// Final verdict. Bad if:
    /// - a critical-location segment is missing (container header/footer), or
    /// - the longest contiguous run of missing segments exceeds the tier's consecutive cap, or
    /// - the total number of missing segments exceeds the cumulative backstop.
    /// </summary>
    public static bool IsBad(SegmentHealthOutcome outcome, int maxConsecutiveSegments, int maxCumulativeSegments)
    {
        if (outcome.CriticalLocationMissing) return true;
        if (outcome.MaxConsecutiveMissing > maxConsecutiveSegments) return true;
        if (outcome.MissingSegments > maxCumulativeSegments) return true;
        return false;
    }

    public static double GetMissingPercent(SegmentHealthOutcome outcome)
    {
        if (outcome.TotalBytes <= 0) return 0;
        return outcome.MissingBytes * 100.0 / outcome.TotalBytes;
    }
}
