using NzbWebDAV.Services;
using Xunit;

namespace NzbWebDAV.Tests;

/// <summary>
/// Pure decision logic for the tolerant health check. The tier-based consecutive-gap tolerance,
/// the cumulative backstop, the critical-location rule, and the shared container-fragility
/// resolver are exercised without any network I/O.
/// </summary>
public class HealthCheckThresholdTests
{
    private const long Mb = 1024 * 1024;

    [Theory]
    [InlineData(0, 100, 1, 1, true)]   // first segment (container header)
    [InlineData(99, 100, 1, 1, true)]  // last segment (container footer)
    [InlineData(50, 100, 1, 1, false)] // middle segment
    public void IsCriticalIndex_SingleFileBoundaries(int index, int total, int prefix, int suffix, bool expected)
    {
        Assert.Equal(expected, HealthCheckThreshold.IsCriticalIndex(index, total, prefix, suffix));
    }

    [Fact]
    public void IsCriticalIndex_MultipartUsesPartBoundaries()
    {
        // 20 segments; first part = 5 segments, last part = 5 segments.
        Assert.True(HealthCheckThreshold.IsCriticalIndex(0, 20, 5, 5));
        Assert.True(HealthCheckThreshold.IsCriticalIndex(4, 20, 5, 5));
        Assert.False(HealthCheckThreshold.IsCriticalIndex(5, 20, 5, 5));
        Assert.True(HealthCheckThreshold.IsCriticalIndex(15, 20, 5, 5));
        Assert.True(HealthCheckThreshold.IsCriticalIndex(19, 20, 5, 5));
        Assert.False(HealthCheckThreshold.IsCriticalIndex(10, 20, 5, 5));
    }

    [Fact]
    public void EstimateMissingBytes_UsesAverageSegmentSize()
    {
        Assert.Equal(300L, HealthCheckThreshold.EstimateMissingBytes(3, 1000, 10));
        Assert.Equal(0L, HealthCheckThreshold.EstimateMissingBytes(3, 0, 10));
        Assert.Equal(0L, HealthCheckThreshold.EstimateMissingBytes(3, 1000, 0));
        Assert.Equal(0L, HealthCheckThreshold.EstimateMissingBytes(0, 1000, 10));
    }

    [Theory]
    [InlineData(ContainerFragilityTier.Resilient, 3, 3)]
    [InlineData(ContainerFragilityTier.Resilient, 10, 10)]
    [InlineData(ContainerFragilityTier.Standard, 3, 2)]   // capped at 2
    [InlineData(ContainerFragilityTier.Standard, 1, 1)]   // below the 2 cap
    [InlineData(ContainerFragilityTier.Unknown, 3, 0)]    // any missing is bad
    [InlineData(ContainerFragilityTier.Resilient, -5, 0)] // negative configured → 0
    public void EffectiveMaxMissingSegments_MapsTierToCap(ContainerFragilityTier tier, int configured, int expected)
    {
        Assert.Equal(expected, HealthCheckThreshold.EffectiveMaxMissingSegments(tier, configured));
    }

    [Theory]
    [InlineData(ContainerFragilityTier.Resilient, 3, 12)] // 3 × 4
    [InlineData(ContainerFragilityTier.Standard, 3, 8)]   // 2 × 4
    [InlineData(ContainerFragilityTier.Unknown, 3, 0)]    // 0 × 4
    public void EffectiveCumulativeMissingSegments_IsMultiplierOfTierCap(ContainerFragilityTier tier, int configured, int expected)
    {
        Assert.Equal(expected, HealthCheckThreshold.EffectiveCumulativeMissingSegments(tier, configured));
    }

    [Fact]
    public void ConsecutiveRunAt_MeasuresRunLength()
    {
        var flags = new bool[10];
        flags[3] = flags[4] = flags[5] = true;

        Assert.Equal(3, HealthCheckThreshold.ConsecutiveRunAt(flags, 3)); // start of run
        Assert.Equal(3, HealthCheckThreshold.ConsecutiveRunAt(flags, 4)); // middle of run
        Assert.Equal(3, HealthCheckThreshold.ConsecutiveRunAt(flags, 5)); // end of run
        Assert.Equal(0, HealthCheckThreshold.ConsecutiveRunAt(flags, 0)); // not missing
        Assert.Equal(0, HealthCheckThreshold.ConsecutiveRunAt(flags, 9)); // not missing
        Assert.Equal(0, HealthCheckThreshold.ConsecutiveRunAt(flags, -1)); // out of range
        Assert.Equal(0, HealthCheckThreshold.ConsecutiveRunAt(flags, 10)); // out of range
    }

