using System.Text.Json.Serialization;

namespace Gbrindes.Web.Data;

public sealed class Department
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    [JsonIgnore] public string NameKey { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public Guid? CreatedBy { get; set; }
    [JsonIgnore] public Guid? UpdatedBy { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
