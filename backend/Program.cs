using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using NWebDav.Server;
using NWebDav.Server.Stores;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Auth;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
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
            Log.Warning("  NzbDav Backend Starting - BUILD v2026-04-23-V1-MIGRATION-FILETYPE-QUEUE-RECOVERY");
            Log.Warning("  FEATURE: v1 Type/SubType normalization plus queue blob recovery from /config/blobs");
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

        await EnsureQueueNzbContentsConsistencyAsync(databaseContext).ConfigureAwait(false);
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
            // v1/newer schema used Type=2 (UsenetFile) with SubType 201/202/203.
            fixedSubtypeNzb = await databaseContext.Database.ExecuteSqlRawAsync(
                "UPDATE DavItems SET Type = 3 WHERE Type = 2 AND SubType = 201")
                .ConfigureAwait(false);
            fixedSubtypeRar = await databaseContext.Database.ExecuteSqlRawAsync(
                "UPDATE DavItems SET Type = 4 WHERE Type = 2 AND SubType = 202")
                .ConfigureAwait(false);
            fixedSubtypeMultipart = await databaseContext.Database.ExecuteSqlRawAsync(
                "UPDATE DavItems SET Type = 6 WHERE Type = 2 AND SubType = 203")
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