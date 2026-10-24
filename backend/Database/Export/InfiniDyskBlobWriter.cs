using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MemoryPack;
using Microsoft.Data.Sqlite;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;
using Serilog;
using ZstdSharp;

namespace NzbWebDAV.Database.Export;

/// <summary>
/// Writes the typed streaming-metadata blobs InfiniDysk keeps under <c>blobs/</c>. Its reader
/// prefers these blobs and falls back to the exported EF rows for anything it cannot read, so
/// each blob is best-effort: a row that cannot be mapped faithfully is simply left without a
/// blob id and keeps using the (already exported) legacy row.
/// </summary>
internal static class InfiniDyskBlobWriter
{
    private const int CompressionLevel = 1;

    /// <summary>Same control characters our value converters strip after decompression.</summary>
    private static readonly Regex ControlCharacters =
        new("[\u0000-\u0008\u000B\u000C\u000E-\u001F]", RegexOptions.Compiled);

    /// <summary>
    /// Writes <c>DavNzbFiles</c>, <c>DavRarFiles</c> and <c>DavMultipartFiles</c> payloads as
    /// typed blobs and returns the item ids that now have a usable blob.
    /// </summary>
    public static async Task<IReadOnlySet<Guid>> WriteMetadataBlobsAsync(
        SqliteConnection source,
        string blobsRoot,
        CancellationToken cancellationToken)
    {
        var written = new HashSet<Guid>();
        await WriteNzbFilesAsync(source, blobsRoot, written, cancellationToken).ConfigureAwait(false);
        await WriteRarFilesAsync(source, blobsRoot, written, cancellationToken).ConfigureAwait(false);
        await WriteMultipartFilesAsync(source, blobsRoot, written, cancellationToken).ConfigureAwait(false);

        if (written.Count > 0)
            Log.Warning("[InfiniDyskExport] Wrote {Count} typed file blobs to {Root}", written.Count, blobsRoot);

        return written;
    }

