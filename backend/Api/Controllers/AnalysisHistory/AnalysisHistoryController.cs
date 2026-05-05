using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.AnalysisHistory;

[ApiController]
[Route("api/analysis-history")]
public class AnalysisHistoryController(DavDatabaseContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AnalysisHistoryResponse>> GetHistory([FromQuery] int page = 0, [FromQuery] int pageSize = 100, [FromQuery] string? search = null, [FromQuery] bool showFailedOnly = false, [FromQuery] string? type = null, [FromQuery] bool showActionNeededOnly = false)
    {
        var apiKey = HttpContext.GetRequestApiKey();
        if (apiKey == null || apiKey != EnvironmentUtil.GetVariable("FRONTEND_BACKEND_API_KEY"))
        {
            return Unauthorized(new { error = "Unauthorized" });
        }

        var query = db.AnalysisHistoryItems.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(x => x.FileName.ToLower().Contains(searchLower) || (x.JobName != null && x.JobName.ToLower().Contains(searchLower)));
        }

        if (showFailedOnly)
        {
            query = query.Where(x => x.Result == "Failed");
        }

        if (showActionNeededOnly)
        {
            query = query.Where(x => db.HealthCheckResults
                .Where(r => r.DavItemId == x.DavItemId)
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => r.RepairStatus)
                .FirstOrDefault() == HealthCheckResult.RepairAction.ActionNeeded);
        }

        var normalizedType = NormalizeHistoryType(type);
        if (normalizedType != null)
        {
            query = ApplyTypeFilter(query, normalizedType);
        }

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip(page * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var itemIds = items.Select(x => x.DavItemId).Distinct().ToList();
        var itemPaths = await db.Items
            .AsNoTracking()
            .Where(x => itemIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Path })
            .ToDictionaryAsync(x => x.Id, x => x.Path);

        var healthRows = await db.HealthCheckResults
            .AsNoTracking()
            .Where(x => itemIds.Contains(x.DavItemId))
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.DavItemId, x.Path, x.RepairStatus })
            .ToListAsync();
        var latestHealthByItemId = healthRows
            .GroupBy(x => x.DavItemId)
            .ToDictionary(x => x.Key, x => x.First());

        var response = items.Select(item =>
        {
            itemPaths.TryGetValue(item.DavItemId, out var currentPath);
            latestHealthByItemId.TryGetValue(item.DavItemId, out var latestHealth);
            var jobName = JobNameUtil.PreferJobName(item.JobName, item.FileName, currentPath ?? latestHealth?.Path);

            return new AnalysisHistoryResponseItem
            {
                Id = item.Id,
                DavItemId = item.DavItemId,
                FileName = item.FileName,
                JobName = jobName,
                CreatedAt = item.CreatedAt,
                Result = item.Result,
                Details = item.Details,
                DurationMs = item.DurationMs,
                Type = GetHistoryType(item.Details),
                IsRemoved = !itemPaths.ContainsKey(item.DavItemId),
                IsActionNeeded = latestHealth?.RepairStatus == HealthCheckResult.RepairAction.ActionNeeded
            };
        }).ToList();

        return Ok(new AnalysisHistoryResponse
        {
            Items = response,
            TotalCount = totalCount
        });
    }

    private static IQueryable<AnalysisHistoryItem> ApplyTypeFilter(IQueryable<AnalysisHistoryItem> query, string type)
    {
        return type switch
        {
            "Health Check" => query.Where(x => x.Details != null && x.Details.ToLower().StartsWith("health check")),
            "Media Analysis" => query.Where(x => x.Details != null
                && !x.Details.ToLower().StartsWith("analysis")
                && (x.Details.ToLower().Contains("ffprobe")
                    || x.Details.ToLower().StartsWith("media analysis")
                    || x.Details.ToLower().StartsWith("media integrity"))),
            "NZB Analysis" => query.Where(x => x.Details != null
                && !x.Details.ToLower().StartsWith("health check")
                && !x.Details.ToLower().StartsWith("analysis")
                && !x.Details.ToLower().Contains("ffprobe")
                && !x.Details.ToLower().StartsWith("media analysis")
                && !x.Details.ToLower().StartsWith("media integrity")
                && x.Details.ToLower().Contains("segment")),
            "Analysis" => query.Where(x => x.Details == null
                || x.Details.ToLower().StartsWith("analysis")
                || (!x.Details.ToLower().StartsWith("health check")
                    && !x.Details.ToLower().Contains("ffprobe")
                    && !x.Details.ToLower().StartsWith("media analysis")
                    && !x.Details.ToLower().StartsWith("media integrity")
                    && !x.Details.ToLower().Contains("segment"))),
            _ => query
        };
    }

    private static string? NormalizeHistoryType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type) || type.Equals("all", StringComparison.OrdinalIgnoreCase)) return null;

        return type.Trim().ToLowerInvariant() switch
        {
            "health" or "health-check" or "health check" => "Health Check",
            "media" or "media-analysis" or "media analysis" => "Media Analysis",
            "nzb" or "nzb-analysis" or "nzb analysis" => "NZB Analysis",
            "analysis" => "Analysis",
            _ => null
        };
    }

    private static string GetHistoryType(string? details)
    {
        var lower = (details ?? "").Trim().ToLowerInvariant();
        if (lower.StartsWith("health check")) return "Health Check";
        if (lower.StartsWith("analysis")) return "Analysis";
        if (lower.Contains("ffprobe") || lower.StartsWith("media analysis") || lower.StartsWith("media integrity")) return "Media Analysis";
        if (lower.Contains("segment")) return "NZB Analysis";
        return "Analysis";
    }
}

public class AnalysisHistoryResponse
{
    public List<AnalysisHistoryResponseItem> Items { get; set; } = [];
    public int TotalCount { get; set; }
}

public class AnalysisHistoryResponseItem
{
    public Guid Id { get; set; }
    public Guid DavItemId { get; set; }
    public required string FileName { get; set; }
    public string? JobName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string Result { get; set; }
    public string? Details { get; set; }
    public long DurationMs { get; set; }
    public required string Type { get; set; }
    public bool IsRemoved { get; set; }
    public bool IsActionNeeded { get; set; }
}
