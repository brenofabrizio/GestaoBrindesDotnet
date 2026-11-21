using System.Text.Json.Serialization;

namespace Gbrindes.Web.Data;

public abstract class ContactLookup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string NameKey { get; set; } = "";
    public string? Cnpj { get; set; }
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? Notes { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public Guid? CreatedBy { get; set; }
    [JsonIgnore] public Guid? UpdatedBy { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
public sealed class Industry : ContactLookup;
public sealed class Supplier : ContactLookup;
