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
    // Surface the FIRST few real exceptions loudly so users / diagnostics can see why a migration
    // is silently failing. After this many we drop back to Debug to avoid log spam.
    private const int VerboseFailureCap = 10;
    private static int _verboseFailures;

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
    /// The first <see cref="VerboseFailureCap"/> failures are logged at Warning with full
    /// exception type + first bytes hex so the actual root cause is visible in container logs.
    /// </summary>
    public static async Task<T?> TryReadAsync<T>(string blobPath, CancellationToken ct = default)
        where T : class
    {
        if (!File.Exists(blobPath)) return null;

        byte[]? rawHead = null;
        byte[]? decompressed = null;
        var stage = "open";
        try
        {
            await using var fileStream = File.OpenRead(blobPath);

            // Capture first 32 raw bytes for diagnostics before we attempt decompression.
            if (fileStream.Length > 0)
            {
                rawHead = new byte[Math.Min(32, fileStream.Length)];
                var read = await fileStream.ReadAsync(rawHead.AsMemory(), ct).ConfigureAwait(false);
                if (read < rawHead.Length) Array.Resize(ref rawHead, read);
                fileStream.Position = 0;
            }

            stage = "decompress";
            await using var decompress = new DecompressionStream(fileStream);
            using var ms = new MemoryStream();
            await decompress.CopyToAsync(ms, ct).ConfigureAwait(false);
            decompressed = ms.ToArray();

            stage = "deserialize";
            return MemoryPackSerializer.Deserialize<T>(decompressed);
        }
        catch (Exception ex)
        {
            var n = Interlocked.Increment(ref _verboseFailures);
            if (n <= VerboseFailureCap)
            {
                Log.Warning(
                    "[BlobStoreReader] FAIL #{N} type={ContractType} stage={Stage} blob={Path} " +
                    "raw_head={RawHead} decompressed_len={DecLen} decompressed_head={DecHead} " +
                    "exception={ExType}: {Error}",
                    n, typeof(T).Name, stage, blobPath,
                    rawHead is null ? "<empty>" : Convert.ToHexString(rawHead),
                    decompressed?.Length ?? -1,
                    decompressed is null ? "<n/a>" : Convert.ToHexString(decompressed.AsSpan(0, Math.Min(32, decompressed.Length))),
                    ex.GetType().FullName, ex.Message);
            }
            else
            {
                Log.Debug("[BlobStoreReader] Failed to read blob {Path}: {Error}", blobPath, ex.Message);
            }
            return null;
        }
    }
}
