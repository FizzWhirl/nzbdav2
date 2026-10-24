namespace NzbWebDAV.Database.Export;

/// <summary>
/// The main-database schema that InfiniDysk (the "NzbDAV SuperFork", v1.6.3) expects in
/// its <c>db.sqlite</c>. Shared lineage aside, the two projects diverged after
/// <c>20251113081523_Populate-Usenet-Providers-Config</c>, so an export is only importable
/// if it is written against InfiniDysk's *current* model rather than our own.
///
/// Deliberate compromises, all chosen so that an InfiniDysk restore cannot fail:
/// <list type="bullet">
/// <item>Column *sets* and SQLite storage classes match InfiniDysk's model snapshot exactly
/// (EF maps every entity property to a column on read/write, so a missing column would break
/// normal operation after the restore).</item>
/// <item>Only primary keys are enforced, and no secondary indexes are created. InfiniDysk's
/// restore marks every migration as applied, so nothing will recreate the indexes for us;
/// they are omitted on purpose because a unique index over migrated legacy rows is a
/// needless way to make the import fail. The application recreates any index it truly
/// needs on its own next write path.</item>
/// <item>NOT NULL is limited to primary-key columns. Being stricter than InfiniDysk would
/// only create new ways for the import to fail.</item>
/// </list>
/// </summary>
internal static class InfiniDyskExportSchema
{
    /// <summary>EF Core product version recorded in <c>__EFMigrationsHistory</c>.</summary>
    public const string EfProductVersion = "10.0.11";

