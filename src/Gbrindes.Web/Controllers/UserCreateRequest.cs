using System.Text.Json.Serialization;

namespace Gbrindes.Web.Controllers;

public sealed class UserCreateRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("role_id")]
    public Guid? RoleId { get; set; }

    [JsonPropertyName("department_id")]
    public Guid? DepartmentId { get; set; }

    [JsonPropertyName("industry_id")]
    public Guid? IndustryId { get; set; }

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("active")]
    public bool? Active { get; set; }

    [JsonPropertyName("must_change_password")]
    public bool? MustChangePassword { get; set; }
}
