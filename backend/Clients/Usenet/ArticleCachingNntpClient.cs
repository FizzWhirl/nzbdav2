using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Streams;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// Per-queue-item INntpClient wrapper that caches decoded segments to temp files.
/// Eliminates redundant network fetches when the same segment is read in multiple
/// queue processing steps (e.g., Step 1 first-segment fetch reused by Step 2 RAR parsing).
/// Short-lived: deletes all cached data on disposal.
/// </summary>
public class ArticleCachingNntpClient : WrappingNntpClient
{
    private readonly string _cacheDir = Directory.CreateTempSubdirectory("nzbdav-queue-cache-").FullName;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, CacheEntry> _cachedSegments = new();

    private record CacheEntry(UsenetYencHeader YencHeaders, UsenetArticleHeaders? ArticleHeaders);

    public ArticleCachingNntpClient(INntpClient usenetClient, bool leaveOpen = true)
        : base(usenetClient)
    {
        _leaveOpen = leaveOpen;
    }

    private readonly bool _leaveOpen;

    public override async Task<YencHeaderStream> GetSegmentStreamAsync(
        string segmentId, bool includeHeaders, CancellationToken ct)
    {
        var semaphore = _pendingRequests.GetOrAdd(segmentId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // Check if already cached
            if (_cachedSegments.TryGetValue(segmentId, out var existingEntry))
            {
                return ReadFromCache(segmentId, existingEntry);
            }

            // Fetch from usenet and cache
            var response = await base.GetSegmentStreamAsync(segmentId, includeHeaders, ct).ConfigureAwait(false);

            // Read yenc headers before consuming the stream
            var yencHeaders = response.Header;
            var articleHeaders = response.ArticleHeaders;

            // Cache the decoded stream to disk
            var cachePath = GetCachePath(segmentId);
            await using (var fileStream = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                await response.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            }
            await response.DisposeAsync().ConfigureAwait(false);

            // Store cache entry
            _cachedSegments.TryAdd(segmentId, new CacheEntry(yencHeaders, articleHeaders));

            // Return a new stream from the cached file
            return ReadFromCache(segmentId, new CacheEntry(yencHeaders, articleHeaders));
        }
        finally
        {
            semaphore.Release();
        }
    }

    public override Task<UsenetYencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken cancellationToken)
    {
        // Return cached yenc headers if available (avoids network round-trip for interpolation search)
        return _cachedSegments.TryGetValue(segmentId, out var existingEntry)
            ? Task.FromResult(existingEntry.YencHeaders)
            : base.GetSegmentYencHeaderAsync(segmentId, cancellationToken);
    }

    private YencHeaderStream ReadFromCache(string segmentId, CacheEntry entry)
    {
        var cachePath = GetCachePath(segmentId);
        var fileStream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        return new YencHeaderStream(entry.YencHeaders, entry.ArticleHeaders, fileStream);
    }

    private string GetCachePath(string segmentId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(segmentId));
        var filename = Convert.ToHexString(hash);
        return Path.Combine(_cacheDir, filename);
    }

    public new void Dispose()
    {
        if (!_leaveOpen)
            base.Dispose();

        foreach (var semaphore in _pendingRequests.Values)
            semaphore.Dispose();
        _pendingRequests.Clear();
        _cachedSegments.Clear();

        // Clean up cache directory in background
        Task.Run(async () => await DeleteCacheDir(_cacheDir));
        GC.SuppressFinalize(this);
    }

    private static async Task DeleteCacheDir(string cacheDir)
    {
        var delay = 1000;
        for (var i = 0; i < 5; i++)
        {
            try
            {
                Directory.Delete(cacheDir, recursive: true);
                return;
            }
            catch (Exception)
            {
                await Task.Delay(delay).ConfigureAwait(false);
                delay = Math.Min(delay * 2, 10000);
            }
        }
    }
}