    /// <summary>
    /// Every table InfiniDysk's <c>DavDatabaseContext</c> maps, in the order their own
    /// dumper would emit them. Tables we do not migrate are still created (empty) because
    /// the application writes to them as soon as it starts using the relevant feature.
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string Ddl)> Tables =
    [
        ("Accounts", """
            CREATE TABLE "Accounts" (
                "Type" INTEGER NOT NULL,
                "Username" TEXT NOT NULL,
                "PasswordHash" TEXT NOT NULL,
                "RandomSalt" TEXT NOT NULL,
                CONSTRAINT "PK_Accounts" PRIMARY KEY ("Type", "Username")
            );
            """),

        ("ArticleMissCacheEntries", """
            CREATE TABLE "ArticleMissCacheEntries" (
                "CacheKey" TEXT NOT NULL,
                "ConfirmedAtUnix" INTEGER NOT NULL,
                CONSTRAINT "PK_ArticleMissCacheEntries" PRIMARY KEY ("CacheKey")
            );
            """),

        ("BlobCleanupItems", """
            CREATE TABLE "BlobCleanupItems" (
                "Id" TEXT NOT NULL,
                CONSTRAINT "PK_BlobCleanupItems" PRIMARY KEY ("Id")
            );
            """),

        ("ConfigItems", """
            CREATE TABLE "ConfigItems" (
                "ConfigName" TEXT NOT NULL,
                "ConfigValue" TEXT NOT NULL,
                CONSTRAINT "PK_ConfigItems" PRIMARY KEY ("ConfigName")
            );
            """),

        ("DavCleanupItems", """
            CREATE TABLE "DavCleanupItems" (
                "Id" TEXT NOT NULL,
                CONSTRAINT "PK_DavCleanupItems" PRIMARY KEY ("Id")
            );
            """),

        ("DavItems", """
            CREATE TABLE "DavItems" (
                "Id" TEXT NOT NULL,
                "ArrDownloadId" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "FileBlobId" TEXT NULL,
                "FileSize" INTEGER NULL,
                "GeneratedStrmOutputRoot" TEXT NULL,
                "GeneratedStrmPath" TEXT NULL,
                "GeneratedStrmTarget" TEXT NULL,
                "GeneratedSymlinkOutputRoot" TEXT NULL,
                "GeneratedSymlinkPath" TEXT NULL,
                "GeneratedSymlinkTarget" TEXT NULL,
                "HealthRepairPending" INTEGER NOT NULL DEFAULT 0,
                "UrgentRepairFailures" INTEGER NULL,
                "HistoryItemId" TEXT NULL,
                "IdPrefix" TEXT NOT NULL,
                "LastHealthCheck" INTEGER NULL,
                "Name" TEXT NOT NULL,
                "NextHealthCheck" INTEGER NULL,
                "NzbBlobId" TEXT NULL,
                "ParentId" TEXT NULL,
                "Path" TEXT NOT NULL,
                "ReleaseDate" INTEGER NULL,
                "SubType" INTEGER NOT NULL,
                "Type" INTEGER NOT NULL,
                CONSTRAINT "PK_DavItems" PRIMARY KEY ("Id")
            );
            """),

        ("DavMultipartFiles", """
            CREATE TABLE "DavMultipartFiles" (
                "Id" TEXT NOT NULL,
                "Metadata" TEXT NOT NULL,
                CONSTRAINT "PK_DavMultipartFiles" PRIMARY KEY ("Id")
            );
            """),

        ("DavNzbFiles", """
            CREATE TABLE "DavNzbFiles" (
                "Id" TEXT NOT NULL,
                "SegmentIds" TEXT NOT NULL,
                CONSTRAINT "PK_DavNzbFiles" PRIMARY KEY ("Id")
            );
            """),

        ("DavRarFiles", """
            CREATE TABLE "DavRarFiles" (
                "Id" TEXT NOT NULL,
                "RarParts" TEXT NOT NULL,
                CONSTRAINT "PK_DavRarFiles" PRIMARY KEY ("Id")
            );
            """),

        ("HealthCheckResults", """
            CREATE TABLE "HealthCheckResults" (
                "Id" TEXT NOT NULL,
                "CreatedAt" INTEGER NOT NULL,
                "DavItemId" TEXT NOT NULL,
                "JobName" TEXT NULL,
                "Message" TEXT NULL,
                "NzbFileName" TEXT NULL,
                "Path" TEXT NOT NULL,
                "RepairStatus" INTEGER NOT NULL,
                "Result" INTEGER NOT NULL,
                CONSTRAINT "PK_HealthCheckResults" PRIMARY KEY ("Id")
            );
            """),

        ("HealthCheckStats", """
            CREATE TABLE "HealthCheckStats" (
                "DateStartInclusive" INTEGER NOT NULL,
                "DateEndExclusive" INTEGER NOT NULL,
                "Result" INTEGER NOT NULL,
                "RepairStatus" INTEGER NOT NULL,
                "Count" INTEGER NOT NULL,
                CONSTRAINT "PK_HealthCheckStats" PRIMARY KEY ("DateStartInclusive", "DateEndExclusive", "Result", "RepairStatus")
            );
            """),

        ("HistoryCleanupItems", """
            CREATE TABLE "HistoryCleanupItems" (
                "Id" TEXT NOT NULL,
                "DeleteMountedFiles" INTEGER NOT NULL,
                CONSTRAINT "PK_HistoryCleanupItems" PRIMARY KEY ("Id")
            );
            """),

        ("HistoryItems", """
            CREATE TABLE "HistoryItems" (
                "Id" TEXT NOT NULL,
                "ArrDownloadId" TEXT NULL,
                "Category" TEXT NOT NULL,
                "ContentGroupKey" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "DownloadDirId" TEXT NULL,
                "DownloadStatus" INTEGER NOT NULL,
                "DownloadTimeSeconds" INTEGER NOT NULL,
                "FailMessage" TEXT NULL,
                "FileName" TEXT NOT NULL,
                "IndexerName" TEXT NULL,
                "JobName" TEXT NOT NULL,
                "LastPlayedAt" INTEGER NULL,
                "NzbBlobId" TEXT NULL,
                "TotalSegmentBytes" INTEGER NOT NULL,
                CONSTRAINT "PK_HistoryItems" PRIMARY KEY ("Id")
            );
            """),

        ("IndexerApiHits", """
            CREATE TABLE "IndexerApiHits" (
                "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "AccessedAt" INTEGER NOT NULL,
                "IndexerName" TEXT NOT NULL,
                "Type" INTEGER NOT NULL
            );
            """),

        ("ListSources", """
            CREATE TABLE "ListSources" (
                "Id" TEXT NOT NULL,
                "Cap" INTEGER NOT NULL,
                "CreatedAtUnix" INTEGER NOT NULL,
                "Enabled" INTEGER NOT NULL,
                "Kind" TEXT NOT NULL,
                "LastSyncError" TEXT NULL,
                "LastSyncedAtUnix" INTEGER NULL,
                "Name" TEXT NOT NULL,
                "SeriesScope" TEXT NULL,
                "Url" TEXT NULL,
                CONSTRAINT "PK_ListSources" PRIMARY KEY ("Id")
            );
            """),

        ("NzbBlobCleanupItems", """
            CREATE TABLE "NzbBlobCleanupItems" (
                "Id" TEXT NOT NULL,
                CONSTRAINT "PK_NzbBlobCleanupItems" PRIMARY KEY ("Id")
            );
            """),

        ("NzbNames", """
            CREATE TABLE "NzbNames" (
                "Id" TEXT NOT NULL,
                "FileName" TEXT NOT NULL,
                CONSTRAINT "PK_NzbNames" PRIMARY KEY ("Id")
            );
            """),

        ("NzbResolutionGroups", """
            CREATE TABLE "NzbResolutionGroups" (
                "Id" TEXT NOT NULL,
                "CandidatesJson" TEXT NOT NULL,
                "CreatedAtUnix" INTEGER NOT NULL,
                "ProfileToken" TEXT NOT NULL,
                "SearchId" TEXT NOT NULL,
                "TokensJson" TEXT NOT NULL,
                "Type" TEXT NOT NULL,
                CONSTRAINT "PK_NzbResolutionGroups" PRIMARY KEY ("Id")
            );
            """),

        ("Par2RepairJobs", """
            CREATE TABLE "Par2RepairJobs" (
                "Id" TEXT NOT NULL,
                "Attempts" INTEGER NOT NULL,
                "BytesRead" INTEGER NOT NULL,
                "CompletedAt" INTEGER NULL,
                "CreatedAt" INTEGER NOT NULL,
                "DavItemId" TEXT NOT NULL,
                "FailureReason" TEXT NULL,
                "MissingSegmentIds" TEXT NOT NULL,
                "NextAttemptAt" INTEGER NULL,
                "Path" TEXT NOT NULL,
                "SlicesReconstructed" INTEGER NOT NULL,
                "StartedAt" INTEGER NULL,
                "State" INTEGER NOT NULL,
                CONSTRAINT "PK_Par2RepairJobs" PRIMARY KEY ("Id")
            );
            """),

        ("QueueItems", """
            CREATE TABLE "QueueItems" (
                "Id" TEXT NOT NULL,
                "ArrDownloadId" TEXT NULL,
                "Category" TEXT NOT NULL,
                "ContentGroupKey" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "FileName" TEXT NOT NULL,
                "IndexerName" TEXT NULL,
                "JobName" TEXT NOT NULL,
                "NzbFileSize" INTEGER NOT NULL,
                "PauseUntil" TEXT NULL,
                "PostProcessing" INTEGER NOT NULL,
                "Priority" INTEGER NOT NULL,
                "SortOrder" INTEGER NOT NULL,
                "TotalSegmentBytes" INTEGER NOT NULL,
                CONSTRAINT "PK_QueueItems" PRIMARY KEY ("Id")
            );
            """),

        ("QueueNzbContents", """
            CREATE TABLE "QueueNzbContents" (
                "Id" TEXT NOT NULL,
                "NzbContents" TEXT NOT NULL,
                CONSTRAINT "PK_QueueNzbContents" PRIMARY KEY ("Id")
            );
            """),

        ("SetupWizardStates", """
            CREATE TABLE "SetupWizardStates" (
                "Id" INTEGER NOT NULL,
                "Disposition" INTEGER NOT NULL,
                "IngestionMethods" TEXT NOT NULL,
                "UpdatedAt" INTEGER NOT NULL,
                "WizardVersion" INTEGER NOT NULL,
                CONSTRAINT "PK_SetupWizardStates" PRIMARY KEY ("Id")
            );
            """),

        ("WantedItems", """
            CREATE TABLE "WantedItems" (
                "Id" TEXT NOT NULL,
                "ContentId" TEXT NOT NULL,
                "CreatedAtUnix" INTEGER NOT NULL,
                "FailReason" TEXT NULL,
                "Key" TEXT NOT NULL,
                "LastResolvedAtUnix" INTEGER NULL,
                "LastVerifiedAtUnix" INTEGER NULL,
                "NextCheckAtUnix" INTEGER NULL,
                "Provenance" TEXT NOT NULL,
                "ResponderHost" TEXT NULL,
                "Shortlist" TEXT NOT NULL,
                "State" TEXT NOT NULL,
                "Title" TEXT NOT NULL,
                "Type" TEXT NOT NULL,
                "UpdatedAtUnix" INTEGER NOT NULL,
                "WinnerNzb" BLOB NULL,
                CONSTRAINT "PK_WantedItems" PRIMARY KEY ("Id")
            );
            """),

        ("WatchdogEntries", """
            CREATE TABLE "WatchdogEntries" (
                "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "AttemptedAt" INTEGER NOT NULL,
                "CandidateTitle" TEXT NOT NULL,
                "ClickId" TEXT NOT NULL,
                "ContentGroupKey" TEXT NULL,
                "ContentType" TEXT NOT NULL,
                "DurationMs" INTEGER NOT NULL,
                "FailReason" TEXT NULL,
                "IndexerName" TEXT NOT NULL,
                "IsWinner" INTEGER NOT NULL,
                "ProviderHost" TEXT NULL,
                "QueueItemId" TEXT NULL,
                "RankIndex" INTEGER NOT NULL,
                "RequestedTitle" TEXT NOT NULL,
                "Result" INTEGER NOT NULL,
                "Size" INTEGER NOT NULL
            );
            """),

        ("__EFMigrationsHistory", """
            CREATE TABLE "__EFMigrationsHistory" (
                "MigrationId" TEXT NOT NULL,
                "ProductVersion" TEXT NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
            );
            """),
    ];

    /// <summary>
    /// InfiniDysk's migration history as of v1.6.3, oldest first. Writing the complete list
    /// is what makes the import land on a schema InfiniDysk considers current: EF then has
    /// no pending migrations and will not try to re-apply anything to a database that was
    /// never at an intermediate state. Extra rows for migrations the fork alone has are
    /// simply ignored by EF, which is why only InfiniDysk's ids appear here.
    /// </summary>
    public static readonly IReadOnlyList<string> MigrationIds =
    [
        "20250529081501_InitializeDatabase",
        "20250529102749_Add-CreatedAt-To-DavItem",
        "20250605030729_Add-Accounts-Table",
        "20250710072717_AddConfigItemsTable",
        "20250722004955_Ensure-Api-Key-Exists",
        "20250818232259_Add-DownloadDirId-To-HistoryItem",
        "20250819091746_Add-Ids-Root-Directory",
        "20250819221618_Add-Path-To-DavItem",
        "20250824100609_Add-IdPrefix-To-DavItems",
        "20251010192643_Add-Healthcheck-Columns-To-DavItems",
        "20251014060520_Add-HealthCheckResults-Table",
        "20251014062743_Add-HealthCheckQueue-Index",
        "20251024003729_Add-DavMultipartFiles-Table",
        "20251030163510_Add-QueueNzbContents-Table",
        "20251105050845_Add-HealthCheckStats-Table",
        "20251105200722_Add-CreatedAt-Index-To-HealthCheckResults-Table",
        "20251106165542_Ensure-Strm-Key-Exists",
        "20251113081523_Populate-Usenet-Providers-Config",
        "20260108054841_Add-BlobCleanupItems-Table",
        "20260108061241_Add-Trigger-To-QueueItems-Table",
        "20260121033824_Populate-Blocklisted-Files-Setting",
        "20260122220920_Populate-Health-Check-Categories-Setting",
        "20260129182923_Update-DavItems-Type-And-SubType",
        "20260129190728_Add-FileBlobId-To-DavItems-Table",
        "20260130005230_Add-Trigger-To-DavItems-Table",
        "20260201023253_Add-HistoryItemId-To-DavItems-Table",
        "20260202005856_Add-Type-CreatedAt-Index-To-DavItems-Table",
        "20260202094106_Add-SubType-CreatedAt-Index-To-DavItems-Table",
        "20260203071130_Add-DavCleanupItems-Table",
        "20260203205014_Add-DeleteMountedFiles-To-HistoryCleanupItems-Table",
        "20260226053712_Add-NzbBlobId-And-NzbNames",
        "20260407215451_Change-QueueItems-FileName-Index-To-Category-FileName",
        "20260518000000_Add-IndexerName-To-Queue-And-History",
        "20260521000000_Add-Variants-Columns",
        "20260522000000_Add-Watchdog-Entries-Table",
        "20260525000000_Add-IndexerApiHits-Table",
        "20260602000000_Add-Watchtower-Tables",
        "20260604000000_Add-SeriesScope-To-ListSources",
        "20260604120000_Add-UpdatedAtUnix-Index-To-WantedItems",
        "20260712000000_Fix-Empty-Categories",
        "20260713120000_Add-Path-Index-To-DavItems",
        "20260718000000_Add-NzbResolutionGroups-Table",
        "20260731171110_Add-SingleAdmin-UniqueIndex",
        "20260817160000_Add-QueueItem-SortOrder",
        "20260817185420_Add-NzbIdentity-To-HealthCheckResults",
        "20260818200000_Add-ArticleMiss-Cache",
        "20260818210000_Copy-Legacy-Pipelining-Keys",
        "20260818220000_Add-Par2-Repair-Jobs",
        "20260820160000_Normalize-Guid-Text-Casing",
        "20260822140000_Add-Profile-Stream-State-Index",
        "20260824143000_Add-Generated-Symlink-Metadata",
        "20260824160000_Add-Health-Repair-Pending",
        "20260830120000_Add-Arr-Download-Provenance",
        "20260902000809_Add-Setup-Wizard-State",
        "20260903120000_Pin-Segment-Cache-For-Existing-Installs",
        "20260904120000_Skip-Setup-Wizard-For-Existing-Installs",
        "20260916120000_Add-Urgent-Repair-Failures",
        "20260921210000_Add-Health-Alert-History-Lookup-Index",
    ];

    /// <summary>
    /// Config keys whose meaning is identical in both projects. <c>usenet.providers</c> is the
    /// one entry that needs translation rather than a copy: InfiniDysk's provider record adds
    /// metrics/quota fields (notably a non-empty <c>ProviderId</c>), so the exporter re-emits it
    /// in their shape instead of copying our JSON verbatim. Everything else is either
    /// fork-specific, renamed, or has a rewritten value shape and is deliberately not carried
    /// over so that a bad value cannot stop the destination instance from starting.
    /// </summary>
    public static readonly IReadOnlyList<string> ExportedConfigKeys =
    [
        "api.key",
        "api.categories",
        "api.manual-category",
        "api.import-strategy",
        "api.completed-downloads-dir",
        "api.user-agent",
        "api.duplicate-nzb-behavior",
        "api.ignore-history-limit",
        "api.ensure-importable-video",
        // The rclone mount point is baked into every generated .rclonelink target
        // (`{mount-dir}/.ids/{prefix}/{guid}`). Carrying it over keeps migrated symlinks
        // pointing at the path Sonarr/Radarr/Plex already have mounted; without it the
        // destination falls back to its own default and every existing link dangles.
        "rclone.mount-dir",
        "usenet.providers",
        "webdav.user",
        "webdav.pass",
        "webdav.show-hidden-files",
        "webdav.enforce-readonly",
        "webdav.preview-par2-files",
    ];
}
