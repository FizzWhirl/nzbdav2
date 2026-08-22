using NzbWebDAV.Utils;
using Xunit;

namespace NzbWebDAV.Tests;

public class OrganizedLinksUtilChunkingTests
{
    /// <summary>
    /// Generates <paramref name="count"/> deterministic, distinct GUIDs in a stable order.
    /// </summary>
    private static List<Guid> GenerateDistinctGuids(int count) =>
        Enumerable.Range(0, count)
            .Select(i => Guid.Parse($"00000000-0000-0000-0000-{i:D12}"))
            .ToList();

    [Fact]
    public void ChunkDavItemIds_2500Ids_ProducesBatchesOf800_800_800_100()
    {
        var ids = GenerateDistinctGuids(2500);

        var batches = OrganizedLinksUtil.ChunkDavItemIds(ids).ToList();

        Assert.Equal(4, batches.Count);
        Assert.Equal(new[] { 800, 800, 800, 100 }, batches.Select(b => b.Count));

        // Full coverage, original order preserved, and no id dropped or duplicated.
        Assert.Equal(ids, batches.SelectMany(b => b));
    }

    [Fact]
    public void ChunkDavItemIds_SmallerThanBatchSize_ReturnsSingleBatch()
    {
        var ids = GenerateDistinctGuids(5);

        var batches = OrganizedLinksUtil.ChunkDavItemIds(ids).ToList();

        var batch = Assert.Single(batches);
        Assert.Equal(ids, batch);
    }

    [Fact]
    public void ChunkDavItemIds_EmptySet_ReturnsNoBatches()
    {
        var batches = OrganizedLinksUtil.ChunkDavItemIds(Array.Empty<Guid>()).ToList();

        Assert.Empty(batches);
    }

    [Fact]
    public void ChunkDavItemIds_IsDeterministic_AndPreservesDistinctness()
    {
        var ids = GenerateDistinctGuids(2500);

        var first = OrganizedLinksUtil.ChunkDavItemIds(ids).SelectMany(b => b);
        var second = OrganizedLinksUtil.ChunkDavItemIds(ids).SelectMany(b => b);

        Assert.Equal(first, second);
        Assert.Equal(ids.Count, first.Distinct().Count());
    }

    [Fact]
    public void ChunkedValidation_MergesIdenticallyToSingleInMemoryContains()
    {
        // Simulates the batched FK validation without EF: every even-indexed id is a
        // "valid" DavItem. The chunked union must equal a single in-memory Contains pass.
        var scanned = GenerateDistinctGuids(2500);
        var valid = new HashSet<Guid>(scanned.Where((_, i) => i % 2 == 0));

        var batched = new HashSet<Guid>();
        foreach (var chunk in OrganizedLinksUtil.ChunkDavItemIds(scanned))
        {
            batched.UnionWith(chunk.Where(valid.Contains));
        }

        var single = new HashSet<Guid>(scanned.Where(valid.Contains));

        Assert.True(single.SetEquals(batched));
        Assert.Equal(single.Count, batched.Count);
    }
}
