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
            Log.Warning("  NzbDav Backend Starting - BUILD v2026-04-24-V1-BLOBSTORE-MIGRATION");
            Log.Warning("  FEATURE: Auto-migrate v1 (upstream blobstore-era) DavItems into v2 metadata tables on startup");
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
    /// Trigger conditions (idempotent — safe to run on every startup):
    ///   1. <c>DavItems</c> has the upstream <c>FileBlobId</c> column.
    ///   2. The blobs directory exists at <c>{ConfigPath}/blobs</c>.
    ///   3. There is at least one <c>Type=2</c> DavItem still missing its metadata row.
    ///
    /// For each candidate item:
    ///   - Reads + decompresses + deserializes the upstream blob.
    ///   - Inserts the corresponding metadata row using EF (so JSON+Zstd value-converters apply).
    ///   - Items whose blob is missing or unreadable are flagged
    ///     <c>IsCorrupted = true</c> with a clear <c>CorruptionReason</c> and left as
    ///     <c>Type=2</c>; <see cref="NormalizeLegacyDavItemTypesAsync"/> only promotes items
    ///     that successfully landed in the metadata table.
    /// </summary>
    private static async Task MigrateDavItemsFromBlobstoreAsync(DavDatabaseContext databaseContext)
    {
        if (!await TableExistsAsync(databaseContext, "DavItems").ConfigureAwait(false)) return;
        if (!await ColumnExistsAsync(databaseContext, "DavItems", "FileBlobId").ConfigureAwait(false)) return;
        if (!await ColumnExistsAsync(databaseContext, "DavItems", "SubType").ConfigureAwait(false)) return;

        var blobsRoot = Path.Combine(DavDatabaseContext.ConfigPath, "blobs");
        if (!Directory.Exists(blobsRoot))
        {
            Log.Warning(
                "[BlobstoreMigration] DavItems has FileBlobId column (upstream blobstore-era schema) " +
                "but no blobs directory found at {BlobsRoot}. Items missing metadata rows will remain " +
                "as Type=2 and be flagged corrupted. Mount the v1 blobs at this path to enable recovery.",
                blobsRoot);
            return;
        }

        // Collect candidate work items (Id, FileBlobId, SubType) for any Type=2 DavItem that does
        // NOT yet have a metadata row in this fork's tables. Process in chunks to keep memory low.
        var candidates = await LoadBlobstoreMigrationCandidatesAsync(databaseContext).ConfigureAwait(false);
        if (candidates.Count == 0) return;

        Log.Warning(
            "[BlobstoreMigration] Found {Count} DavItems needing recovery from upstream blobstore " +
            "(blobs root: {BlobsRoot}). Migrating now…",
            candidates.Count, blobsRoot);

        var migratedNzb = 0;
        var migratedRar = 0;
        var migratedMultipart = 0;
        var orphaned = 0;
        var failed = 0;
        var unknownSubtype = 0;

        const int BatchSize = 500;
        var processed = 0;
        var startedAt = DateTimeOffset.UtcNow;

        foreach (var batch in candidates.Chunk(BatchSize))
        {
            foreach (var (davItemId, fileBlobId, subType) in batch)
            {
                if (fileBlobId is null)
                {
                    await MarkOrphanAsync(databaseContext, davItemId,
                        "Upstream blobstore item with no FileBlobId — cannot recover metadata.")
                        .ConfigureAwait(false);
                    orphaned++;
                    continue;
                }

                var blobPath = BlobStoreReader.GetBlobPath(blobsRoot, fileBlobId.Value);

                try
                {
                    var inserted = subType switch
                    {
                        201 => await TryMigrateNzbAsync(databaseContext, davItemId, blobPath).ConfigureAwait(false),
                        202 => await TryMigrateRarAsync(databaseContext, davItemId, blobPath).ConfigureAwait(false),
                        203 => await TryMigrateMultipartAsync(databaseContext, davItemId, blobPath).ConfigureAwait(false),
                        _ => -1,
                    };

                    switch (subType)
                    {
                        case 201 when inserted == 1: migratedNzb++; break;
                        case 202 when inserted == 1: migratedRar++; break;
                        case 203 when inserted == 1: migratedMultipart++; break;
                        case 201 or 202 or 203 when inserted == 0:
                            await MarkOrphanAsync(databaseContext, davItemId,
                                $"Upstream blob file missing or unreadable at {blobPath}. " +
                                "Re-download via Sonarr/Radarr to recover.")
                                .ConfigureAwait(false);
                            orphaned++;
                            break;
                        default:
                            // Unknown SubType — leave Type=2 and flag with a clear reason.
                            await MarkOrphanAsync(databaseContext, davItemId,
                                $"Unknown upstream SubType={subType}; this fork does not know how " +
                                "to migrate this item.")
                                .ConfigureAwait(false);
                            unknownSubtype++;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[BlobstoreMigration] Unhandled error migrating item {DavItemId} " +
                        "(blob={BlobPath}, subType={SubType})", davItemId, blobPath, subType);
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
            "UnknownSubType={UnknownSubtype}, Failed={Failed}.",
            elapsed, migratedNzb, migratedRar, migratedMultipart, orphaned, unknownSubtype, failed);
    }

    private record struct BlobstoreCandidate(Guid DavItemId, Guid? FileBlobId, int? SubType);

    private static async Task<List<BlobstoreCandidate>> LoadBlobstoreMigrationCandidatesAsync(
        DavDatabaseContext databaseContext)
    {
        var connection = databaseContext.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync().ConfigureAwait(false);

        try
        {
            await using var cmd = connection.CreateCommand();
            // Type=2 = upstream UsenetFile. Restrict to items still missing metadata in this
            // fork's tables (so re-runs are idempotent and cheap).
            cmd.CommandText =
                """
                SELECT d.Id, d.FileBlobId, d.SubType
                FROM DavItems d
                WHERE d.Type = 2
                  AND (d.IsCorrupted = 0 OR d.IsCorrupted IS NULL)
                  AND NOT EXISTS (SELECT 1 FROM DavNzbFiles n WHERE n.Id = d.Id)
                  AND NOT EXISTS (SELECT 1 FROM DavRarFiles r WHERE r.Id = d.Id)
                  AND NOT EXISTS (SELECT 1 FROM DavMultipartFiles m WHERE m.Id = d.Id)
                """;

            var results = new List<BlobstoreCandidate>();
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (!Guid.TryParse(reader[0]?.ToString(), out var davItemId)) continue;

                Guid? fileBlobId = null;
                if (!reader.IsDBNull(1) && Guid.TryParse(reader[1]?.ToString(), out var parsedBlobId))
                {
                    fileBlobId = parsedBlobId;
                }

                int? subType = null;
                if (!reader.IsDBNull(2) && int.TryParse(reader[2]?.ToString(), out var parsedSubType))
                {
                    subType = parsedSubType;
                }

                results.Add(new BlobstoreCandidate(davItemId, fileBlobId, subType));
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
        await databaseContext.Database.ExecuteSqlRawAsync(
            "UPDATE DavItems SET IsCorrupted = 1, CorruptionReason = {0} WHERE Id = {1}",
            reason, davItemId).ConfigureAwait(false);
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