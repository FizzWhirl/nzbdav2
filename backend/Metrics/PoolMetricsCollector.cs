using Microsoft.Extensions.Hosting;
using NzbWebDAV.Streams;
using Serilog;

namespace NzbWebDAV.Metrics;

/// <summary>
/// Periodically pulls live counter values from registered ConnectionPools
/// into Prometheus gauges. This is much simpler than wiring callbacks at
/// every increment site.
/// </summary>
public sealed class PoolMetricsCollector : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("[PoolMetricsCollector] Started, refreshing every {Seconds}s", Interval.TotalSeconds);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    AppMetrics.RefreshPoolGauges();
                    AppMetrics.SharedStreamActiveEntries.Set(SharedStreamManager.ActiveEntryCount);
                    AppMetrics.SharedStreamActiveReaders.Set(SharedStreamManager.ActiveReaderCount);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[PoolMetricsCollector] Error while refreshing pool gauges");
                }

                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        Log.Information("[PoolMetricsCollector] Stopped");
    }
}
