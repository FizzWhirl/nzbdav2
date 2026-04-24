using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Data;
using System.Text;
using NWebDav.Server;
using NWebDav.Server.Stores;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Auth;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.BlobStoreCompat;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Middlewares;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.Tools;
using NzbWebDAV.WebDav;
using NzbWebDAV.Streams;
using NzbWebDAV.WebDav.Base;
using NzbWebDAV.Websocket;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace NzbWebDAV;

class Program
{
    static async Task Main(string[] args)
    {
        // Update thread-pool
        var coreCount = Environment.ProcessorCount;
        var minThreads = Math.Max(coreCount * 2, 50); // 2x cores, minimum 50
        var maxThreads = Math.Max(coreCount * 50, 1000); // 50x cores, minimum 1000
        ThreadPool.SetMinThreads(minThreads, minThreads);
        ThreadPool.SetMaxThreads(maxThreads, maxThreads);

        // Initialize logger
        var defaultLevel = LogEventLevel.Information;
        var envLevel = Environment.GetEnvironmentVariable("LOG_LEVEL");
        var level = Enum.TryParse<LogEventLevel>(envLevel, true, out var parsed) ? parsed : defaultLevel;
        var levelSwitch = new LoggingLevelSwitch(level);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .MinimumLevel.Override("NWebDAV", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Mvc", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Routing", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.DataProtection", LogEventLevel.Error)
            .WriteTo.Console(theme: AnsiConsoleTheme.Code)
            .WriteTo.Sink(InMemoryLogSink.Instance)
            .CreateLogger();

        // Log build version to verify correct build is running
        Log.Warning("═══════════════════════════════════════════════════════════════");
            Log.Warning("  NzbDav Backend Starting - BUILD v2026-04-24-V1-BLOB-HEXDUMP");
            Log.Warning("  FEATURE: V1 blobstore migration with raw 64-byte hex dump of 3 sample blobs + fast 200-row overlap check (no slow temp-table). One last sanity check before declaring 25k items unrecoverable.");
        Log.Warning("═══════════════════════════════════════════════════════════════");

        // Run Arr History Tester if requested
        if (args.Contains("--test-arr-history"))
        {
            await ArrHistoryTester.RunAsync(args).ConfigureAwait(false);
            return;
        }

        // Run repair simulation if requested
        if (args.Contains("--simulate-repair"))
        {
            await RepairSimulation.RunAsync().ConfigureAwait(false);
            return;
        }

        // Run magic test if requested
        if (args.Contains("--magic-test"))
        {
            await MagicTester.RunAsync(args).ConfigureAwait(false);
            return;
        }

        // Run full nzb test if requested
        if (args.Contains("--test-full-nzb"))
        {
            await FullNzbTester.RunAsync(args).ConfigureAwait(false);
            return;
        }

        // Run test nzb extraction
        if (args.Contains("--extract-test-nzbs"))
        {
            await ExtractTestNzbs.RunAsync(args).ConfigureAwait(false);
            return;
        }

        // Run mock benchmark
        if (args.Contains("--mock-benchmark"))
        {
            await MockBenchmark.RunAsync(args).ConfigureAwait(false);
            return;
        }

        // Run WebDAV performance tester
        if (args.Contains("--test-webdav"))
        {
            await WebDavTester.RunAsync(args).ConfigureAwait(false);
            return;
        }

        // Run database NZB performance tester
        if (args.Contains("--test-db-nzb"))
        {
            await NzbFromDbTester.RunAsync(args).ConfigureAwait(false);
            return;
        }

        // Run Usenet connectivity tester
        if (args.Contains("--test-usenet"))
        {
            await UsenetConnectivityTester.RunAsync(args).ConfigureAwait(false);
            return;
        }

        // initialize database
        await using var databaseContext = new DavDatabaseContext();

        // run database migration, if necessary.
        if (args.Contains("--db-migration"))
        {
            Log.Warning("Starting database migration with PRAGMA optimizations...");

            // Apply PRAGMA optimizations for faster migrations (5-10x speedup)
            Log.Warning("  → Applying PRAGMA journal_mode = WAL");
            await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;").ConfigureAwait(false);

            Log.Warning("  → Applying PRAGMA synchronous = NORMAL");
            await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA synchronous = NORMAL;").ConfigureAwait(false);

            Log.Warning("  → Applying PRAGMA cache_size = -64000 (64MB cache)");
            await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA cache_size = -64000;").ConfigureAwait(false);

            Log.Warning("  → Applying PRAGMA temp_store = MEMORY");
            await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA temp_store = MEMORY;").ConfigureAwait(false);

            Log.Warning("  → Applying PRAGMA mmap_size = 1610612736 (1.5GB)");
            await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA mmap_size = 1610612736;").ConfigureAwait(false); // 1.5GB - covers full DB

            // Clear stale migration locks from previous failed attempts
            Log.Warning("  → Clearing any stale migration locks...");
            try
            {
                await databaseContext.Database.ExecuteSqlRawAsync("DELETE FROM __EFMigrationsLock WHERE 1=1;").ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Table might not exist yet, ignore
            }

            await EnsureMigrationDropIndexPrerequisitesAsync(databaseContext).ConfigureAwait(false);
            await EnsureAddHistoryCleanupMigrationCompatibilityAsync(databaseContext).ConfigureAwait(false);
            await EnsureQueueCategoryFileNameIndexMigrationCompatibilityAsync(databaseContext).ConfigureAwait(false);

            Log.Warning("  → Running migrations...");
            var argIndex = args.ToList().IndexOf("--db-migration");
            var targetMigration = args.Length > argIndex + 1 ? args[argIndex + 1] : null;
            await databaseContext.Database.MigrateAsync(targetMigration).ConfigureAwait(false);
            await EnsureSchemaCompatibilityAsync(databaseContext).ConfigureAwait(false);

            Log.Warning("Database migration finished successfully!");
            return;
        }

        // Apply runtime database optimizations for better query performance
        Log.Debug("Applying database runtime optimizations...");
        await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;").ConfigureAwait(false);
        await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA synchronous = NORMAL;").ConfigureAwait(false);
        await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA cache_size = -64000;").ConfigureAwait(false);
        await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA temp_store = MEMORY;").ConfigureAwait(false);
        await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA mmap_size = 1610612736;").ConfigureAwait(false); // 1.5GB - covers full DB
        await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout = 5000;").ConfigureAwait(false);
        await EnsureSchemaCompatibilityAsync(databaseContext).ConfigureAwait(false);

        // initialize the config-manager
        var configManager = new ConfigManager();
        await configManager.LoadConfig().ConfigureAwait(false);

        // Optional startup VACUUM (reclaims disk space, useful after large deletions)
        if (configManager.IsStartupVacuumEnabled())
        {
            Log.Warning("Running startup VACUUM (this may take a while for large databases)...");
            await databaseContext.Database.ExecuteSqlRawAsync("VACUUM;").ConfigureAwait(false);
            Log.Warning("Startup VACUUM completed.");
        }

        // Sync log level from config
        var configLevel = configManager.GetLogLevel();
        if (configLevel != null) levelSwitch.MinimumLevel = configLevel.Value;

        // Update log level on config change
        configManager.OnConfigChanged += (_, eventArgs) =>
        {
            if (eventArgs.NewConfig.TryGetValue("general.log-level", out var val)
                && Enum.TryParse<LogEventLevel>(val, true, out var newLevel))
            {
                levelSwitch.MinimumLevel = newLevel;
                Log.Information($"Log level updated to {newLevel}");
            }
        };

        // Set initial concurrent buffered stream cap
        BufferedSegmentStream.SetMaxConcurrentStreams(configManager.GetMaxConcurrentBufferedStreams());

        // Update on config change
        configManager.OnConfigChanged += (_, eventArgs) =>
        {
            if (eventArgs.NewConfig.ContainsKey("usenet.max-concurrent-buffered-streams"))
            {
                BufferedSegmentStream.SetMaxConcurrentStreams(configManager.GetMaxConcurrentBufferedStreams());
            }
        };

        // initialize websocket-manager
        var websocketManager = new WebsocketManager();

        // initialize webapp
        var builder = WebApplication.CreateBuilder(args);
        var maxRequestBodySize = EnvironmentUtil.GetLongVariable("MAX_REQUEST_BODY_SIZE") ?? 100 * 1024 * 1024;
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxRequestBodySize);
        builder.Host.UseSerilog();
        builder.Services.AddControllers();
        builder.Services.AddHealthChecks();
        builder.Services.AddHttpClient("RcloneRc");
        builder.Services
            .AddWebdavBasicAuthentication(configManager)
            .AddSingleton(configManager)
            .AddSingleton(websocketManager)
            .AddSingleton<BandwidthService>()
            .AddSingleton<NzbProviderAffinityService>()
            .AddSingleton<ProviderErrorService>()
            .AddSingleton<UsenetStreamingClient>()
            .AddSingleton<QueueManager>()
            .AddSingleton<ArrMonitoringService>()
            .AddSingleton<HealthCheckService>()
            .AddSingleton<NzbAnalysisService>()
            .AddSingleton<MediaAnalysisService>()
            .AddSingleton<RcloneRcService>()
            .AddSingleton<StreamingConnectionLimiter>()
            .AddHostedService<DatabaseMaintenanceService>()
            .AddHostedService<HistoryCleanupService>()
            .AddScoped<DavDatabaseContext>()
            .AddScoped<DavDatabaseClient>()
            .AddScoped<DatabaseStore>()
            .AddScoped<IStore, DatabaseStore>()
            .AddScoped<GetAndHeadHandlerPatch>()
            .AddScoped<SabApiController>()
            .AddNWebDav(opts =>
            {
                opts.Handlers["GET"] = typeof(GetAndHeadHandlerPatch);
                opts.Handlers["HEAD"] = typeof(GetAndHeadHandlerPatch);
                opts.Filter = opts.GetFilter();
                opts.RequireAuthentication = !WebApplicationAuthExtensions
                    .IsWebdavAuthDisabled();
            });

        // force instantiation of services
        var app = builder.Build();

        // Wire rclone vfs/forget into DavDatabaseContext SaveChangesAsync
        var rcloneService = app.Services.GetRequiredService<RcloneRcService>();
        DavDatabaseContext.VfsForgetCallback = paths => rcloneService.ForgetAsync(paths);

        app.Services.GetRequiredService<ArrMonitoringService>();
        app.Services.GetRequiredService<HealthCheckService>();
        app.Services.GetRequiredService<BandwidthService>();

        // Backfill JobNames for missing article events (Background, delayed)
        _ = Task.Run(async () =>
        {
            // Wait for 10 seconds to allow application to start
            await Task.Delay(TimeSpan.FromSeconds(10), app.Lifetime.ApplicationStopping);
            
            var providerErrorService = app.Services.GetRequiredService<ProviderErrorService>();
            
            // Critical for UI performance
            await providerErrorService
                .BackfillSummariesAsync(app.Lifetime.ApplicationStopping);

            await providerErrorService
                .BackfillDavItemIdsAsync(app.Lifetime.ApplicationStopping);

            // Merge duplicate summaries (e.g., "Movie" and "Movie.mkv" -> single entry)
            await providerErrorService
                .MergeDuplicateSummariesAsync(app.Lifetime.ApplicationStopping);

            await providerErrorService
                .CleanupOrphanedErrorsAsync(app.Lifetime.ApplicationStopping);

            // Start the OrganizedLinksUtil refresh service after initial setup
            OrganizedLinksUtil.StartRefreshService(app.Services, app.Services.GetRequiredService<ConfigManager>(), app.Lifetime.ApplicationStopping);

            // Initial call to InitializeAsync is part of the refresh service now,
            // so we don't need a separate call here. The refresh service will trigger it.
        }, app.Lifetime.ApplicationStopping);

        // run
        app.UseMiddleware<ExceptionMiddleware>();
        // ReservedConnectionsMiddleware removed - using GlobalOperationLimiter instead
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.MapHealthChecks("/health");
        app.Map("/ws", websocketManager.HandleRoute);
        app.MapControllers();
        app.UseWebdavBasicAuthentication();
        app.UseNWebDav();
        app.Lifetime.ApplicationStopping.Register(SigtermUtil.Cancel);
        await app.RunAsync().ConfigureAwait(false);
    }

