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
    /// Extracts the embedded DavItem.Id from a v1 blob WITHOUT a full deserialization.
    ///
    /// All three upstream metadata contracts (UpstreamDavNzbFile, UpstreamDavRarFile,
    /// UpstreamDavMultipartFile) declare <c>[MemoryPackOrder(0)] Guid Id</c> as their first
    /// member. MemoryPack object wire format for a non-null reference type is:
    ///   byte 0  : member count (255 = null reference)
    ///   bytes 1..16 : first member, raw 16 bytes for Guid
    ///   ...     : remaining members
    ///
    /// So we only need to decompress the blob and read 17 bytes to recover the embedded Id.
    /// This is critical for the v1→v2 migration on installs where the FileBlobId column
    /// (which previously stored DavItem.Id → blob filename mapping) was dropped — the embedded
    /// Id is the ONLY remaining link between a blob file and its owning DavItem.
    ///
    /// Returns null if the file is missing, unreadable, the payload is shorter than 17 bytes,
    /// or the leading byte indicates a null reference.
    /// </summary>
    public static async Task<Guid?> TryReadEmbeddedIdAsync(string blobPath, CancellationToken ct = default)
    {
        if (!File.Exists(blobPath)) return null;

        try
        {
            await using var fileStream = File.OpenRead(blobPath);
            await using var decompress = new DecompressionStream(fileStream);

            // We only need the first 17 bytes; bound the decompressed buffer to keep this cheap
            // even on partially valid blobs.
            var buffer = new byte[17];
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = await decompress.ReadAsync(buffer.AsMemory(totalRead), ct).ConfigureAwait(false);
                if (read <= 0) break;
                totalRead += read;
            }

            if (totalRead < 17) return null;
            // 255 = MemoryPack null sentinel; anything else is the member count.
            if (buffer[0] == 255) return null;

            return new Guid(buffer.AsSpan(1, 16));
        }
        catch (Exception ex)
        {
            var n = Interlocked.Increment(ref _verboseFailures);
            if (n <= VerboseFailureCap)
            {
                Log.Warning(
                    "[BlobStoreReader] FAIL #{N} (Id-extract) blob={Path} exception={ExType}: {Error}",
                    n, blobPath, ex.GetType().FullName, ex.Message);
            }
            return null;
        }
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
