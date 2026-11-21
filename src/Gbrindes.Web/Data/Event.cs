using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Gbrindes.Web.Data;

public sealed class Event
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [MaxLength(190)]
    public string? Venue { get; set; }

    public Guid? LocationId { get; set; }

    public DateOnly? StartsOn { get; set; }

    public DateOnly? EndsOn { get; set; }

    [Required, MaxLength(20)]
    public string Status { get; set; } = "planejado";

    public string? Notes { get; set; }

    public DateTimeOffset? OpenedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public Guid? CreatedBy { get; set; }

    [JsonIgnore]
    public Guid? UpdatedBy { get; set; }

    [JsonIgnore]
    public Guid? OpenedBy { get; set; }

    [JsonIgnore]
    public Guid? ClosedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}