    private static async Task EnsureSchemaCompatibilityAsync(DavDatabaseContext databaseContext)
    {
        await EnsureColumnExistsAsync(databaseContext, "HistoryItems", "DownloadDirId", "TEXT").ConfigureAwait(false);
        await EnsureColumnExistsAsync(databaseContext, "HistoryCleanupItems", "DownloadDirId", "TEXT").ConfigureAwait(false);
        await databaseContext.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_HistoryItems_Category_DownloadDirId ON HistoryItems (Category, DownloadDirId);")
            .ConfigureAwait(false);

        await EnsureDavItemsHistoryItemIdForeignKeyCompatibilityAsync(databaseContext).ConfigureAwait(false);

        // Fix legacy v1 schema: SubType should be nullable for new DavItems
        // The issue: v1 databases have SubType NOT NULL, but v2 EF model doesn't know about it,
        // causing INSERT failures when new DavItems are created without SubType value
        if (await ColumnExistsAsync(databaseContext, "DavItems", "SubType").ConfigureAwait(false))
        {
            try
            {
                Log.Warning("[SchemaCompat] Fixing legacy SubType NOT NULL constraint for DavItems");
                
                // Disable foreign keys during table recreation
                await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;").ConfigureAwait(false);
                
                // Create backup of existing data
                await databaseContext.Database.ExecuteSqlRawAsync(
                    "CREATE TABLE IF NOT EXISTS DavItems_backup AS SELECT * FROM DavItems;")
                    .ConfigureAwait(false);
                
                // Drop old table
                await databaseContext.Database.ExecuteSqlRawAsync("DROP TABLE DavItems;").ConfigureAwait(false);
                
                // Recreate with nullable SubType
                await databaseContext.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE DavItems (
                        Id TEXT NOT NULL PRIMARY KEY,
                        ParentId TEXT,
                        Name TEXT NOT NULL,
                        FileSize INTEGER,
                        Type INTEGER NOT NULL,
                        CreatedAt TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
                        Path TEXT NOT NULL DEFAULT '',
                        IdPrefix TEXT NOT NULL DEFAULT '',
                        LastHealthCheck INTEGER,
                        NextHealthCheck INTEGER,
                        ReleaseDate INTEGER,
                        MediaInfo TEXT,
                        CorruptionReason TEXT,
                        IsCorrupted INTEGER NOT NULL DEFAULT 0,
                        HistoryItemId TEXT,
                        SubType INTEGER,
                        CONSTRAINT FK_DavItems_DavItems_ParentId FOREIGN KEY (ParentId) REFERENCES DavItems(Id) ON DELETE CASCADE
                    );
                    """)
                    .ConfigureAwait(false);
                
                // Restore data - explicitly select only the columns we need, in the right order
                // This handles cases where the backup may have extra or differently-ordered columns
                await databaseContext.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO DavItems 
                    (Id, ParentId, Name, FileSize, Type, CreatedAt, Path, IdPrefix, 
                     LastHealthCheck, NextHealthCheck, ReleaseDate, MediaInfo, 
                     CorruptionReason, IsCorrupted, HistoryItemId, SubType)
                    SELECT 
                        COALESCE(Id, ''),
                        ParentId,
                        COALESCE(Name, ''),
                        FileSize,
                        COALESCE(Type, 1),
                        COALESCE(CreatedAt, '0001-01-01 00:00:00'),
                        COALESCE(Path, ''),
                        COALESCE(IdPrefix, ''),
                        LastHealthCheck,
                        NextHealthCheck,
                        ReleaseDate,
                        MediaInfo,
                        CorruptionReason,
                        COALESCE(IsCorrupted, 0),
                        HistoryItemId,
                        SubType
                    FROM DavItems_backup;
                    """)
                    .ConfigureAwait(false);
                
                // Recreate indexes
                await databaseContext.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX IX_DavItems_ParentId_Name ON DavItems (ParentId, Name);")
                    .ConfigureAwait(false);
                await databaseContext.Database.ExecuteSqlRawAsync(
                    "CREATE INDEX IX_DavItems_IdPrefix_Type ON DavItems (IdPrefix, Type);")
                    .ConfigureAwait(false);
                await databaseContext.Database.ExecuteSqlRawAsync(
                    "CREATE INDEX IX_DavItems_Path ON DavItems (Path);")
                    .ConfigureAwait(false);
                
                // Drop backup
                await databaseContext.Database.ExecuteSqlRawAsync("DROP TABLE DavItems_backup;").ConfigureAwait(false);
                
                // Re-enable foreign keys
                await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;").ConfigureAwait(false);
                
                Log.Warning("[SchemaCompat] Successfully made SubType nullable");
            }
            catch (Exception ex)
            {
                Log.Warning($"[SchemaCompat] Error fixing SubType: {ex.Message}. Continuing anyway...");
                // Try to re-enable foreign keys even on error
                try { await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;").ConfigureAwait(false); } catch { }
            }
        }

        await EnsureQueueNzbContentsConsistencyAsync(databaseContext).ConfigureAwait(false);
        await MigrateDavItemsFromBlobstoreAsync(databaseContext).ConfigureAwait(false);
        await NormalizeLegacyDavItemTypesAsync(databaseContext).ConfigureAwait(false);
        await ReportOrphanedDavItemsAsync(databaseContext).ConfigureAwait(false);
    }

