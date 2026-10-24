using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Database.Export;

/// <summary>Summary of a completed InfiniDysk migration export.</summary>
/// <param name="OutputDirectory">Directory the export was written to.</param>
/// <param name="SqlFilePath">Full path of the generated <c>db.sql</c>.</param>
/// <param name="Bytes">Size of the generated dump.</param>
/// <param name="TableRows">Number of rows written per destination table.</param>
/// <param name="NzbBlobCount">Number of raw NZB blob files written.</param>
/// <param name="FileBlobCount">Number of typed streaming-metadata blob files written.</param>
/// <param name="Warning">Set when the dump cannot be imported through InfiniDysk's upload endpoint.</param>
public sealed record InfiniDyskExportResult(
    string OutputDirectory,
    string SqlFilePath,
    long Bytes,
    IReadOnlyDictionary<string, int> TableRows,
    int NzbBlobCount,
    int FileBlobCount,
    string? Warning = null);

/// <summary>
/// Exports the parts of this database that InfiniDysk can consume, as a logical SQL dump
/// (<c>db.sql</c>) in the exact shape InfiniDysk's Backup &amp; Restore upload accepts.
///
/// The dump is written against <see cref="InfiniDyskExportSchema"/> rather than our own
/// schema, because the two projects diverged after the shared
/// <c>20251113081523_Populate-Usenet-Providers-Config</c> migration. Rows are mapped
/// column-by-column; fork-only tables, columns, and settings are dropped instead of being
/// carried across blindly.
/// </summary>
public static class InfiniDyskExporter
{
    public const string SqlFileName = "db.sql";
    public const string ReadmeFileName = "INFINIDYSK-MIGRATION-README.txt";
    public const string BlobsDirectoryName = "blobs";

    /// <summary>
    /// Control characters our value converters strip after decompression. Re-applied here so
    /// the exported JSON is byte-identical to what our own readers see.
    /// </summary>
    private static readonly Regex ControlCharacters = new("[\u0000-\u0008\u000B\u000C\u000E-\u001F]", RegexOptions.Compiled);

    private static readonly string[] DavItemColumns =
    [
        "Id", "ArrDownloadId", "CreatedAt", "FileBlobId", "FileSize",
        "GeneratedStrmOutputRoot", "GeneratedStrmPath", "GeneratedStrmTarget",
        "GeneratedSymlinkOutputRoot", "GeneratedSymlinkPath", "GeneratedSymlinkTarget",
        "HealthRepairPending", "UrgentRepairFailures", "HistoryItemId", "IdPrefix",
        "LastHealthCheck", "Name", "NextHealthCheck", "NzbBlobId", "ParentId", "Path",
        "ReleaseDate", "SubType", "Type",
    ];

    private static readonly string[] HistoryItemColumns =
    [
        "Id", "ArrDownloadId", "Category", "ContentGroupKey", "CreatedAt", "DownloadDirId",
        "DownloadStatus", "DownloadTimeSeconds", "FailMessage", "FileName", "IndexerName",
        "JobName", "LastPlayedAt", "NzbBlobId", "TotalSegmentBytes",
    ];

    private static readonly string[] HealthCheckResultColumns =
    [
        "Id", "CreatedAt", "DavItemId", "JobName", "Message", "NzbFileName", "Path",
        "RepairStatus", "Result",
    ];

    private static readonly string[] HealthCheckStatColumns =
    [
        "DateStartInclusive", "DateEndExclusive", "Result", "RepairStatus", "Count",
    ];

    /// <summary>
    /// InfiniDysk binds <see cref="Guid"/> parameters as UPPERCASE text, and its
    /// <c>Normalize-Guid-Text-Casing</c> migration (already marked applied in our dump) rewrote
    /// historical lowercase rows for exactly that reason. Normalising here keeps every id
    /// comparable after the restore; blob *file* names stay lowercase because the destination
    /// derives those from a <c>Guid</c>, not from these strings.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> GuidTextColumns = new(StringComparer.Ordinal)
    {
        ["DavItems"] = ["Id", "ParentId", "HistoryItemId", "FileBlobId", "NzbBlobId"],
        ["DavNzbFiles"] = ["Id"],
        ["DavRarFiles"] = ["Id"],
        ["DavMultipartFiles"] = ["Id"],
        ["HistoryItems"] = ["Id", "DownloadDirId", "NzbBlobId"],
        ["HealthCheckResults"] = ["Id", "DavItemId"],
        ["NzbNames"] = ["Id"],
    };

