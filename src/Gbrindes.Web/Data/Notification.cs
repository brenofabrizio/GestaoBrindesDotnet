using Gbrindes.Web.Identity;

namespace Gbrindes.Web.Data;

public sealed class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public string Channel { get; set; } = "in_app";
    public string? ToEmail { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string? Body { get; set; }
    public string? LinkUrl { get; set; }
    public string? RelatedType { get; set; }
    public Guid? RelatedId { get; set; }
    public string Status { get; set; } = "fila";
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public string? DedupeKey { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ApplicationUser? User { get; set; }
}
