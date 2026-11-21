using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gbrindes.Web.Controllers;

public sealed class UserUpdateRequest
{
    [JsonPropertyName("name")]
    public JsonElement Name { get; set; }

    [JsonPropertyName("email")]
    public JsonElement Email { get; set; }

    [JsonPropertyName("password")]
    public JsonElement Password { get; set; }


    [JsonPropertyName("role_id")]
    public JsonElement RoleId { get; set; }

    [JsonPropertyName("department_id")]
    public JsonElement DepartmentId { get; set; }

    [JsonPropertyName("industry_id")]
    public JsonElement IndustryId { get; set; }

    [JsonPropertyName("phone")]
    public JsonElement Phone { get; set; }

    [JsonPropertyName("must_change_password")]
    public JsonElement MustChangePassword { get; set; }
}