    // Well-known root folder ids from DavItem. These carry their meaning in the id, so the
    // directory SubType can be recovered even if the stored Type column is stale.
    private static readonly Guid NzbsRootId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ContentRootId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid SymlinkRootId = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid IdsRootId = Guid.Parse("00000000-0000-0000-0000-000000000004");

    /// <summary>
    /// InfiniDysk's restore endpoint refuses a main database dump larger than 4 GiB: the controller
    /// sets a matching multipart limit, and exceeding it surfaces as a bare HTTP 500 rather than a
    /// readable error. Knowing the ceiling lets us say so instead of letting the operator discover
    /// it from a failed upload.
    /// </summary>
    public const long MaxUploadBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// Writes <c>db.sql</c> and a <c>blobs/</c> tree into <paramref name="targetDirectory"/>.
    /// </summary>
    /// <param name="targetDirectory">Absolute directory to write into; created if missing.</param>
    /// <param name="compact">
    /// When true, health-check history (<c>HealthCheckResults</c> / <c>HealthCheckStats</c>) is
    /// omitted. Those tables are per-check audit/metrics rows, not migration data — InfiniDysk
    /// re-checks the library itself — and they are usually the bulk of a large dump.
    /// </param>
    /// <param name="progress">Optional human-readable stage reporter.</param>
    /// <param name="cancellationToken">Cancels the run and removes the partial dump.</param>
    public static async Task<InfiniDyskExportResult> ExportAsync(
        string targetDirectory,
        bool compact = false,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        void Report(string message) => progress?.Invoke(message);

        var directory = ValidateTargetDirectory(targetDirectory);
        Directory.CreateDirectory(directory);

        var sqlPath = Path.Join(directory, SqlFileName);
        var tempPath = sqlPath + ".tmp";
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var nzbBlobCount = 0;
        var fileBlobCount = 0;

        Log.Warning("[InfiniDyskExport] Starting export to {Directory} (compact={Compact})", directory, compact);
        Report($"Starting export to {directory}" + (compact ? " (compact)" : string.Empty));

        try
        {
            Report("Opening database...");
            await using var source = await OpenSourceAsync(cancellationToken);

            await ExecuteAsync(source, "BEGIN;", cancellationToken);

            // NZB blobs are real files, not rows, so they are written before the dump. Doing
            // it inside the read transaction keeps blob content and rows on one snapshot.
            Report("Writing NZB blobs...");
            var nzbBlobs = await WriteNzbBlobsAsync(source, directory, cancellationToken);
            Report($"Wrote {nzbBlobs.Count} NZB blob(s)");

            // Same for the typed streaming-metadata blobs InfiniDysk prefers over the EF rows.
            Report("Writing file metadata blobs...");
            var fileBlobs = await InfiniDyskBlobWriter
                .WriteMetadataBlobsAsync(source, Path.Join(directory, BlobsDirectoryName), cancellationToken)
                .ConfigureAwait(false);
            Report($"Wrote {fileBlobs.Count} file metadata blob(s)");

            var providerGeneration = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            nzbBlobCount = nzbBlobs.Count;
            fileBlobCount = fileBlobs.Count;

            Report("Writing db.sql...");

            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024))
            {
                await writer.WriteLineAsync("PRAGMA foreign_keys=OFF;");
                await writer.WriteLineAsync("BEGIN TRANSACTION;");

                foreach (var (_, ddl) in InfiniDyskExportSchema.Tables)
                    await writer.WriteLineAsync(ddl);

                counts["DavItems"] = await WriteRowsAsync(
                    source, writer, "DavItems", DavItemColumns,
                    """
                    SELECT "Id", "IdPrefix", "CreatedAt", "ParentId", "Name", "FileSize",
                           "Type", "Path", "ReleaseDate", "LastHealthCheck", "NextHealthCheck",
                           "HistoryItemId"
                    FROM "DavItems"
                    """,
                    reader => MapDavItem(reader, nzbBlobs, fileBlobs), cancellationToken);
                Report($"Exported {counts["DavItems"]} library item(s)");

                counts["DavNzbFiles"] = await WriteRowsAsync(
                    source, writer, "DavNzbFiles", ["Id", "SegmentIds"],
                    """SELECT "Id", "SegmentIds" FROM "DavNzbFiles" """,
                    reader => CoveredByBlob(reader, fileBlobs)
                        ? null
                        : new object?[] { reader.GetValue(0), UnpackJson(reader.GetValue(1)) },
                    cancellationToken);

                counts["DavRarFiles"] = await WriteRowsAsync(
                    source, writer, "DavRarFiles", ["Id", "RarParts"],
                    """SELECT "Id", "RarParts" FROM "DavRarFiles" """,
                    reader => CoveredByBlob(reader, fileBlobs)
                        ? null
                        : new object?[] { reader.GetValue(0), UnpackJson(reader.GetValue(1)) },
                    cancellationToken);

                counts["DavMultipartFiles"] = await WriteRowsAsync(
                    source, writer, "DavMultipartFiles", ["Id", "Metadata"],
                    """SELECT "Id", "Metadata" FROM "DavMultipartFiles" """,
                    reader => CoveredByBlob(reader, fileBlobs)
                        ? null
                        : new object?[] { reader.GetValue(0), UnpackJson(reader.GetValue(1)) },
                    cancellationToken);

                // Hidden items are fork-only state; InfiniDysk has no column for them, so
                // resurrecting them as visible history entries would be a regression.
                counts["HistoryItems"] = await WriteRowsAsync(
                    source, writer, "HistoryItems", HistoryItemColumns,
                    """
                    SELECT "Id", "CreatedAt", "FileName", "JobName", "Category",
                           "DownloadStatus", "TotalSegmentBytes", "DownloadTimeSeconds",
                           "FailMessage", "DownloadDirId"
                    FROM "HistoryItems"
                    WHERE IFNULL("IsHidden", 0) = 0
                    """,
                    reader => MapHistoryItem(reader, nzbBlobs), cancellationToken);
                Report($"Exported {counts["HistoryItems"]} history row(s)");

                // InfiniDysk serves "Download NZB" and resolves health-check identities through
                // NzbNames, so every blob we wrote needs its original filename recorded.
                counts["NzbNames"] = await WriteNzbNamesAsync(writer, nzbBlobs, cancellationToken);

                // Our HealthResult enum uses 2 for "Skipped"; InfiniDysk uses 2 for "Degraded".
                // Exporting skipped rows raw would relabel them as degraded damage, so drop them.
                // Compact mode drops the tables entirely: they are per-check history, not
                // migration data, and they dominate the size of a large dump.
                if (compact)
                {
                    // Keep the keys present so callers can render "0" rather than a missing row.
                    counts["HealthCheckResults"] = 0;
                    counts["HealthCheckStats"] = 0;
                    Report("Compact mode — skipping health-check history");
                }
                else
                {
                    counts["HealthCheckResults"] = await WriteRowsAsync(
                        source, writer, "HealthCheckResults", HealthCheckResultColumns,
                        """
                        SELECT "Id", "CreatedAt", "DavItemId", "Path", "Result", "RepairStatus", "Message"
                        FROM "HealthCheckResults"
                        WHERE "Result" <> 2
                        """,
                        MapHealthCheckResult, cancellationToken);

                    counts["HealthCheckStats"] = await WriteRowsAsync(
                        source, writer, "HealthCheckStats", HealthCheckStatColumns,
                        """
                        SELECT "DateStartInclusive", "DateEndExclusive", "Result", "RepairStatus", "Count"
                        FROM "HealthCheckStats"
                        WHERE "Result" <> 2
                        """,
                        reader => new object?[]
                        {
                            reader.GetValue(0), reader.GetValue(1), reader.GetValue(2),
                            reader.GetValue(3), reader.GetValue(4),
                        },
                        cancellationToken);
                }

                counts["ConfigItems"] = await WriteRowsAsync(
                    source, writer, "ConfigItems", ["ConfigName", "ConfigValue"],
                    """SELECT "ConfigName", "ConfigValue" FROM "ConfigItems" """,
                    reader => MapConfigItem(reader, providerGeneration), cancellationToken);

                counts["__EFMigrationsHistory"] = await WriteMigrationHistoryAsync(writer, cancellationToken);

                await writer.WriteLineAsync("COMMIT;");
                await writer.FlushAsync(cancellationToken);
            }

