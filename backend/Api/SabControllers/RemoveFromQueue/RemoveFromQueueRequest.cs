using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.SabControllers.RemoveFromQueue;

public class RemoveFromQueueRequest()
{
    public List<Guid> NzoIds { get; init; } = [];
    public CancellationToken CancellationToken { get; init; }

    public static async Task<RemoveFromQueueRequest> New(HttpContext httpContext)
    {
        var cancellationToken = SigtermUtil.GetCancellationToken();
        return new RemoveFromQueueRequest()
        {
            NzoIds = NzoIdsFromQueryParam(httpContext)
                .Concat(await NzoIdsFromRequestBody(httpContext, cancellationToken).ConfigureAwait(false))
                .ToList(),
            CancellationToken = cancellationToken
        };
    }

    private static IEnumerable<Guid> NzoIdsFromQueryParam(HttpContext httpContext)
    {
        return httpContext.GetQueryParamValues("value").Select(ParseNzoId);
    }

    private static Guid ParseNzoId(string value)
    {
        if (Guid.TryParse(value, out var id)) return id;
        throw new BadHttpRequestException($"Invalid nzo_id value: {value}");
    }

    private static async Task<List<Guid>> NzoIdsFromRequestBody(HttpContext httpContext, CancellationToken ct)
    {
        try
        {
            await using var stream = httpContext.Request.Body;
            var deserialized = await JsonSerializer.DeserializeAsync<RequestBody>(stream, cancellationToken: ct).ConfigureAwait(false);
            return deserialized?.NzoIds ?? [];
        }
        catch
        {
            return [];
        }
    }

    private class RequestBody
    {
        [JsonPropertyName("nzo_ids")]
        public List<Guid> NzoIds { get; set; } = [];
    }
}