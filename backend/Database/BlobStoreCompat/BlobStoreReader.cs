using MemoryPack;
using Serilog;
using ZstdSharp;

namespace NzbWebDAV.Database.BlobStoreCompat;

/// <summary>
/// Reads upstream nzbdav-dev BlobStore-era blob files. Each blob is a Zstd-compressed stream
/// (magic <c>28 b5 2f fd</c>) wrapping a MemoryPack-serialized object of type <typeparamref name="T"/>.
/// </summary>
internal static class BlobStoreReader
{
    /// <summary>
    /// Computes the on-disk path for a blob given its FileBlobId and the blobs root directory.
    /// Layout: <c>{blobsRoot}/{first2}/{next2}/{guid}</c> with lowercase hex.
    /// Matches upstream's BlobStore.GetBlobPath.
    /// </summary>
    public static string GetBlobPath(string blobsRoot, Guid blobId)
    {
        var noHyphens = blobId.ToString("N"); // 32 lowercase hex chars
        var first2 = noHyphens[..2];
        var next2 = noHyphens.Substring(2, 2);
        return Path.Combine(blobsRoot, first2, next2, blobId.ToString());
    }

    /// <summary>
    /// Attempts to deserialize a blob file. Returns null and logs a debug message if the file is
    /// missing, unreadable, malformed, or not a valid blob of the expected type.
    /// </summary>
    public static async Task<T?> TryReadAsync<T>(string blobPath, CancellationToken ct = default)
        where T : class
    {
        try
        {
            if (!File.Exists(blobPath)) return null;

            await using var fileStream = File.OpenRead(blobPath);
            await using var decompress = new DecompressionStream(fileStream);
            using var ms = new MemoryStream();
            await decompress.CopyToAsync(ms, ct).ConfigureAwait(false);
            ms.Position = 0;

            return MemoryPackSerializer.Deserialize<T>(ms.ToArray());
        }
        catch (Exception ex)
        {
            Log.Debug("[BlobStoreReader] Failed to read blob {Path}: {Error}", blobPath, ex.Message);
            return null;
        }
    }
}