            await ExecuteAsync(source, "COMMIT;", CancellationToken.None);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        Report("Finalising...");
        File.Move(tempPath, sqlPath, overwrite: true);
        var length = new FileInfo(sqlPath).Length;
        var warning = length > MaxUploadBytes
            ? $"db.sql is {length / (1024 * 1024 * 1024.0):0.#} GiB, which exceeds InfiniDysk's " +
              $"{MaxUploadBytes / (1024 * 1024 * 1024)} GiB upload ceiling. It cannot be imported by upload; " +
              "re-export with 'Compact' enabled to drop health-check history."
            : null;

        await WriteReadmeAsync(directory, counts, nzbBlobCount, fileBlobCount, compact, warning, cancellationToken);

        Log.Warning(
            "[InfiniDyskExport] Wrote {Path} ({Bytes} bytes, {Items} DavItems, {History} history rows, {NzbBlobs} NZB blobs, {FileBlobs} file blobs)",
            sqlPath, length, counts.GetValueOrDefault("DavItems"), counts.GetValueOrDefault("HistoryItems"),
            nzbBlobCount, fileBlobCount);
        if (warning is not null) Log.Warning("[InfiniDyskExport] {Warning}", warning);
        Report($"Done — wrote {sqlPath} ({length} bytes)");

