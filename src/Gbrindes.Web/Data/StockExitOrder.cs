using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Gbrindes.Web.Data;

public sealed class StockExitOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public Guid ItemId { get; set; }
    public int Quantity { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string? Recipient { get; set; }
    public Guid? IndustryId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? RequesterId { get; set; }
    public string? PurchaseTicketNo { get; set; }
    public string? DocumentRef { get; set; }
    [JsonIgnore] public string? AttachmentPath { get; set; }
    [NotMapped, JsonPropertyName("attachment_url")]
    public string? AttachmentUrl => string.IsNullOrWhiteSpace(AttachmentPath) ? null : $"/api/v1/stock/exit-orders/{Id}/attachment";
    public string? Notes { get; set; }
    public string Status { get; set; } = "autorizada";
    public Guid AuthorizedBy { get; set; }
    public DateTimeOffset AuthorizedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ConfirmedBy { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public Guid? MovementId { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? IdempotencyRequestHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
