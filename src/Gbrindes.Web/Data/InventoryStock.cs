namespace Gbrindes.Web.Data;

public sealed class InventoryStock
{
    public Guid ItemId { get; set; }
    public int OnHand { get; set; }
    public int Reserved { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public CatalogItem Item { get; set; } = null!;
}

public sealed class StockMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ItemId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? IndustryId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("department_id")] public Guid? DepartmentId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("supplier_id")] public Guid? SupplierId { get; set; }
    public Guid? DeliveryId { get; set; }
    public string Type { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public int BalanceAfter { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("unit_value")] public decimal? UnitValue { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped, System.Text.Json.Serialization.JsonPropertyName("attachment_url")]
    public string? AttachmentUrl => string.IsNullOrWhiteSpace(AttachmentPath) ? null : $"/api/v1/stock/movements/{Id}/attachment";
    [System.Text.Json.Serialization.JsonIgnore] public string? AttachmentPath { get; set; }
    public string? Reason { get; set; }
    public string? Notes { get; set; }
    public string? DocumentRef { get; set; }
    public string? Purpose { get; set; }
    public string? Recipient { get; set; }
    public string? PurchaseTicketNo { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? IdempotencyRequestHash { get; set; }
    public Guid? LocationId { get; set; }
    public Guid? FromLocationId { get; set; }
    public Guid? ToLocationId { get; set; }
    public Guid? TransferId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public CatalogItem Item { get; set; } = null!;
}

public sealed class StockPosition
{
    public Guid ItemId { get; set; }
    public Guid LocationId { get; set; }
    public int Quantity { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