    private static async Task EnsureMigrationDropIndexPrerequisitesAsync(DavDatabaseContext databaseContext)
    {
        if (await TableExistsAsync(databaseContext, "DavItems").ConfigureAwait(false)
            && await ColumnsExistAsync(databaseContext, "DavItems", "Type", "NextHealthCheck", "ReleaseDate", "Id").ConfigureAwait(false))
        {
            await databaseContext.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS IX_DavItems_Type_NextHealthCheck_ReleaseDate_Id ON DavItems (Type, NextHealthCheck, ReleaseDate, Id);")
                .ConfigureAwait(false);
        }

        if (await TableExistsAsync(databaseContext, "QueueItems").ConfigureAwait(false)
            && await ColumnsExistAsync(databaseContext, "QueueItems", "FileName").ConfigureAwait(false))
        {
            await databaseContext.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS IX_QueueItems_FileName ON QueueItems (FileName);")
                .ConfigureAwait(false);
        }
    }

    private static async Task EnsureAddHistoryCleanupMigrationCompatibilityAsync(DavDatabaseContext databaseContext)
    {
        const string migrationId = "20260313005733_AddHistoryCleanup";

        if (!await TableExistsAsync(databaseContext, "__EFMigrationsHistory").ConfigureAwait(false))
        {
            return;
        }

        if (await IsMigrationAppliedAsync(databaseContext, migrationId).ConfigureAwait(false))
        {
            return;
        }

        await using var connection = databaseContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync().ConfigureAwait(false);
        }

        var hasHistoryItemId = await ColumnExistsAsync(databaseContext, "DavItems", "HistoryItemId").ConfigureAwait(false);
        var hasHistoryCleanupItems = await TableExistsAsync(databaseContext, "HistoryCleanupItems").ConfigureAwait(false);
        if (!hasHistoryItemId || !hasHistoryCleanupItems)
        {
            return;
        }

        // Schema already contains what this migration adds. Create expected indexes and mark migration as applied.
        Log.Warning("[SchemaCompat] Detected drifted schema for AddHistoryCleanup. Marking migration as applied.");
        await databaseContext.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_DavItems_HistoryItemId_Type_CreatedAt ON DavItems (HistoryItemId, Type, CreatedAt);")
            .ConfigureAwait(false);
        await databaseContext.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_DavItems_Type_HistoryItemId_NextHealthCheck_ReleaseDate_Id ON DavItems (Type, HistoryItemId, NextHealthCheck, ReleaseDate, Id);")
            .ConfigureAwait(false);

        string productVersion;
        await using var productVersionCmd = connection.CreateCommand();
        productVersionCmd.CommandText = "SELECT ProductVersion FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1";
        var productVersionObj = await productVersionCmd.ExecuteScalarAsync().ConfigureAwait(false);
        productVersion = productVersionObj?.ToString() ?? "10.0.0";

