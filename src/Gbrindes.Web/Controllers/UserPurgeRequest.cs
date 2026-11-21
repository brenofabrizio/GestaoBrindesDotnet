using System.Text.Json.Serialization;

namespace Gbrindes.Web.Controllers;

public sealed class UserPurgeRequest
{
    [JsonPropertyName("confirm")]
    public string? Confirm { get; set; }
}
