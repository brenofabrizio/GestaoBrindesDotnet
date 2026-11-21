using System.ComponentModel.DataAnnotations.Schema;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Gbrindes.Web.Data;

public sealed class CatalogItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid CategoryId { get; set; }
    public Guid? IndustryId { get; set; }
    public Guid? LocationId { get; set; }
    public Guid? SupplierId { get; set; }
    public string Kind { get; set; } = "fisico";
    public decimal? UnitValue { get; set; }
    public int MinStock { get; set; }
    public string Status { get; set; } = "ativo";
    public DateOnly? EntryDate { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonIgnore]
    public Guid? CreatedBy { get; set; }
    [JsonIgnore]
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    [JsonIgnore]
    public Guid? DeletedBy { get; set; }
    [JsonIgnore]
    public string? PhotoPath { get; set; }
    [JsonIgnore]
    public string? ThumbPath { get; set; }
    [NotMapped, JsonPropertyName("photo_url")]
    public string? PhotoUrl => PhotoPath is null ? null : $"/api/v1/items/{Id}/photo?v={PhotoVersion()}";
    [NotMapped, JsonPropertyName("thumb_url")]
    public string? ThumbUrl => ThumbPath is null ? null : $"/api/v1/items/{Id}/photo?size=thumb&v={PhotoVersion()}";
    public Category Category { get; set; } = null!;
    public Industry? Industry { get; set; }
    public Location? Location { get; set; }
    public Supplier? Supplier { get; set; }

    private string PhotoVersion() => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(PhotoPath ?? ThumbPath ?? string.Empty)))[..8].ToLowerInvariant();
}