    [Fact]
    public void IsBad_CriticalLocationAlwaysBad()
    {
        var outcome = new SegmentHealthOutcome
        {
            TotalSegments = 1000,
            CheckedSegments = 1,
            MissingSegments = 1,
            MaxConsecutiveMissing = 1,
            MissingBytes = 1,
            TotalBytes = 1000L * Mb,
            CriticalLocationMissing = true,
            CriticalIndex = 0
        };
        // Even with generous caps, header/footer damage is fatal.
        Assert.True(HealthCheckThreshold.IsBad(outcome, 100, 400));
    }

    [Fact]
    public void IsBad_ConsecutiveRunIsPrimarySignal()
    {
        var baseOutcome = new SegmentHealthOutcome
        {
            TotalSegments = 10000,
            CheckedSegments = 10000,
            MissingBytes = 0,
            TotalBytes = 0,
            CriticalLocationMissing = false
        };

        // Resilient: consecutive cap 3, cumulative cap 12.
        // A 4-segment contiguous gap is bad even though total missing is only 4.
        Assert.True(HealthCheckThreshold.IsBad(baseOutcome with { MissingSegments = 4, MaxConsecutiveMissing = 4 }, 3, 12));

        // Four scattered single-segment holes are fine (longest run is 1).
        Assert.False(HealthCheckThreshold.IsBad(baseOutcome with { MissingSegments = 4, MaxConsecutiveMissing = 1 }, 3, 12));
    }

    [Fact]
    public void IsBad_CumulativeBackstopCatchesScatteredDamage()
    {
        var baseOutcome = new SegmentHealthOutcome
        {
            TotalSegments = 10000,
            CheckedSegments = 10000,
            MissingBytes = 0,
            TotalBytes = 0,
            CriticalLocationMissing = false
        };

        // Rolling takedown: 13 scattered single-segment holes (no long run) exceeds cumulative 12.
        Assert.True(HealthCheckThreshold.IsBad(baseOutcome with { MissingSegments = 13, MaxConsecutiveMissing = 1 }, 3, 12));
        Assert.False(HealthCheckThreshold.IsBad(baseOutcome with { MissingSegments = 12, MaxConsecutiveMissing = 1 }, 3, 12));
    }

    [Fact]
    public void IsBad_UnknownTierAnyMissingIsBad()
    {
        var outcome = new SegmentHealthOutcome
        {
            TotalSegments = 10000,
            CheckedSegments = 10000,
            MissingSegments = 1,
            MaxConsecutiveMissing = 1,
            MissingBytes = 0,
            TotalBytes = 0,
            CriticalLocationMissing = false
        };
        Assert.True(HealthCheckThreshold.IsBad(outcome, 0, 0));
        Assert.False(HealthCheckThreshold.IsBad(outcome with { MissingSegments = 0, MaxConsecutiveMissing = 0 }, 0, 0));
    }

    [Theory]
    [InlineData(@"{""format_name"":""matroska,webm""}", ContainerFragilityTier.Resilient)]
    [InlineData(@"{""format_name"":""mpegts""}", ContainerFragilityTier.Resilient)]
    [InlineData(@"{""format_name"":""mov,mp4,m4a"",""__nzbdav_mp4_layout"":""faststart""}", ContainerFragilityTier.Standard)]
    [InlineData(@"{""format_name"":""avi""}", ContainerFragilityTier.Standard)]
    [InlineData(@"{""format_name"":""mov,mp4"",""__nzbdav_mp4_layout"":""moov-at-end""}", ContainerFragilityTier.Unknown)]
    [InlineData(@"{""format_name"":""mov,mp4"",""__nzbdav_mp4_layout"":""fragmented""}", ContainerFragilityTier.Resilient)]
    [InlineData(@"{""format_name"":""wav""}", ContainerFragilityTier.Unknown)]
    [InlineData("", ContainerFragilityTier.Unknown)]
    public void ContainerFragilityTierResolver_ClassifiesContainers(string mediaInfoJson, ContainerFragilityTier expected)
    {
        Assert.Equal(expected, ContainerFragilityTierResolver.Resolve(mediaInfoJson));
    }

    [Fact]
    public void GetMissingPercent_ComputesPercentage()
    {
        var outcome = new SegmentHealthOutcome
        {
            TotalSegments = 10,
            MissingSegments = 3,
            MissingBytes = 300,
            TotalBytes = 1000
        };
        Assert.Equal(30.0, HealthCheckThreshold.GetMissingPercent(outcome), 1);
    }
}
