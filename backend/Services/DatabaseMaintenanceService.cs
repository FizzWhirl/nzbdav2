using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public class DatabaseMaintenanceService(IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("[DatabaseMaintenance] Service started. Scheduled to run every 24 hours.");

        // Wait a bit on startup to let other heavy tasks finish
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PerformMaintenanceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DatabaseMaintenance] Error occurred during scheduled maintenance.");
            }

            // Run daily
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    public async Task PerformMaintenanceAsync(CancellationToken stoppingToken)
    {
        Log.Information("[DatabaseMaintenance] Starting daily database maintenance...");

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DavDatabaseContext>();

        // 1. Prune BandwidthSamples (> 30 days) — accumulate bytes before deleting
        var bandwidthCutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(stoppingToken);

        // Sum bytes about to be pruned (raw SQL to match DELETE pattern)
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(stoppingToken).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        var cutoffParam = cmd.CreateParameter();
        cutoffParam.ParameterName = "@cutoff";
        cutoffParam.Value = bandwidthCutoff;
        cmd.Parameters.Add(cutoffParam);
        cmd.CommandText = "SELECT COALESCE(SUM(\"Bytes\"), 0) FROM \"BandwidthSamples\" WHERE \"Timestamp\" < @cutoff";
        var prunedBytes = Convert.ToInt64(await cmd.ExecuteScalarAsync(stoppingToken).ConfigureAwait(false));

        if (prunedBytes > 0)
        {
            // Read current counter, upsert with accumulated bytes
            var existing = await dbContext.ConfigItems
                .FirstOrDefaultAsync(c => c.ConfigName == "stats.alltime-bandwidth-bytes", stoppingToken)
                .ConfigureAwait(false);
            var currentTotal = long.TryParse(existing?.ConfigValue, out var parsed) ? parsed : 0L;
            var newTotal = currentTotal + prunedBytes;

            if (existing != null)
                existing.ConfigValue = newTotal.ToString();
            else
                dbContext.ConfigItems.Add(new ConfigItem
                    { ConfigName = "stats.alltime-bandwidth-bytes", ConfigValue = newTotal.ToString() });

            await dbContext.SaveChangesAsync(stoppingToken).ConfigureAwait(false);
            Log.Information("[DatabaseMaintenance] Archived {Bytes} bandwidth bytes to all-time counter (total: {Total}).",
                prunedBytes, newTotal);
        }

        // Delete old samples
        var bandwidthDeleted = await dbContext.Database.ExecuteSqlRawAsync(
            $"DELETE FROM \"BandwidthSamples\" WHERE \"Timestamp\" < {bandwidthCutoff}",
            stoppingToken);
        if (bandwidthDeleted > 0)
            Log.Information("[DatabaseMaintenance] Pruned {Count} old records from BandwidthSamples.", bandwidthDeleted);

        await transaction.CommitAsync(stoppingToken).ConfigureAwait(false);

        // 2. Prune HealthCheckResults (> 30 days)
        // Keep Deleted items longer? For now, treat all same as 30 days history.
        var healthCutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
        var healthDeleted = await dbContext.Database.ExecuteSqlRawAsync(
            $"DELETE FROM \"HealthCheckResults\" WHERE \"CreatedAt\" < {healthCutoff}", 
            stoppingToken);
        if (healthDeleted > 0)
            Log.Information("[DatabaseMaintenance] Pruned {Count} old records from HealthCheckResults.", healthDeleted);

        // 3. Prune MissingArticleEvents (> 14 days)
        // These can grow huge, so aggressive pruning is good.
        var eventsCutoff = DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeSeconds();
        var eventsDeleted = await dbContext.Database.ExecuteSqlRawAsync(
            $"DELETE FROM \"MissingArticleEvents\" WHERE \"Timestamp\" < {eventsCutoff}", 
            stoppingToken);
        if (eventsDeleted > 0)
            Log.Information("[DatabaseMaintenance] Pruned {Count} old records from MissingArticleEvents.", eventsDeleted);

        // 4. Prune MissingArticleSummaries (> 14 days last seen)
        var summaryCutoff = DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeSeconds();
        var summariesDeleted = await dbContext.Database.ExecuteSqlRawAsync(
            $"DELETE FROM \"MissingArticleSummaries\" WHERE \"LastSeen\" < {summaryCutoff}",
            stoppingToken);
        if (summariesDeleted > 0)
            Log.Information("[DatabaseMaintenance] Pruned {Count} old records from MissingArticleSummaries.", summariesDeleted);

        // 5. Cleanup old hidden history items (> 30 days)
        var dbClient = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
        try
        {
            await dbClient.CleanupOldHiddenHistoryItemsAsync(30, stoppingToken);
            Log.Information("[DatabaseMaintenance] Cleaned up old hidden history items.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[DatabaseMaintenance] Error cleaning up old hidden history items.");
        }

        // 6. Compress uncompressed HistoryItem NzbContents
        await CompressHistoryNzbContentsAsync(dbContext, stoppingToken);

        // 7. Optimize WAL (Checkpoint)
        // This merges the WAL file into the main DB and truncates it, keeping disk usage low.
        Log.Information("[DatabaseMaintenance] Checkpointing WAL file...");
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", stoppingToken);

        Log.Information("[DatabaseMaintenance] Maintenance completed successfully.");
    }

    private static async Task CompressHistoryNzbContentsAsync(DavDatabaseContext dbContext, CancellationToken ct)
    {
        var conn = dbContext.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct).ConfigureAwait(false);

        // Find uncompressed rows (NzbContents is not null and doesn't start with ZSTD: prefix)
        var uncompressedIds = new List<Guid>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id FROM HistoryItems WHERE NzbContents IS NOT NULL AND NzbContents NOT LIKE 'ZSTD:%' LIMIT 1000";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                uncompressedIds.Add(Guid.Parse(reader.GetString(0)));
        }

        if (uncompressedIds.Count == 0) return;

        Log.Warning("[DatabaseMaintenance] Compressing {Count} uncompressed HistoryItem NzbContents...", uncompressedIds.Count);

        var compressed = 0;
        foreach (var id in uncompressedIds)
        {
            if (ct.IsCancellationRequested) break;

            // Read raw content
            string? rawContent = null;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT NzbContents FROM HistoryItems WHERE Id = @id";
                var p = cmd.CreateParameter();
                p.ParameterName = "@id";
                p.Value = id.ToString().ToUpperInvariant();
                cmd.Parameters.Add(p);
                rawContent = (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }

            if (rawContent == null || CompressionUtil.IsCompressed(rawContent)) continue;

            // Compress and update
            var compressedContent = CompressionUtil.Compress(rawContent);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE HistoryItems SET NzbContents = @content WHERE Id = @id";
                var pContent = cmd.CreateParameter();
                pContent.ParameterName = "@content";
                pContent.Value = compressedContent;
                cmd.Parameters.Add(pContent);
                var pId = cmd.CreateParameter();
                pId.ParameterName = "@id";
                pId.Value = id.ToString().ToUpperInvariant();
                cmd.Parameters.Add(pId);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            compressed++;
        }

        Log.Warning("[DatabaseMaintenance] Compressed {Count} HistoryItem NzbContents rows.", compressed);
    }
}
