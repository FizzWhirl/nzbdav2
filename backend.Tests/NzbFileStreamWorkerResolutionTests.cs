using NzbWebDAV.Streams;
using Xunit;

namespace NzbWebDAV.Tests;

/// <summary>
/// Tests for <see cref="NzbFileStream.ResolveDirectBufferedStreamParameters"/>, the pure helper
/// that decides the worker count / buffer size for a direct <see cref="BufferedSegmentStream"/>.
/// The production path in <c>GetCombinedStream</c> shares this exact arithmetic.
/// </summary>
public class NzbFileStreamWorkerResolutionTests
{
    /// <summary>
    /// Bounded HTTP Range reads (rclone vfs-cache 32MB chunks behind Plex) must resolve to the
    /// small worker set regardless of whether a scarce full buffered-stream slot was acquired.
    /// If a bounded 52-segment chunk ran the full configured worker count (e.g. 120), parallel
    /// chunks would exhaust the streaming-permit semaphore and the provider connection pool.
    /// </summary>
    [Theory]
    [InlineData(true)]   // slot acquired
    [InlineData(false)]  // no slot
    public void BoundedRead_ResolvesToSmallWorkerSet_RegardlessOfSlot(bool slotAcquired)
    {
        var (workerCount, bufferSize) = NzbFileStream.ResolveDirectBufferedStreamParameters(
            concurrentConnections: 120,
            segmentCount: 1865,
            configuredBufferSize: 20,
            hasRequestedEndByte: true,
            slotAcquired: slotAcquired);

        // min(min(120, 1865), 4) clamped to [1, 4] => 4; buffer clamp(20, 8, 16) => 16.
        Assert.Equal(4, workerCount);
        Assert.Equal(16, bufferSize);
    }

    [Fact]
    public void UnboundedRead_WithSlotAcquired_ResolvesToFullConcurrentConnections()
    {
        var (workerCount, bufferSize) = NzbFileStream.ResolveDirectBufferedStreamParameters(
            concurrentConnections: 120,
            segmentCount: 1865,
            configuredBufferSize: 20,
            hasRequestedEndByte: false,
            slotAcquired: true);

        Assert.Equal(120, workerCount);
        Assert.Equal(20, bufferSize);
    }

    [Fact]
    public void UnboundedRead_WithoutSlot_SmallSegmentCount_KeepsFallbackBehavior()
    {
        // No slot + small segment count resolves to the legacy range-reliability small worker set:
        // min(min(120, 2), 4) clamped to [1, 4] => 2; buffer clamp(20, 4, 16) => 16.
        var (workerCount, bufferSize) = NzbFileStream.ResolveDirectBufferedStreamParameters(
            concurrentConnections: 120,
            segmentCount: 2,
            configuredBufferSize: 20,
            hasRequestedEndByte: false,
            slotAcquired: false);

        Assert.Equal(2, workerCount);
        Assert.Equal(16, bufferSize);
    }

    [Fact]
    public void BoundedRead_SegmentCountBelowWorkerCap_UsesSegmentCount()
    {
        // A 3-segment bounded chunk cannot use more workers than it has segments.
        var (workerCount, _) = NzbFileStream.ResolveDirectBufferedStreamParameters(
            concurrentConnections: 120,
            segmentCount: 3,
            configuredBufferSize: 20,
            hasRequestedEndByte: true,
            slotAcquired: false);

        Assert.Equal(3, workerCount);
    }

    [Fact]
    public void BoundedRead_ConcurrentConnectionsBelowFour_UsesConcurrentConnections()
    {
        var (workerCount, bufferSize) = NzbFileStream.ResolveDirectBufferedStreamParameters(
            concurrentConnections: 2,
            segmentCount: 100,
            configuredBufferSize: 20,
            hasRequestedEndByte: true,
            slotAcquired: false);

        Assert.Equal(2, workerCount);
        Assert.Equal(16, bufferSize); // clamp(20, 4, 16)
    }

    [Fact]
    public void BoundedRead_ZeroConcurrentConnections_ClampsToAtLeastOneWorker()
    {
        // Guard against divide/zero or negative clamp bugs: the worker floor is 1.
        var (workerCount, bufferSize) = NzbFileStream.ResolveDirectBufferedStreamParameters(
            concurrentConnections: 0,
            segmentCount: 100,
            configuredBufferSize: 20,
            hasRequestedEndByte: true,
            slotAcquired: false);

        Assert.Equal(1, workerCount);
        Assert.Equal(16, bufferSize); // clamp(20, 2, 16)
    }
}
