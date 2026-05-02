using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.GetProviderStats;

[ApiController]
[Route("api/provider-stats")]
public class GetProviderStatsController(
    DavDatabaseContext dbContext,
    ConfigManager configManager) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string range = "all")
    {
        var providerConfig = configManager.GetUsenetProviderConfig();
        var providers = providerConfig.Providers;

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? cutoff = range switch
        {
            "1h" => now.AddHours(-1),
            "24h" => now.AddHours(-24),
            "30d" => now.AddDays(-30),
            "all" => null,
            _ => null
        };

        var query = dbContext.NzbProviderStats.AsNoTracking();
        if (cutoff.HasValue)
        {
            query = query.Where(s => s.LastUsed >= cutoff.Value);
        }

        var statsByProvider = await query
            .GroupBy(s => s.ProviderIndex)
            .Select(g => new
            {
                ProviderIndex = g.Key,
                SuccessfulSegments = g.Sum(s => s.SuccessfulSegments),
                FailedSegments = g.Sum(s => s.FailedSegments),
                TotalBytes = g.Sum(s => s.TotalBytes),
                TotalTimeMs = g.Sum(s => s.TotalTimeMs)
            })
            .ToListAsync();

        var totalOperations = statsByProvider.Sum(s => s.SuccessfulSegments + s.FailedSegments);

        var providerStatsList = statsByProvider
            .Where(s => s.ProviderIndex < providers.Count)
            .OrderByDescending(s => s.SuccessfulSegments + s.FailedSegments)
            .Select(s =>
            {
                var total = s.SuccessfulSegments + s.FailedSegments;
                return new
                {
                    ProviderIndex = s.ProviderIndex,
                    ProviderHost = providers[s.ProviderIndex].Host,
                    ProviderType = providers[s.ProviderIndex].Type.ToString(),
                    TotalOperations = total,
                    OperationCounts = new Dictionary<string, int>
                    {
                        ["BODY"] = s.SuccessfulSegments,
                        ["BODY_FAIL"] = s.FailedSegments
                    },
                    PercentageOfTotal = totalOperations > 0
                        ? Math.Round((double)total / totalOperations * 100, 1)
                        : 0,
                    TotalBytes = s.TotalBytes,
                    AverageSpeedMbps = s.TotalTimeMs > 0
                        ? Math.Round((double)s.TotalBytes / s.TotalTimeMs * 1000 / 1024 / 1024, 1)
                        : 0
                };
            })
            .ToList();

        return Ok(new
        {
            Providers = providerStatsList,
            TotalOperations = totalOperations,
            CalculatedAt = DateTimeOffset.UtcNow.ToString("o"),
            TimeWindow = range,
            TimeWindowHours = range switch
            {
                "1h" => 1,
                "24h" => 24,
                "30d" => 720,
                _ => 0
            }
        });
    }
}