    private static async Task WriteNzbFilesAsync(
        SqliteConnection source,
        string blobsRoot,
        HashSet<Guid> written,
        CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand();
        command.CommandText = """
            SELECT n."Id", n."SegmentIds", n."SegmentSizes", n."SegmentFallbacks", i."FileSize"
            FROM "DavNzbFiles" n
            LEFT JOIN "DavItems" i ON i."Id" = n."Id"
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (TryParseGuid(reader.GetValue(0)) is not { } id) continue;

            try
            {
                // The EF converter for SegmentIds serialises the array itself, so the column is
                // `["id", ...]` rather than an object wrapper.
                var segmentIds = JsonSerializer.Deserialize<string[]>(DecompressJson(reader.GetValue(1)) ?? "null");
                if (segmentIds is not { Length: > 0 }) continue;

                var fileSize = reader.GetValue(4) is long size ? size : (long?)null;
                var ranges = BuildRanges(0, ReadSegmentSizes(reader.GetValue(2)), fileSize);

                var contract = new InfiniDyskDavNzbFileContract
                {
                    Id = id,
                    SegmentIds = segmentIds,
                    SegmentByteRanges = ranges,
                    SegmentByteRangesTrusted = ranges is null ? null : true,
                    SegmentFallbackIds = ExpandFallbacks(ReadFallbacks(reader.GetValue(3)), segmentIds.Length),
                };

                if (await WriteBlobAsync(blobsRoot, id, MemoryPackSerializer.Serialize(contract), cancellationToken)
                        .ConfigureAwait(false))
                {
                    written.Add(id);
                }
            }
            catch (Exception e) when (IsRecoverable(e))
            {
                Log.Warning("[InfiniDyskExport] Skipping file blob {Id} (DavNzbFiles): {Error}", id, e.Message);
            }
        }
    }

    private static async Task WriteRarFilesAsync(
        SqliteConnection source,
        string blobsRoot,
        HashSet<Guid> written,
        CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand();
        command.CommandText = """SELECT "Id", "RarParts" FROM "DavRarFiles" """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (TryParseGuid(reader.GetValue(0)) is not { } id) continue;

            try
            {
                var parts = JsonSerializer.Deserialize<DavRarFile.RarPart[]>(DecompressJson(reader.GetValue(1)) ?? "null");
                if (parts is not { Length: > 0 }) continue;

                // ObfuscationKey has no counterpart in the destination model. Emitting a blob
                // without it would change behaviour versus the exported row, so leave these
                // items on the database fallback instead.
                if (parts.Any(x => x.ObfuscationKey is { Length: > 0 })) continue;

                var contract = new InfiniDyskDavRarFileContract
                {
                    Id = id,
                    RarParts = parts.Select(part =>
                    {
                        var ranges = BuildRanges(0, part.SegmentSizes, part.PartSize);
                        return new InfiniDyskRarPartContract
                        {
                            SegmentIds = part.SegmentIds,
                            PartSize = part.PartSize,
                            Offset = part.Offset,
                            ByteCount = part.ByteCount,
                            SegmentByteRanges = ranges,
                            SegmentByteRangesTrusted = ranges is null ? null : true,
                        };
                    }).ToArray(),
                };

                if (await WriteBlobAsync(blobsRoot, id, MemoryPackSerializer.Serialize(contract), cancellationToken)
                        .ConfigureAwait(false))
                {
                    written.Add(id);
                }
            }
            catch (Exception e) when (IsRecoverable(e))
            {
                Log.Warning("[InfiniDyskExport] Skipping file blob {Id} (DavRarFiles): {Error}", id, e.Message);
            }
        }
    }

    private static async Task WriteMultipartFilesAsync(
        SqliteConnection source,
        string blobsRoot,
        HashSet<Guid> written,
        CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand();
        command.CommandText = """SELECT "Id", "Metadata" FROM "DavMultipartFiles" """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (TryParseGuid(reader.GetValue(0)) is not { } id) continue;

            try
            {
                var meta = JsonSerializer.Deserialize<DavMultipartFile.Meta>(DecompressJson(reader.GetValue(1)) ?? "null");
                if (meta?.FileParts is not { Length: > 0 } fileParts) continue;

                // See the RAR note above: fork-only state must not be silently dropped.
                if (meta.ObfuscationKey is { Length: > 0 }) continue;

                var contract = new InfiniDyskDavMultipartFileContract
                {
                    Id = id,
                    Metadata = new InfiniDyskMultipartMetaContract
                    {
                        AesParams = MapAesParams(meta.AesParams),
                        IsLazy = false,
                        PathInArchive = null,
                        ArchivePassword = null,
                        FileParts = fileParts.Select(MapFilePart).ToArray(),
                    },
                };

                if (await WriteBlobAsync(blobsRoot, id, MemoryPackSerializer.Serialize(contract), cancellationToken)
                        .ConfigureAwait(false))
                {
                    written.Add(id);
                }
            }
            catch (Exception e) when (IsRecoverable(e))
            {
                Log.Warning("[InfiniDyskExport] Skipping file blob {Id} (DavMultipartFiles): {Error}", id, e.Message);
            }
        }
    }

    private static InfiniDyskFilePartContract MapFilePart(DavMultipartFile.FilePart part)
    {
        LongRange? segmentRange = part.SegmentIdByteRange;
        LongRange? partRange = part.FilePartByteRange;

        // A blob is only written when both ranges survived deserialization; the destination
        // treats them as non-nullable, so substituting defaults would be worse than falling
        // back to the row.
        if (segmentRange is null || partRange is null)
            throw new JsonException("Missing segment/file-part byte range.");

        var ranges = BuildRanges(segmentRange.StartInclusive, part.SegmentSizes, segmentRange.Count);

        return new InfiniDyskFilePartContract
        {
            SegmentIds = part.SegmentIds,
            SegmentIdByteRange = new InfiniDyskLongRangeContract
            {
                StartInclusive = segmentRange.StartInclusive,
                EndExclusive = segmentRange.EndExclusive,
            },
            FilePartByteRange = new InfiniDyskLongRangeContract
            {
                StartInclusive = partRange.StartInclusive,
                EndExclusive = partRange.EndExclusive,
            },
            SegmentByteRanges = ranges,
            SegmentByteRangesTrusted = ranges is null ? null : true,
            SegmentFallbackIds = ExpandFallbacks(part.SegmentFallbacks, part.SegmentIds.Length),
            IsSplitAfter = null,
        };
    }

    private static InfiniDyskAesParamsContract? MapAesParams(AesParams? aesParams) =>
        aesParams is null
            ? null
            : new InfiniDyskAesParamsContract
            {
                DecodedSize = aesParams.DecodedSize,
                Iv = aesParams.Iv,
                Key = aesParams.Key,
            };

    /// <summary>
    /// Rebuilds per-segment absolute byte ranges from stored yEnc part sizes. Ranges are only
    /// produced when their total matches an independently known size, mirroring the guard our
    /// own seek path uses; otherwise null keeps InfiniDysk on header-probed seeking.
    /// </summary>
    private static InfiniDyskLongRangeContract[]? BuildRanges(long start, long[]? sizes, long? expectedTotal)
    {
        if (sizes is not { Length: > 0 } || expectedTotal is not { } total || total <= 0) return null;
        if (sizes.Any(size => size <= 0)) return null;

        long sum = 0;
        foreach (var size in sizes) sum += size;
        if (sum != total) return null;

        var ranges = new InfiniDyskLongRangeContract[sizes.Length];
        var offset = start;
        for (var i = 0; i < sizes.Length; i++)
        {
            ranges[i] = new InfiniDyskLongRangeContract
            {
                StartInclusive = offset,
                EndExclusive = offset + sizes[i],
            };
            offset += sizes[i];
        }

        return ranges;
    }

    /// <summary>Aligns our sparse index→alternatives map with the destination's per-segment array.</summary>
    private static string[][]? ExpandFallbacks(Dictionary<int, string[]>? fallbacks, int segmentCount)
    {
        if (fallbacks is not { Count: > 0 } || segmentCount <= 0) return null;

        var expanded = new string[segmentCount][];
        var any = false;
        foreach (var (index, alternatives) in fallbacks)
        {
            if (index < 0 || index >= segmentCount || alternatives is not { Length: > 0 }) continue;
            expanded[index] = alternatives;
            any = true;
        }

        return any ? expanded : null;
    }

    /// <summary>Our <c>SegmentSizes</c> column is a raw little-endian <c>long[]</c> blob.</summary>
    private static long[]? ReadSegmentSizes(object? value)
    {
        if (value is not byte[] { Length: > 0 } bytes || bytes.Length % sizeof(long) != 0) return null;
        var sizes = new long[bytes.Length / sizeof(long)];
        Buffer.BlockCopy(bytes, 0, sizes, 0, bytes.Length);
        return sizes;
    }

    internal static string GetBlobPath(string blobsRoot, Guid id)
    {
        // InfiniDysk: {root}/{first2}/{next2}/{guid}, derived from the hyphen-free lowercase form.
        var noHyphens = id.ToString("N");
        return Path.Join(blobsRoot, noHyphens[..2], noHyphens.Substring(2, 2), id.ToString());
    }

    private static async Task<bool> WriteBlobAsync(
        string blobsRoot,
        Guid id,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        using var compressor = new Compressor(CompressionLevel);
        var compressed = compressor.Wrap(payload).ToArray();

        var path = GetBlobPath(blobsRoot, id);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        await File.WriteAllBytesAsync(path, compressed, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string? DecompressJson(object? stored) =>
        stored is string value && value.Length > 0
            ? ControlCharacters.Replace(CompressionUtil.Decompress(value), string.Empty)
            : null;

    /// <summary>Our <c>SegmentFallbacks</c> column is a bare JSON object of index → message ids.</summary>
    private static Dictionary<int, string[]>? ReadFallbacks(object? stored)
    {
        var json = DecompressJson(stored);
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<Dictionary<int, string[]>>(json);
    }

    private static Guid? TryParseGuid(object? value)
    {
        if (value is Guid guid) return guid;
        return Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed)
            ? parsed
            : null;
    }

    private static bool IsRecoverable(Exception e) =>
        e is JsonException or MemoryPackSerializationException or IOException
            or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
