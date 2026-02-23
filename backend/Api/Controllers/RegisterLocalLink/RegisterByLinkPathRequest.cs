using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.Controllers.RegisterLocalLink;

public class RegisterByLinkPathRequest
{
    [JsonPropertyName("linkPath")]
    public string LinkPath { get; set; } = string.Empty;
}