        return new InfiniDyskExportResult(
            directory, sqlPath, length, counts, nzbBlobCount, fileBlobCount, warning);
    }

    /// <summary>
    /// Resolves and sanity-checks the operator-supplied destination directory. The backend
    /// runs inside the container, so only paths that are actually mounted are useful; the
    /// checks below are about refusing nonsense and pseudo-filesystems, not about sandboxing.
    /// </summary>
    public static string ValidateTargetDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new BadHttpRequestException("An export directory is required.");

        var trimmed = path.Trim();
        if (!Path.IsPathRooted(trimmed))
            throw new BadHttpRequestException(
                $"`{trimmed}` is not an absolute path. Use a path inside a directory mounted into the container, e.g. `/config/infinidysk-export`.");

        var full = Path.GetFullPath(trimmed);

        foreach (var blocked in new[] { "/proc", "/sys", "/dev" })
        {
            if (full.Equals(blocked, StringComparison.Ordinal)
                || full.StartsWith(blocked + "/", StringComparison.Ordinal))
            {
                throw new BadHttpRequestException($"`{full}` is not a usable export location.");
            }
        }

        if (File.Exists(full))
            throw new BadHttpRequestException($"`{full}` already exists and is a file, not a directory.");

        return full;
    }

    private static object?[] MapDavItem(
        SqliteDataReader reader,
        IReadOnlyDictionary<Guid, string> nzbBlobs,
        IReadOnlySet<Guid> fileBlobs)
    {
        var id = reader.GetValue(0);
        var historyItemId = reader.GetValue(11);
        var ourType = Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture);
        var (type, subType) = MapItemType(id, ourType);

        // InfiniDysk points a mounted file at the same NZB blob as the history item it was
        // processed from (FileAggregator.cs: nzbBlobId = MountDirectory.HistoryItemId), so
        // reuse the history id instead of writing a duplicate blob.
        var nzbBlobId = TryParseGuid(historyItemId) is { } blobId && nzbBlobs.ContainsKey(blobId)
            ? historyItemId
            : null;

        // The typed metadata blob is keyed by the DavItem id (fileBlobId: davNzbFile.Id in
        // InfiniDysk's aggregators). Null keeps the destination on the exported EF row.
        var fileBlobId = TryParseGuid(id) is { } selfId && fileBlobs.Contains(selfId) ? id : null;

        return
        [
            id,                              // Id
            null,                            // ArrDownloadId
            reader.GetValue(2),              // CreatedAt
            fileBlobId,                      // FileBlobId
            reader.GetValue(5),              // FileSize
            null, null, null,                // GeneratedStrm*
            null, null, null,                // GeneratedSymlink*
            false,                           // HealthRepairPending
            null,                            // UrgentRepairFailures
            historyItemId,                   // HistoryItemId
            reader.GetValue(1),              // IdPrefix
            reader.GetValue(9),              // LastHealthCheck
            reader.GetValue(4),              // Name
            reader.GetValue(10),             // NextHealthCheck
            nzbBlobId,                       // NzbBlobId
            reader.GetValue(3),              // ParentId
            reader.GetValue(7),              // Path
            reader.GetValue(8),              // ReleaseDate
            subType,                         // SubType
            type,                            // Type
        ];
    }

    private static object?[] MapHistoryItem(SqliteDataReader reader, IReadOnlyDictionary<Guid, string> nzbBlobs)
    {
        var id = reader.GetValue(0);
        var nzbBlobId = TryParseGuid(id) is { } blobId && nzbBlobs.ContainsKey(blobId) ? id : null;

        return
        [
            id,                  // Id
            null,                // ArrDownloadId
            reader.GetValue(4),  // Category
            null,                // ContentGroupKey
            reader.GetValue(1),  // CreatedAt
            reader.GetValue(9),  // DownloadDirId
            reader.GetValue(5),  // DownloadStatus
            reader.GetValue(7),  // DownloadTimeSeconds
            reader.GetValue(8),  // FailMessage
            reader.GetValue(2),  // FileName
            null,                // IndexerName
            reader.GetValue(3),  // JobName
            null,                // LastPlayedAt
            nzbBlobId,           // NzbBlobId
            reader.GetValue(6),  // TotalSegmentBytes
        ];
    }

    private static object?[] MapHealthCheckResult(SqliteDataReader reader) =>
    new object?[]
    {
        reader.GetValue(0),  // Id
        reader.GetValue(1),  // CreatedAt
        reader.GetValue(2),  // DavItemId
        null,                // JobName
        reader.GetValue(6),  // Message
        null,                // NzbFileName
        reader.GetValue(3),  // Path
        reader.GetValue(5),  // RepairStatus
        reader.GetValue(4),  // Result
    };

    /// <summary>
    /// Metadata that made it into a typed blob is not also exported as a legacy row: the blob
    /// *is* that row, so a second copy only gives InfiniDysk's
    /// <c>UsenetFileToBlobstoreMigrationService</c> work to do on first boot. Rows without a
    /// blob (obfuscation-key items, unparseable payloads) are still exported as the fallback.
    /// </summary>
    private static bool CoveredByBlob(SqliteDataReader reader, IReadOnlySet<Guid> fileBlobs) =>
        TryParseGuid(reader.GetValue(0)) is { } id && fileBlobs.Contains(id);

    private static object?[]? MapConfigItem(SqliteDataReader reader, long providerGeneration)
    {
        var name = reader.GetValue(0) as string;
        if (name is null || !InfiniDyskExportSchema.ExportedConfigKeys.Contains(name))
            return null;

        if (name == "usenet.providers")
        {
            var providers = MapUsenetProviders(reader.GetValue(1) as string, providerGeneration);
            return providers is null ? null : new object?[] { name, providers };
        }

        // Other secrets are stored as-is; webdav.pass is an ASP.NET Identity PBKDF2 hash, which
        // both projects verify with the same PasswordUtil, so it keeps working after the move.
        return new object?[] { name, reader.GetValue(1) };
    }

    /// <summary>
    /// Rewrites our provider list into InfiniDysk's <c>usenet.providers</c> shape. Property names
    /// for the fields we have are identical, but their record requires a non-empty
    /// <c>ProviderId</c> (used as the metrics/quota key, and asserted by
    /// <c>UsenetProviderIdentity</c>) plus quota fields we do not have.
    /// </summary>
    private static string? MapUsenetProviders(string? raw, long providerGeneration)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(raw);
        }
        catch (JsonException e)
        {
            Log.Warning("[InfiniDyskExport] Skipping usenet.providers (not valid JSON): {Error}", e.Message);
            return null;
        }

        if (root?["Providers"] is not JsonArray providers || providers.Count == 0) return null;

        var mapped = new JsonArray();
        foreach (var node in providers)
        {
            if (node is not JsonObject provider) continue;

            var host = ReadString(provider, "Host");
            if (string.IsNullOrWhiteSpace(host)) continue;

            var user = ReadString(provider, "User") ?? "";
            mapped.Add(new JsonObject
            {
                ["ProviderId"] = DeterministicProviderId(host, user).ToString(),
                ["Type"] = ReadInt(provider, "Type") ?? 1, // Pooled
                ["Host"] = host,
                ["Port"] = ReadInt(provider, "Port") ?? 563,
                ["UseSsl"] = ReadBool(provider, "UseSsl") ?? true,
                ["SkipTlsVerification"] = false,
                ["User"] = user,
                ["Pass"] = ReadString(provider, "Pass") ?? "",
                ["MaxConnections"] = ReadInt(provider, "MaxConnections") ?? 1,
                ["MaxTransferConnections"] = null,
                // Priority stays 0: our fork prioritises by list position, and inventing an
                // ordering would silently change which provider serves requests first.
                ["Priority"] = 0,
                ["PipeliningDepth"] = null,
                ["Nickname"] = null,
                ["StorageGroup"] = "",
                ["ByteLimit"] = null,
                ["BytesUsedOffset"] = 0,
                ["BytesUsedResetAt"] = 0,
            });
        }

        if (mapped.Count == 0) return null;

        var result = new JsonObject
        {
            ["ProviderGeneration"] = providerGeneration,
            ["Providers"] = mapped,
        };
        return result.ToJsonString();
    }

    /// <summary>
    /// Writes every NZB we still hold as raw XML (<c>HistoryItems.NzbContents</c>) into
    /// <c>{export}/blobs/..</c> in InfiniDysk's blob layout, keyed by the history item id. That
    /// is exactly the id InfiniDysk uses for both the history row's blob and the mounted file's
    /// blob. Returns blob id → original filename for the <c>NzbNames</c> rows.
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, string>> WriteNzbBlobsAsync(
        SqliteConnection source,
        string directory,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<Guid, string>();
        var blobsRoot = Path.Join(directory, BlobsDirectoryName);

        await using var command = source.CreateCommand();
        command.CommandText = """
            SELECT "Id", "FileName", "NzbContents"
            FROM "HistoryItems"
            WHERE "NzbContents" IS NOT NULL AND "NzbContents" <> ''
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var written = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (TryParseGuid(reader.GetValue(0)) is not { } id) continue;

            string xml;
            try
            {
                xml = CompressionUtil.Decompress((string)reader.GetValue(2)!);
            }
            catch (Exception e)
            {
                // A blob is enrichment: a corrupt column must not fail the whole migration.
                Log.Warning("[InfiniDyskExport] Skipping NZB blob {Id}: {Error}", id, e.Message);
                continue;
            }

            if (string.IsNullOrWhiteSpace(xml)) continue;

            // Guard against a mangled/compressed-in-place column: InfiniDysk parses these blobs
            // as NZB XML, so anything that does not start with a tag is skipped rather than
            // written as a blob that will only fail later.
            xml = xml.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            if (xml.Length == 0 || xml[0] != '<')
            {
                Log.Warning("[InfiniDyskExport] Skipping NZB blob {Id}: content is not XML", id);
                continue;
            }

            var blobPath = InfiniDyskBlobWriter.GetBlobPath(blobsRoot, id);
            try
            {
                var blobDirectory = Path.GetDirectoryName(blobPath);
                if (!string.IsNullOrEmpty(blobDirectory)) Directory.CreateDirectory(blobDirectory);
                await File.WriteAllBytesAsync(blobPath, Encoding.UTF8.GetBytes(xml), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warning("[InfiniDyskExport] Skipping NZB blob {Id}: {Error}", id, e.Message);
                continue;
            }

            var fileName = reader.GetValue(1) as string;
            names[id] = string.IsNullOrWhiteSpace(fileName) ? id + ".nzb" : fileName;
            written++;
        }

        if (written > 0)
            Log.Warning("[InfiniDyskExport] Wrote {Count} NZB blobs to {Root}", written, blobsRoot);

        return names;
    }

    private static async Task<int> WriteNzbNamesAsync(
        StreamWriter writer,
        IReadOnlyDictionary<Guid, string> nzbBlobs,
        CancellationToken cancellationToken)
    {
        foreach (var (blobId, fileName) in nzbBlobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(
                "INSERT INTO \"NzbNames\" (\"Id\",\"FileName\") VALUES (" +
                $"{QuoteString(blobId.ToString().ToUpperInvariant())},{QuoteString(fileName)});")
                .ConfigureAwait(false);
        }

        return nzbBlobs.Count;
    }

    /// <summary>
    /// Stable per-account metrics key. InfiniDysk asserts a non-empty ProviderId before using a
    /// provider as a metrics key, and two accounts on one host must not share counters, so the
    /// id is derived from host + username rather than left empty or randomised per export.
    /// </summary>
    private static Guid DeterministicProviderId(string host, string user)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(host + "\n" + user));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static Guid? TryParseGuid(object? value)
    {
        if (value is Guid guid) return guid;
        return Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed)
            ? parsed
            : null;
    }

    private static string? ReadString(JsonObject obj, string name)
    {
        try
        {
            return obj[name]?.GetValue<string>();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static int? ReadInt(JsonObject obj, string name)
    {
        try
        {
            return obj[name]?.GetValue<int>();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static bool? ReadBool(JsonObject obj, string name)
    {
        try
        {
            return obj[name]?.GetValue<bool>();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps our <see cref="NzbWebDAV.Database.Models.DavItem.ItemType"/> values onto
    /// InfiniDysk's Directory/UsenetFile + SubType model.
    /// </summary>
    private static (int Type, int SubType) MapItemType(object? rawId, int ourType)
    {
        if (Guid.TryParse(Convert.ToString(rawId, CultureInfo.InvariantCulture), out var id))
        {
            if (id == Guid.Empty) return (1, 102);
            if (id == NzbsRootId) return (1, 103);
            if (id == ContentRootId) return (1, 104);
            if (id == SymlinkRootId) return (1, 105);
            if (id == IdsRootId) return (1, 106);
        }

        return ourType switch
        {
            2 => (1, 105), // SymlinkRoot
            3 => (2, 201), // NzbFile
            4 => (2, 202), // RarFile (legacy enum value, migrated to MultipartFile)
            5 => (1, 106), // IdsRoot
            6 => (2, 203), // MultipartFile
            _ => (1, 101), // Directory
        };
    }

    /// <summary>
    /// Decompresses one of our Zstd-wrapped JSON columns so InfiniDysk sees the plain JSON it
    /// expects. Values that were never compressed are returned unchanged.
    /// </summary>
    private static object? UnpackJson(object? stored)
    {
        if (stored is not string value || value.Length == 0)
            return stored;

        var json = CompressionUtil.Decompress(value);
        return ControlCharacters.Replace(json, string.Empty);
    }

    private static async Task<int> WriteRowsAsync(
        SqliteConnection source,
        StreamWriter writer,
        string destinationTable,
        IReadOnlyList<string> destinationColumns,
        string selectSql,
        Func<SqliteDataReader, object?[]?> map,
        CancellationToken cancellationToken)
    {
        var columnList = string.Join(",", destinationColumns.Select(c => $"\"{c}\""));
        await using var command = source.CreateCommand();
        command.CommandText = selectSql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var written = 0;
        var sql = new StringBuilder(512);
        var guidColumns = GuidTextColumns.GetValueOrDefault(destinationTable);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = map(reader);
            if (values is null) continue;

            sql.Clear();
            sql.Append("INSERT INTO \"").Append(destinationTable).Append("\" (").Append(columnList).Append(") VALUES (");
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0) sql.Append(',');

                var value = values[i];
                if (guidColumns is not null && value is string text
                    && guidColumns.Contains(destinationColumns[i]))
                {
                    value = text.ToUpperInvariant();
                }

                sql.Append(FormatValue(value));
            }
            sql.Append(");");

            await writer.WriteLineAsync(sql.ToString()).ConfigureAwait(false);
            written++;
        }

        return written;
    }

    private static async Task<int> WriteMigrationHistoryAsync(StreamWriter writer, CancellationToken cancellationToken)
    {
        const string columnList = "\"MigrationId\",\"ProductVersion\"";
        foreach (var migrationId in InfiniDyskExportSchema.MigrationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(
                $"INSERT INTO \"__EFMigrationsHistory\" ({columnList}) VALUES " +
                $"({QuoteString(migrationId)},{QuoteString(InfiniDyskExportSchema.EfProductVersion)});")
                .ConfigureAwait(false);
        }

        return InfiniDyskExportSchema.MigrationIds.Count;
    }

    private static string FormatValue(object? value) => value switch
    {
        null or DBNull => "NULL",
        string s => QuoteString(s),
        bool b => b ? "1" : "0",
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        short sh => sh.ToString(CultureInfo.InvariantCulture),
        byte b => b.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("G17", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("G17", CultureInfo.InvariantCulture),
        decimal m => ((double)m).ToString("G17", CultureInfo.InvariantCulture),
        byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
        Guid guid => QuoteString(guid.ToString()),
        DateTime dateTime => QuoteString(
            dateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)),
        DateTimeOffset dateTimeOffset => QuoteString(
            dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture)),
        _ => QuoteString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    private static string QuoteString(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static async Task<SqliteConnection> OpenSourceAsync(CancellationToken cancellationToken)
    {
        var databasePath = DavDatabaseContext.DatabaseFilePath;
        if (!File.Exists(databasePath))
            throw new FileNotFoundException($"Database file not found: `{databasePath}`.", databasePath);

        var readOnly = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();

        var connection = new SqliteConnection(readOnly);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        // A WAL database may need to create -shm on open, which fails read-only.
        var readWrite = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();

        connection = new SqliteConnection(readWrite);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteReadmeAsync(
        string directory,
        IReadOnlyDictionary<string, int> counts,
        int nzbBlobCount,
        int fileBlobCount,
        bool compact,
        string? warning,
        CancellationToken cancellationToken)
    {
        var path = Path.Join(directory, ReadmeFileName);
        var sqlBytes = File.Exists(Path.Join(directory, SqlFileName))
            ? new FileInfo(Path.Join(directory, SqlFileName)).Length
            : 0L;

        // Only emitted when it applies, so the file stays short in the normal case.
        var warningSection = warning is null
            ? string.Empty
            : $"""

                !! IMPORTANT WARNING !!
                {warning}
                """;

        var compactNote = compact
            ? "Health-check history (HealthCheckResults / HealthCheckStats) was omitted because\n" +
              "this was a COMPACT export. InfiniDysk re-checks the library itself, so nothing\n" +
              "important is lost."
            : "Health-check history (HealthCheckResults / HealthCheckStats) was included. If\n" +
              "db.sql is too large for the target setup, re-export with 'Compact' enabled to\n" +
              "drop it.";

        var content = $"""
            InfiniDysk migration export
            Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC
            db.sql size: {sqlBytes:N0} bytes

            IMPORTING
              1. Copy the `blobs` folder from this export into InfiniDysk's config volume
                 (`/config/blobs`), merging with anything already there. This restores the
                 original NZB documents used by "Download NZB", PAR2 repair, and health
                 check identity. Do this BEFORE restoring so the post-restore check finds
                 the files.
              2. In InfiniDysk open Settings -> Backup & Restore and pick `db.sql` with the
                 upload file input. Uploading only stages the file.
              3. Click Restore on the uploaded row, tick the confirmation, and let the
                 backend restart into maintenance. Do not interrupt it.
              4. Sign in. No accounts are carried over, so create the admin account when
                 prompted.{warningSection}

            TROUBLESHOOTING
              "Upload failed (413)" means a proxy in front of InfiniDysk rejected the body;
              InfiniDysk itself never returns 413 (its size failures are 400 with a JSON
              error, or a 500 at its 4 GiB multipart ceiling). Common causes:
                - nginx / Nginx Proxy Manager: `client_max_body_size` defaults to 1 MB.
                  Raise it (`client_max_body_size 5g;`) and disable request buffering
                  (`proxy_request_buffering off;`) so the upload is streamed.
                - Cloudflare (orange cloud): 100 MB cap on every plan; bypass it for this
                  upload.
                - Any other reverse proxy: raise its maximum body size and timeouts.

              To bypass every proxy, upload from inside the container instead:
                docker cp db.sql <container>:/tmp/db.sql
                KEY=$(docker exec -u appuser <container> sh -c 'tr "\0" "\n" < \
                  /proc/$(pgrep -f "^./NzbWebDAV" | head -1)/environ \
                  | grep FRONTEND_BACKEND_API_KEY | cut -d= -f2')
                docker exec -u appuser <container> curl -sS -H "x-api-key: $KEY" \
                  -F "file=@/tmp/db.sql" -F "notes=migration" \
                  http://localhost:8080/api/db-backup-upload
              Then click Restore on the new row in the Backup tab.

              If the upload succeeds but the page later reports missing blobs, step 1 was
              skipped or `blobs` was copied to the wrong path.

            WHAT WAS EXPORTED
              Library tree (DavItems)             : {counts.GetValueOrDefault("DavItems"),6}
              NZB file metadata (DavNzbFiles)     : {counts.GetValueOrDefault("DavNzbFiles"),6}
              RAR file metadata (DavRarFiles)     : {counts.GetValueOrDefault("DavRarFiles"),6}
              Multipart metadata (DavMultipartFiles): {counts.GetValueOrDefault("DavMultipartFiles"),6}
              History rows (HistoryItems)         : {counts.GetValueOrDefault("HistoryItems"),6}
              Health check results                : {counts.GetValueOrDefault("HealthCheckResults"),6}
              Health check stats                  : {counts.GetValueOrDefault("HealthCheckStats"),6}
              NZB blob files (blobs/)             : {nzbBlobCount,6}
              File metadata blobs (blobs/)        : {fileBlobCount,6}
              Settings (ConfigItems)              : {counts.GetValueOrDefault("ConfigItems"),6}

              {compactNote}

            WHAT WAS NOT EXPORTED
              - The active queue. In-flight downloads are better re-added than migrated, and
                every queue row would need an NZB document that is still being processed.
              - User accounts and sessions. Create a fresh admin on first sign-in.
              - Indexer, *Arr, rclone, and Watchtower/Warden settings. Re-enter those in
                Settings after the restore.
              - Typed file metadata blobs (`DavItems.FileBlobId`) are written wherever our
                metadata can be mapped without dropping fork-only state. If one is ever
                unreadable, InfiniDysk logs a warning and falls back to the exported
                `DavNzbFiles`/`DavRarFiles`/`DavMultipartFiles` row.
              - NZB blobs are written from `HistoryItems.NzbContents`. Library items whose
                history row is gone have no NZB document left to copy: they still stream via
                the database rows, but "Download NZB" and PAR2 repair stay unavailable.
              - Fork-only tables (provider stats, missing-article history, local links,
                bandwidth samples, analysis history).

            NOTES
              - `api.key`, the WebDAV credentials, and the Usenet provider list are carried
                over, so *Arr clients and WebDAV mounts keep working if the API key is
                preserved. Provider priority is exported as 0 for all providers (this fork
                prioritises by list order); review the order in Settings once.
              - Import this dump into a *fresh* InfiniDysk instance. Restoring over an
                existing install replaces its settings, queue, history, and WebDAV tree.
            """;

        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort — a leftover .tmp file is harmless.
        }
    }
}