        await using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@migrationId, @productVersion)";
        var idParam = insertCmd.CreateParameter();
        idParam.ParameterName = "@migrationId";
        idParam.Value = migrationId;
        insertCmd.Parameters.Add(idParam);
        var pvParam = insertCmd.CreateParameter();
        pvParam.ParameterName = "@productVersion";
        pvParam.Value = productVersion;
        insertCmd.Parameters.Add(pvParam);
        await insertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task EnsureQueueCategoryFileNameIndexMigrationCompatibilityAsync(DavDatabaseContext databaseContext)
    {
        const string migrationId = "20260408180402_ChangeQueueItemsFileNameIndexToCategoryFileName";

        if (!await TableExistsAsync(databaseContext, "__EFMigrationsHistory").ConfigureAwait(false)
            || !await TableExistsAsync(databaseContext, "QueueItems").ConfigureAwait(false))
        {
            return;
        }

        if (await IsMigrationAppliedAsync(databaseContext, migrationId).ConfigureAwait(false))
        {
            return;
        }

        if (!await IndexExistsAsync(databaseContext, "IX_QueueItems_Category_FileName").ConfigureAwait(false))
        {
            return;
        }

        // Drift case: index already exists before migration runs, so migration fails while creating it.
        // Drop it so the migration can recreate it cleanly.
        Log.Warning("[SchemaCompat] Detected pre-existing IX_QueueItems_Category_FileName before migration. Dropping for replay.");
        await databaseContext.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS IX_QueueItems_Category_FileName;")
            .ConfigureAwait(false);
    }

    private static async Task EnsureDavItemsHistoryItemIdForeignKeyCompatibilityAsync(DavDatabaseContext databaseContext)
    {
        if (!await TableExistsAsync(databaseContext, "DavItems").ConfigureAwait(false)
            || !await ColumnExistsAsync(databaseContext, "DavItems", "HistoryItemId").ConfigureAwait(false))
        {
            return;
        }

        var hasUnexpectedHistoryItemFk = false;
        var connection = databaseContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync().ConfigureAwait(false);
        }

        await using (var fkCmd = connection.CreateCommand())
        {
            fkCmd.CommandText = "PRAGMA foreign_key_list('DavItems')";
            await using var reader = await fkCmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var targetTable = reader[2]?.ToString(); // table
                var fromColumn = reader[3]?.ToString();  // from
                if (string.Equals(targetTable, "HistoryItems", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(fromColumn, "HistoryItemId", StringComparison.OrdinalIgnoreCase))
                {
                    hasUnexpectedHistoryItemFk = true;
                    break;
                }
            }
        }

        if (!hasUnexpectedHistoryItemFk)
        {
            return;
        }

        Log.Warning("[SchemaCompat] Removing unexpected FK DavItems.HistoryItemId -> HistoryItems.Id");
        await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;").ConfigureAwait(false);
        try
        {
            await databaseContext.Database.ExecuteSqlRawAsync(
                "CREATE TABLE IF NOT EXISTS DavItems_backup AS SELECT * FROM DavItems;")
                .ConfigureAwait(false);
            await databaseContext.Database.ExecuteSqlRawAsync("DROP TABLE DavItems;").ConfigureAwait(false);
            await databaseContext.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE DavItems (
                    Id TEXT NOT NULL PRIMARY KEY,
                    ParentId TEXT,
                    Name TEXT NOT NULL,
                    FileSize INTEGER,
                    Type INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
                    Path TEXT NOT NULL DEFAULT '',
                    IdPrefix TEXT NOT NULL DEFAULT '',
                    LastHealthCheck INTEGER,
                    NextHealthCheck INTEGER,
                    ReleaseDate INTEGER,
                    MediaInfo TEXT,
                    CorruptionReason TEXT,
                    IsCorrupted INTEGER NOT NULL DEFAULT 0,
                    HistoryItemId TEXT,
                    SubType INTEGER,
                    CONSTRAINT FK_DavItems_DavItems_ParentId FOREIGN KEY (ParentId) REFERENCES DavItems(Id) ON DELETE CASCADE
                );
                """)
                .ConfigureAwait(false);

            await databaseContext.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO DavItems 
                (Id, ParentId, Name, FileSize, Type, CreatedAt, Path, IdPrefix,
                 LastHealthCheck, NextHealthCheck, ReleaseDate, MediaInfo,
                 CorruptionReason, IsCorrupted, HistoryItemId, SubType)
                SELECT
                    COALESCE(Id, ''),
                    ParentId,
                    COALESCE(Name, ''),
                    FileSize,
                    COALESCE(Type, 1),
                    COALESCE(CreatedAt, '0001-01-01 00:00:00'),
                    COALESCE(Path, ''),
                    COALESCE(IdPrefix, ''),
                    LastHealthCheck,
                    NextHealthCheck,
                    ReleaseDate,
                    MediaInfo,
                    CorruptionReason,
                    COALESCE(IsCorrupted, 0),
                    HistoryItemId,
                    SubType
                FROM DavItems_backup;
                """)
                .ConfigureAwait(false);

            await databaseContext.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX IF NOT EXISTS IX_DavItems_ParentId_Name ON DavItems (ParentId, Name);")
                .ConfigureAwait(false);
            await databaseContext.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS IX_DavItems_IdPrefix_Type ON DavItems (IdPrefix, Type);")
                .ConfigureAwait(false);
            await databaseContext.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS IX_DavItems_Path ON DavItems (Path);")
                .ConfigureAwait(false);

            await databaseContext.Database.ExecuteSqlRawAsync("DROP TABLE DavItems_backup;").ConfigureAwait(false);
        }
        finally
        {
            await databaseContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;").ConfigureAwait(false);
        }
    }

    private static async Task<bool> IsMigrationAppliedAsync(DavDatabaseContext databaseContext, string migrationId)
    {
        var connection = databaseContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync().ConfigureAwait(false);
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM __EFMigrationsHistory WHERE MigrationId = @migrationId";
        var param = cmd.CreateParameter();
        param.ParameterName = "@migrationId";
        param.Value = migrationId;
        cmd.Parameters.Add(param);

        var countObj = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        var count = Convert.ToInt32(countObj ?? 0);
        return count > 0;
    }

    private static async Task<bool> IndexExistsAsync(DavDatabaseContext databaseContext, string indexName)
    {
        var connection = databaseContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync().ConfigureAwait(false);
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = @indexName";
        var param = cmd.CreateParameter();
        param.ParameterName = "@indexName";
        param.Value = indexName;
        cmd.Parameters.Add(param);

        var countObj = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        var count = Convert.ToInt32(countObj ?? 0);
        return count > 0;
    }

    private static async Task EnsureQueueNzbContentsConsistencyAsync(DavDatabaseContext databaseContext)
    {
        if (!await TableExistsAsync(databaseContext, "QueueItems").ConfigureAwait(false))
        {
            return;
        }

        if (!await TableExistsAsync(databaseContext, "QueueNzbContents").ConfigureAwait(false))
        {
            return;
        }

        if (await ColumnExistsAsync(databaseContext, "QueueItems", "NzbContents").ConfigureAwait(false))
        {
            var backfilled = await databaseContext.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO QueueNzbContents (Id, NzbContents)
                SELECT q.Id, q.NzbContents
                FROM QueueItems q
                LEFT JOIN QueueNzbContents c ON c.Id = q.Id
                WHERE c.Id IS NULL AND q.NzbContents IS NOT NULL
                """)
                .ConfigureAwait(false);

            if (backfilled > 0)
            {
                Log.Warning("[SchemaCompat] Backfilled {Count} QueueNzbContents rows from legacy QueueItems.NzbContents", backfilled);
            }
        }

        var recoveredFromBlob = await RecoverQueueNzbContentsFromBlobStoreAsync(databaseContext).ConfigureAwait(false);
        if (recoveredFromBlob > 0)
        {
            Log.Warning("[SchemaCompat] Recovered {Count} QueueNzbContents rows from legacy blob files", recoveredFromBlob);
        }

        var removedOrphans = await databaseContext.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM QueueItems
            WHERE Id IN (
                SELECT q.Id
                FROM QueueItems q
                LEFT JOIN QueueNzbContents c ON c.Id = q.Id
                WHERE c.Id IS NULL
            )
            """)
            .ConfigureAwait(false);

        if (removedOrphans > 0)
        {
            Log.Warning("[SchemaCompat] Removed {Count} orphaned QueueItems missing QueueNzbContents", removedOrphans);
        }
    }

    private static async Task<int> RecoverQueueNzbContentsFromBlobStoreAsync(DavDatabaseContext databaseContext)
    {
        if (!await TableExistsAsync(databaseContext, "QueueItems").ConfigureAwait(false)
            || !await TableExistsAsync(databaseContext, "QueueNzbContents").ConfigureAwait(false))
        {
            return 0;
        }

        var connection = databaseContext.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync().ConfigureAwait(false);
        }

        try
        {
            var missingIds = new List<Guid>();
            await using (var selectMissing = connection.CreateCommand())
            {
                selectMissing.CommandText =
                    """
                    SELECT q.Id
                    FROM QueueItems q
                    LEFT JOIN QueueNzbContents c ON c.Id = q.Id
                    WHERE c.Id IS NULL
                    """;

                await using var reader = await selectMissing.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (Guid.TryParse(reader[0]?.ToString(), out var id))
                    {
                        missingIds.Add(id);
                    }
                }
            }

            if (missingIds.Count == 0)
            {
                return 0;
            }

            var recovered = 0;
            foreach (var id in missingIds)
            {
                var blobPath = GetLegacyBlobPath(id);
                if (!File.Exists(blobPath))
                {
                    continue;
                }

                string? nzbContents;
                await using (var fileStream = File.OpenRead(blobPath))
                using (var streamReader = new StreamReader(fileStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                {
                    nzbContents = await streamReader.ReadToEndAsync().ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(nzbContents))
                {
                    continue;
                }

                await using var insert = connection.CreateCommand();
                insert.CommandText =
                    """
                    INSERT OR IGNORE INTO QueueNzbContents (Id, NzbContents)
                    VALUES (@id, @nzb)
                    """;

                var idParam = insert.CreateParameter();
                idParam.ParameterName = "@id";
                idParam.Value = id;
                insert.Parameters.Add(idParam);

                var nzbParam = insert.CreateParameter();
                nzbParam.ParameterName = "@nzb";
                nzbParam.Value = nzbContents;
                insert.Parameters.Add(nzbParam);

                recovered += await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            return recovered;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static string GetLegacyBlobPath(Guid id)
    {
        var guidWithoutHyphens = id.ToString("N");
        var firstTwo = guidWithoutHyphens[..2];
        var nextTwo = guidWithoutHyphens.Substring(2, 2);
        return Path.Combine(DavDatabaseContext.ConfigPath, "blobs", firstTwo, nextTwo, id.ToString());
    }

    /// <summary>
    /// Migrates DavItems metadata from upstream nzbdav-dev's filesystem blobstore (Zstd-compressed
    /// MemoryPack) into this fork's relational <c>DavNzbFiles</c> / <c>DavRarFiles</c> /
    /// <c>DavMultipartFiles</c> tables.
    ///
    /// Blob lookup strategy (BLOB-DRIVEN, NOT id-derived path):
    /// This fork once had a <c>FileBlobId</c> column on <c>DavItems</c> that mapped each
    /// DavItem.Id to its blob filename (which is a different GUID). That column has since been
    /// dropped, destroying the only DB-side link between an item and its blob. Computing the
    /// blob path from <c>DavItem.Id</c> alone (as upstream does) does NOT work on this fork.
    ///
    /// Instead, we recover the mapping from the BLOBS THEMSELVES: every upstream metadata blob
    /// (Nzb / Rar / Multipart) embeds <c>[MemoryPackOrder(0)] Guid Id = DavItem.Id</c> as its
    /// first member. We scan the blobs directory once, decompress each file, read 17 bytes, and
    /// build an in-memory <c>Dictionary&lt;Guid, string&gt;</c> from DavItem.Id → blob path.
    /// Candidates are then resolved via lookup, not path-derivation.
    ///
    /// Trigger conditions (idempotent — safe to run on every startup):
    ///   1. The blobs directory exists at <c>{ConfigPath}/blobs</c>.
    ///   2. There is at least one DavItem that "should" have metadata (SubType 201/202/203 OR
    ///      already-promoted Type 3/4/6) but is missing its row in the corresponding metadata table.
    ///
    /// For each candidate item:
    ///   - Looks up the blob path in the pre-built index.
    ///   - If found: deserializes via the appropriate Upstream contract and inserts the metadata row.
    ///   - If NOT found: flagged <c>IsCorrupted = true</c> with a clear <c>CorruptionReason</c> and
    ///     reverted to <c>Type=2</c> (folder) so it isn't surfaced as a broken file.
    /// </summary>
    private static async Task MigrateDavItemsFromBlobstoreAsync(DavDatabaseContext databaseContext)
    {
        if (!await TableExistsAsync(databaseContext, "DavItems").ConfigureAwait(false)) return;

        var blobsRoot = Path.Combine(DavDatabaseContext.ConfigPath, "blobs");
        if (!Directory.Exists(blobsRoot)) return;

        // One-time recovery: a previous build of this fork had a broken BlobStoreReader that
        // could not deserialize ANY zstd blob and therefore mass-flagged items IsCorrupted=1
        // with the reason "Upstream blob file missing or unreadable…". If those items' blob
        // files actually still exist on disk, clear the flag so the (now-fixed) reader gets a
        // chance to retry them. We only touch our own marker (by exact CorruptionReason prefix)
        // to avoid clobbering corruption flags set by anything else.
        var resetCount = await databaseContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE DavItems
            SET IsCorrupted = 0,
                CorruptionReason = NULL
            WHERE IsCorrupted = 1
              AND (
                    CorruptionReason LIKE 'Upstream blob file missing or unreadable%'
                 OR CorruptionReason LIKE 'Upstream blob file is missing from disk%'
                 OR CorruptionReason LIKE 'Upstream blob file at %could not be deserialized%'
              )
            """).ConfigureAwait(false);
        if (resetCount > 0)
        {
            Log.Warning(
                "[BlobstoreMigration] Reset IsCorrupted=0 on {Count} DavItems previously marked " +
                "by a broken migration so the fixed reader can retry them.", resetCount);
        }

        var hasSubType = await ColumnExistsAsync(databaseContext, "DavItems", "SubType").ConfigureAwait(false);

        // Collect candidate work items. Two complementary cases:
        //   A. SubType 201/202/203 with Type=2 (untouched legacy items).
        //   B. Type 3/4/6 with no metadata row (force-promoted by an earlier buggy v2 build).
        // Case B is what bites users who already ran the previous broken migration — we MUST
        // pick those up here too, otherwise streaming continues to fail forever.
        var candidates = await LoadBlobstoreMigrationCandidatesAsync(databaseContext, hasSubType).ConfigureAwait(false);
        if (candidates.Count == 0) return;

        Log.Warning(
            "[BlobstoreMigration] Found {Count} DavItems needing recovery from upstream blobstore " +
            "(blobs root: {BlobsRoot}). Building blob index…",
            candidates.Count, blobsRoot);

        // Pre-scan ALL blob files and build DavItem.Id → blob path index. This is the workaround
        // for the dropped FileBlobId column: the embedded Id inside each blob is now the only
        // remaining link between DavItems and blob files.
        var indexStart = DateTimeOffset.UtcNow;
        var blobIndex = await BuildBlobIdIndexAsync(blobsRoot).ConfigureAwait(false);
        Log.Warning(
            "[BlobstoreMigration] Indexed {Indexed} blob files in {Elapsed:c}. Resolving {Count} candidates…",
            blobIndex.Count, DateTimeOffset.UtcNow - indexStart, candidates.Count);

        // Sanity check: dump 5 sample indexed Ids + 5 sample candidate Ids + count overlap with
        // DavItems. If overlap is 0, the embedded-Id assumption is wrong (e.g. byte order, wrong
        // MemoryPack offset, or the embedded field is something other than DavItem.Id).
        var overlap = await DiagnoseBlobIndexAsync(databaseContext, blobIndex, candidates).ConfigureAwait(false);
        if (overlap == 0)
        {
            Log.Warning(
                "[BlobstoreMigration] ABORTING: zero indexed Ids match any DavItem. The embedded-Id " +
                "assumption is wrong on this database. Skipping migration to avoid mass-orphaning " +
                "{Count} items. See [BlobIndexDiag] output above for sample Ids — please report this.",
                candidates.Count);
            return;
        }

        var migratedNzb = 0;
        var migratedRar = 0;
        var migratedMultipart = 0;
        var orphaned = 0;
        var failed = 0;
        var unknownKind = 0;

        const int BatchSize = 500;
        var processed = 0;
        var startedAt = DateTimeOffset.UtcNow;

        foreach (var batch in candidates.Chunk(BatchSize))
        {
            foreach (var (davItemId, currentType, subType) in batch)
            {
                // Pick deserializer kind: SubType is authoritative when present (legacy untouched
                // items); otherwise fall back to the already-promoted Type for items the buggy
                // earlier v2 build force-promoted before the SubType column was dropped.
                //   201 / Type=3 → Nzb
                //   202 / Type=4 → Rar
                //   203 / Type=6 → Multipart
                var kind = subType switch
                {
                    201 => MigrationKind.Nzb,
                    202 => MigrationKind.Rar,
                    203 => MigrationKind.Multipart,
                    _ => currentType switch
                    {
                        3 => MigrationKind.Nzb,
                        4 => MigrationKind.Rar,
                        6 => MigrationKind.Multipart,
                        _ => MigrationKind.Unknown,
                    },
                };

                // Resolve blob via the pre-built index (NOT by computing path from DavItem.Id).
                if (!blobIndex.TryGetValue(davItemId, out var blobPath))
                {
                    blobPath = null;
                }

                try
                {
                    var inserted = (kind, blobPath) switch
                    {
                        (MigrationKind.Unknown, _) => -1,
                        (_, null) => 0,
                        (MigrationKind.Nzb, _) => await TryMigrateNzbAsync(databaseContext, davItemId, blobPath).ConfigureAwait(false),
                        (MigrationKind.Rar, _) => await TryMigrateRarAsync(databaseContext, davItemId, blobPath).ConfigureAwait(false),
                        (MigrationKind.Multipart, _) => await TryMigrateMultipartAsync(databaseContext, davItemId, blobPath).ConfigureAwait(false),
                    };

                    switch (kind)
                    {
                        case MigrationKind.Nzb when inserted == 1: migratedNzb++; break;
                        case MigrationKind.Rar when inserted == 1: migratedRar++; break;
                        case MigrationKind.Multipart when inserted == 1: migratedMultipart++; break;
                        case MigrationKind.Nzb or MigrationKind.Rar or MigrationKind.Multipart when inserted == 0:
                            await MarkOrphanAsync(databaseContext, davItemId,
                                blobPath is null
                                    ? "Upstream blob file is missing from disk (no blob found whose " +
                                      "embedded Id matches this DavItem). Re-download via Sonarr/Radarr to recover."
                                    : $"Upstream blob file at {blobPath} could not be deserialized as " +
                                      $"the expected type ({kind}). Re-download via Sonarr/Radarr to recover.")
                                .ConfigureAwait(false);
                            orphaned++;
                            break;
                        default:
                            // Unknown kind — neither SubType nor Type identified the item.
                            await MarkOrphanAsync(databaseContext, davItemId,
                                $"Could not determine blobstore item kind (Type={currentType}, " +
                                $"SubType={subType?.ToString() ?? "null"}); this fork does not " +
                                "know how to migrate this item.")
                                .ConfigureAwait(false);
                            unknownKind++;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[BlobstoreMigration] Unhandled error migrating item {DavItemId} " +
                        "(blob={BlobPath}, kind={Kind})", davItemId, blobPath ?? "<not-indexed>", kind);
                    failed++;
                }

                processed++;
            }

            // Save the batch and report progress.
            await databaseContext.SaveChangesAsync().ConfigureAwait(false);
            Log.Warning(
                "[BlobstoreMigration] Progress: {Processed}/{Total} (Nzb={Nzb}, Rar={Rar}, " +
                "Multipart={Multipart}, Orphaned={Orphaned}, Failed={Failed})",
                processed, candidates.Count, migratedNzb, migratedRar, migratedMultipart, orphaned, failed);
        }

        var elapsed = DateTimeOffset.UtcNow - startedAt;
        Log.Warning(
            "[BlobstoreMigration] Complete in {Elapsed:c}. Migrated: Nzb={Nzb}, Rar={Rar}, " +
            "Multipart={Multipart}. Orphaned (no recoverable blob)={Orphaned}, " +
            "UnknownKind={UnknownKind}, Failed={Failed}.",
            elapsed, migratedNzb, migratedRar, migratedMultipart, orphaned, unknownKind, failed);
    }

    /// <summary>
    /// Walks the blobs directory and builds a <c>DavItem.Id → blob path</c> index by extracting
    /// the embedded Id from each blob's MemoryPack payload. Skips files whose payload doesn't
    /// look like a v1 metadata blob (e.g. raw NZB XML, HTML error pages, queue contents).
    /// Runs in parallel for throughput.
    /// </summary>
    private static async Task<Dictionary<Guid, string>> BuildBlobIdIndexAsync(string blobsRoot)
    {
        var index = new System.Collections.Concurrent.ConcurrentDictionary<Guid, string>();
        var allFiles = Directory.EnumerateFiles(blobsRoot, "*", SearchOption.AllDirectories).ToArray();

        // Cap parallelism to keep IO + decompression CPU usage reasonable on small hosts.
        var parallelism = Math.Max(2, Math.Min(8, Environment.ProcessorCount));
        var sem = new SemaphoreSlim(parallelism, parallelism);
        var tasks = new List<Task>(allFiles.Length);

        foreach (var file in allFiles)
        {
            await sem.WaitAsync().ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var id = await BlobStoreReader.TryReadEmbeddedIdAsync(file).ConfigureAwait(false);
                    if (id.HasValue && id.Value != Guid.Empty)
                    {
                        // Last-write-wins on duplicate Ids; in practice each DavItem.Id maps to a
                        // unique blob, so this is just defensive.
                        index[id.Value] = file;
                    }
                }
                finally
                {
                    sem.Release();
                }
            }));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new Dictionary<Guid, string>(index);
    }

    /// <summary>
    /// Diagnostic: dumps 5 sample indexed Guids + 5 sample candidate Guids + counts how many of
    /// the indexed Guids actually exist in the DavItems table (by Id). If overlap is 0, our
    /// embedded-Id assumption is wrong and we need to pivot strategy. Logged at Warning so it
    /// shows up clearly in container logs.
    /// </summary>
    private static async Task<long> DiagnoseBlobIndexAsync(
        DavDatabaseContext databaseContext,
        Dictionary<Guid, string> blobIndex,
        List<BlobstoreCandidate> candidates)
    {
        long matchCount = 0;
        try
        {
            var sampleIndexed = blobIndex.Take(5).ToList();
            var sampleCandidates = candidates.Take(5).ToList();

            Log.Warning("[BlobIndexDiag] Sample of 5 indexed (embeddedId → blobPath):");
            foreach (var (id, path) in sampleIndexed)
            {
                Log.Warning("[BlobIndexDiag]   {Id} → {Path}", id, path);
            }

            Log.Warning("[BlobIndexDiag] Sample of 5 candidate DavItem.Ids needing recovery:");
            foreach (var c in sampleCandidates)
            {
                Log.Warning("[BlobIndexDiag]   {Id} (Type={Type}, SubType={Sub})",
                    c.DavItemId, c.CurrentType, c.SubType?.ToString() ?? "null");
            }

            // Count overlap: of the 5 indexed sample Ids, how many exist in DavItems?
            var connection = databaseContext.Database.GetDbConnection();
            var shouldClose = connection.State != ConnectionState.Open;
            if (shouldClose) await connection.OpenAsync().ConfigureAwait(false);

            try
            {
                foreach (var (id, _) in sampleIndexed)
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SELECT Id, Name, Type FROM DavItems WHERE lower(Id) = lower(@id) LIMIT 1";
                    var p = cmd.CreateParameter();
                    p.ParameterName = "@id";
                    p.Value = id.ToString();
                    cmd.Parameters.Add(p);
                    await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                    if (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        Log.Warning("[BlobIndexDiag] HIT  {Id} → DavItem name={Name} type={Type}",
                            id, reader.GetString(1), reader.GetValue(2));
                    }
                    else
                    {
                        Log.Warning("[BlobIndexDiag] MISS {Id} → not found in DavItems.Id", id);
                    }
                }

                // Lightweight overlap estimate (sample first 200 indexed Ids; avoids 25k+ INSERTs).
                long sampled = 0;
                long sampleHits = 0;
                await using (var existsCmd = connection.CreateCommand())
                {
                    existsCmd.CommandText = "SELECT 1 FROM DavItems WHERE lower(Id)=lower(@id) LIMIT 1";
                    var p = existsCmd.CreateParameter();
                    p.ParameterName = "@id";
                    existsCmd.Parameters.Add(p);
                    foreach (var key in blobIndex.Keys.Take(200))
                    {
                        p.Value = key.ToString();
                        var v = await existsCmd.ExecuteScalarAsync().ConfigureAwait(false);
                        sampled++;
                        if (v is not null and not DBNull) sampleHits++;
                    }
                }
                Log.Warning(
                    "[BlobIndexDiag] Sampled {Sampled} of {IndexedTotal} indexed Ids: {Hits} match DavItems.Id",
                    sampled, blobIndex.Count, sampleHits);
                matchCount = sampleHits;

                // Hex dump first 64 decompressed bytes of 3 sample blobs so we can locate where
                // the DavItem.Id (if any) actually lives in the payload.
                Log.Warning("[BlobIndexDiag] Raw payload dumps (first 64 decompressed bytes):");
                foreach (var (id, path) in blobIndex.Take(3))
                {
                    var head = await BlobStoreReader.TryReadDecompressedHeadAsync(path, 64).ConfigureAwait(false);
                    Log.Warning("[BlobIndexDiag]   blob={Path}\n              extractedId={Id}\n              hex64={Hex}",
                        path, id, head is null ? "<null>" : Convert.ToHexString(head));
                }
            }
            finally
            {
                if (shouldClose) await connection.CloseAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[BlobIndexDiag] Diagnostic failed (non-fatal).");
        }
        return matchCount;
    }

    private enum MigrationKind { Unknown, Nzb, Rar, Multipart }

    private record struct BlobstoreCandidate(Guid DavItemId, int CurrentType, int? SubType);

    private static async Task<List<BlobstoreCandidate>> LoadBlobstoreMigrationCandidatesAsync(
        DavDatabaseContext databaseContext, bool hasSubType)
    {
        var connection = databaseContext.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync().ConfigureAwait(false);

        try
        {
            await using var cmd = connection.CreateCommand();
            // Two complementary cases unioned together:
            //   A. Untouched legacy items: SubType in (201,202,203). These typically still have
            //      Type=2 if NormalizeLegacyDavItemTypesAsync hasn't promoted them yet.
            //   B. Force-promoted-but-orphaned items: Type in (3,4,6) with no metadata row. This
            //      is the hot path for users who already ran the buggy earlier v2 build.
            //
            // We exclude items that already have a metadata row (idempotent re-runs) and items
            // already flagged corrupted (don't keep retrying known-bad items every startup).
            var subtypeClause = hasSubType
                ? "OR (d.SubType IN (201, 202, 203))"
                : string.Empty;
            cmd.CommandText =
                $"""
                SELECT d.Id, d.Type, {(hasSubType ? "d.SubType" : "NULL AS SubType")}
                FROM DavItems d
                WHERE (d.IsCorrupted = 0 OR d.IsCorrupted IS NULL)
                  AND (
                        d.Type IN (3, 4, 6)
                        {subtypeClause}
                      )
                  AND NOT EXISTS (SELECT 1 FROM DavNzbFiles n WHERE n.Id = d.Id)
                  AND NOT EXISTS (SELECT 1 FROM DavRarFiles r WHERE r.Id = d.Id)
                  AND NOT EXISTS (SELECT 1 FROM DavMultipartFiles m WHERE m.Id = d.Id)
                """;

            var results = new List<BlobstoreCandidate>();
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (!Guid.TryParse(reader[0]?.ToString(), out var davItemId)) continue;

                var currentType = 0;
                if (!reader.IsDBNull(1) && int.TryParse(reader[1]?.ToString(), out var parsedType))
                {
                    currentType = parsedType;
                }

                int? subType = null;
                if (!reader.IsDBNull(2) && int.TryParse(reader[2]?.ToString(), out var parsedSubType))
                {
                    subType = parsedSubType;
                }

                results.Add(new BlobstoreCandidate(davItemId, currentType, subType));
            }

            return results;
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <returns>1 if migrated, 0 if blob missing/unreadable, -1 if SubType unsupported.</returns>
    private static async Task<int> TryMigrateNzbAsync(
        DavDatabaseContext databaseContext, Guid davItemId, string blobPath)
    {
        var blob = await BlobStoreReader.TryReadAsync<UpstreamDavNzbFile>(blobPath).ConfigureAwait(false);
        if (blob is null) return 0;

        databaseContext.NzbFiles.Add(new DavNzbFile
        {
            Id = davItemId,
            SegmentIds = blob.SegmentIds ?? [],
            // SegmentSizes / SegmentFallbacks are additive in this fork; left null and lazily
            // populated by NzbAnalysisService on first stream open.
        });
        return 1;
    }

    private static async Task<int> TryMigrateRarAsync(
        DavDatabaseContext databaseContext, Guid davItemId, string blobPath)
    {
        var blob = await BlobStoreReader.TryReadAsync<UpstreamDavRarFile>(blobPath).ConfigureAwait(false);
        if (blob is null) return 0;

        databaseContext.RarFiles.Add(new DavRarFile
        {
            Id = davItemId,
            RarParts = (blob.RarParts ?? []).Select(p => new DavRarFile.RarPart
            {
                SegmentIds = p.SegmentIds ?? [],
                PartSize = p.PartSize,
                Offset = p.Offset,
                ByteCount = p.ByteCount,
                // ObfuscationKey is additive in this fork; not present in upstream contract.
            }).ToArray(),
        });
        return 1;
    }

    private static async Task<int> TryMigrateMultipartAsync(
        DavDatabaseContext databaseContext, Guid davItemId, string blobPath)
    {
        var blob = await BlobStoreReader.TryReadAsync<UpstreamDavMultipartFile>(blobPath).ConfigureAwait(false);
        if (blob is null) return 0;

        var meta = blob.Metadata ?? new UpstreamMultipartMeta();
        databaseContext.MultipartFiles.Add(new DavMultipartFile
        {
            Id = davItemId,
            Metadata = new DavMultipartFile.Meta
            {
                AesParams = meta.AesParams is null ? null : new NzbWebDAV.Models.AesParams
                {
                    DecodedSize = meta.AesParams.DecodedSize,
                    Iv = meta.AesParams.Iv ?? [],
                    Key = meta.AesParams.Key ?? [],
                },
                FileParts = (meta.FileParts ?? []).Select(fp => new DavMultipartFile.FilePart
                {
                    SegmentIds = fp.SegmentIds ?? [],
                    SegmentIdByteRange = new NzbWebDAV.Models.LongRange(
                        fp.SegmentIdByteRange.StartInclusive,
                        fp.SegmentIdByteRange.EndExclusive),
                    FilePartByteRange = new NzbWebDAV.Models.LongRange(
                        fp.FilePartByteRange.StartInclusive,
                        fp.FilePartByteRange.EndExclusive),
                    // SegmentFallbacks is additive in this fork; not present in upstream contract.
                }).ToArray(),
            },
        });
        return 1;
    }

    private static async Task MarkOrphanAsync(
        DavDatabaseContext databaseContext, Guid davItemId, string reason)
    {
        // Use raw SQL so this works whether or not the DavItem entity is currently tracked.
        // Also revert Type back to 2 (upstream UsenetFile / "folder") if a previous buggy v2 build
        // had already force-promoted this item to Type=3/4/6 without populating metadata. Without
        // this revert the item would continue to be surfaced as a "file" in WebDAV listings and
        // throw FileNotFoundException on every stream attempt; reverting to Type=2 makes its
        // unrecoverable state visible to the user (it shows as a directory) and stops the spam.
        await databaseContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE DavItems
            SET IsCorrupted = 1,
                CorruptionReason = {0},
                Type = CASE WHEN Type IN (3, 4, 6) THEN 2 ELSE Type END
            WHERE Id = {1}
            """,
            reason, davItemId).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports DavItems that remain unrecoverable after the v1 blobstore migration.
    /// These are items whose blob file is missing from disk (no blob with a matching embedded
    /// DavItem.Id was found) — they cannot be streamed and must be re-imported via Sonarr/Radarr.
    ///
    /// Logged at startup so the user knows exactly how many items + a sample of paths are
    /// affected. Idempotent: read-only, runs every startup.
    /// </summary>
    private static async Task ReportOrphanedDavItemsAsync(DavDatabaseContext databaseContext)
    {
        if (!await TableExistsAsync(databaseContext, "DavItems").ConfigureAwait(false)) return;
        if (!await ColumnExistsAsync(databaseContext, "DavItems", "IsCorrupted").ConfigureAwait(false)) return;

        var connection = databaseContext.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        try
        {
            if (shouldClose) await connection.OpenAsync().ConfigureAwait(false);

            await using var countCmd = connection.CreateCommand();
            countCmd.CommandText =
                """
                SELECT COUNT(*) FROM DavItems
                WHERE IsCorrupted = 1
                  AND CorruptionReason LIKE 'Upstream blob file %'
                """;
            var totalObj = await countCmd.ExecuteScalarAsync().ConfigureAwait(false);
            var total = totalObj is null or DBNull ? 0L : Convert.ToInt64(totalObj);
            if (total == 0) return;

            var samples = new List<string>();
            await using var sampleCmd = connection.CreateCommand();
            sampleCmd.CommandText =
                """
                SELECT COALESCE(Path, Name) FROM DavItems
                WHERE IsCorrupted = 1
                  AND CorruptionReason LIKE 'Upstream blob file %'
                ORDER BY CreatedAt DESC
                LIMIT 10
                """;
            await using (var reader = await sampleCmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var path = reader.IsDBNull(0) ? "(unnamed)" : reader.GetString(0);
                    samples.Add(path);
                }
            }

            Log.Warning("═══════════════════════════════════════════════════════════════");
            Log.Warning(
                "[OrphanReport] {Total} DavItem(s) are unrecoverable: their upstream v1 blob file " +
                "is missing from /config/blobs and cannot be streamed.",
                total);
            Log.Warning(
                "[OrphanReport] Recovery: re-import these via Sonarr/Radarr to redownload from " +
                "Usenet. The orphan flag is harmless — items remain hidden until you choose to " +
                "delete the row.");
            Log.Warning("[OrphanReport] Sample of up to 10 most-recent affected paths:");
            foreach (var p in samples)
            {
                Log.Warning("[OrphanReport]   {Path}", p);
            }
            Log.Warning("═══════════════════════════════════════════════════════════════");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[OrphanReport] Failed to enumerate orphaned DavItems (non-fatal).");
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private static async Task NormalizeLegacyDavItemTypesAsync(DavDatabaseContext databaseContext)
    {
        if (!await TableExistsAsync(databaseContext, "DavItems").ConfigureAwait(false))
        {
            return;
        }

        var fixedSubtypeNzb = 0;
        var fixedSubtypeRar = 0;
        var fixedSubtypeMultipart = 0;
        var fixedSubtypeSymlinkRoot = 0;
        var fixedSubtypeIdsRoot = 0;

        if (await ColumnExistsAsync(databaseContext, "DavItems", "SubType").ConfigureAwait(false))
        {
            // v1/upstream-blobstore schema used Type=2 (UsenetFile) with SubType 201/202/203.
            // Only promote to this fork's enum values when the corresponding metadata row
            // already exists (DavNzbFiles/DavRarFiles/DavMultipartFiles). The blobstore
            // migration phase (MigrateDavItemsFromBlobstoreAsync) populates those rows from
            // the upstream blob files; items missing both a metadata row AND a recoverable
            // blob remain Type=2 (and are flagged IsCorrupted with a clear reason) so they
            // are not silently surfaced as unreadable files in WebDAV / Plex / rclone mounts.
            fixedSubtypeNzb = await databaseContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE DavItems SET Type = 3
                WHERE Type = 2 AND SubType = 201
                  AND EXISTS (SELECT 1 FROM DavNzbFiles n WHERE n.Id = DavItems.Id)
                """)
                .ConfigureAwait(false);
            fixedSubtypeRar = await databaseContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE DavItems SET Type = 4
                WHERE Type = 2 AND SubType = 202
                  AND EXISTS (SELECT 1 FROM DavRarFiles r WHERE r.Id = DavItems.Id)
                """)
                .ConfigureAwait(false);
            fixedSubtypeMultipart = await databaseContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE DavItems SET Type = 6
                WHERE Type = 2 AND SubType = 203
                  AND EXISTS (SELECT 1 FROM DavMultipartFiles m WHERE m.Id = DavItems.Id)
                """)
                .ConfigureAwait(false);

            // Keep special root folders aligned with this fork's enum values.
            fixedSubtypeSymlinkRoot = await databaseContext.Database.ExecuteSqlRawAsync(
                "UPDATE DavItems SET Type = 2 WHERE Type = 1 AND SubType = 105")
                .ConfigureAwait(false);
            fixedSubtypeIdsRoot = await databaseContext.Database.ExecuteSqlRawAsync(
                "UPDATE DavItems SET Type = 5 WHERE Type = 1 AND SubType = 106")
                .ConfigureAwait(false);
        }

        var fixedNzbTypes = await databaseContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE DavItems
            SET Type = 3
            WHERE Type IN (1, 2)
              AND EXISTS (SELECT 1 FROM DavNzbFiles n WHERE n.Id = DavItems.Id)
            """)
            .ConfigureAwait(false);

        var fixedRarTypes = await databaseContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE DavItems
            SET Type = 4
                        WHERE Type IN (1, 2)
              AND EXISTS (SELECT 1 FROM DavRarFiles r WHERE r.Id = DavItems.Id)
            """)
            .ConfigureAwait(false);

        var fixedMultipartTypes = await databaseContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE DavItems
            SET Type = 6
            WHERE Type IN (1, 2)
              AND EXISTS (SELECT 1 FROM DavMultipartFiles m WHERE m.Id = DavItems.Id)
            """)
            .ConfigureAwait(false);

        var totalFixed = fixedNzbTypes + fixedRarTypes + fixedMultipartTypes +
            fixedSubtypeNzb + fixedSubtypeRar + fixedSubtypeMultipart +
            fixedSubtypeSymlinkRoot + fixedSubtypeIdsRoot;
        if (totalFixed > 0)
        {
            Log.Warning(
                "[SchemaCompat] Normalized {Total} legacy DavItems types (SubtypeMap: Nzb={SubtypeNzb}, Rar={SubtypeRar}, Multipart={SubtypeMultipart}, SymlinkRoot={SubtypeSymlinkRoot}, IdsRoot={SubtypeIdsRoot}; TableMap: Nzb={Nzb}, Rar={Rar}, Multipart={Multipart})",
                totalFixed,
                fixedSubtypeNzb,
                fixedSubtypeRar,
                fixedSubtypeMultipart,
                fixedSubtypeSymlinkRoot,
                fixedSubtypeIdsRoot,
                fixedNzbTypes,
                fixedRarTypes,
                fixedMultipartTypes);
        }
    }

    private static async Task EnsureColumnExistsAsync(
        DavDatabaseContext databaseContext,
        string tableName,
        string columnName,
        string sqlType)
    {
        if (await ColumnExistsAsync(databaseContext, tableName, columnName).ConfigureAwait(false))
        {
            return;
        }

        Log.Warning("[SchemaCompat] Missing column detected: {Table}.{Column}. Adding it now.", tableName, columnName);
        await databaseContext.Database.ExecuteSqlRawAsync(
            $"ALTER TABLE {tableName} ADD COLUMN {columnName} {sqlType} NULL;")
            .ConfigureAwait(false);
    }

    private static async Task<bool> ColumnExistsAsync(DavDatabaseContext databaseContext, string tableName, string columnName)
    {
        var connection = databaseContext.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync().ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({tableName});";

            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (string.Equals(reader["name"]?.ToString(), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> ColumnsExistAsync(DavDatabaseContext databaseContext, string tableName, params string[] columnNames)
    {
        foreach (var column in columnNames)
        {
            if (!await ColumnExistsAsync(databaseContext, tableName, column).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> TableExistsAsync(DavDatabaseContext databaseContext, string tableName)
    {
        var connection = databaseContext.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync().ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @table LIMIT 1;";
            var param = command.CreateParameter();
            param.ParameterName = "@table";
            param.Value = tableName;
            command.Parameters.Add(param);

            var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
            return result != null;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }
}