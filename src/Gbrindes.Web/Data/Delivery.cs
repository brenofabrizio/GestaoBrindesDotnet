using System.Text.Json.Serialization;
using Gbrindes.Web.Identity;

namespace Gbrindes.Web.Data;

public sealed class Delivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string Type { get; set; } = "dia_a_dia";
    public Guid? RequestId { get; set; }
    public Guid? EventId { get; set; }
    public Guid? IndustryId { get; set; }
    public Guid? DeliveredById { get; set; }
    public string ReceivedByName { get; set; } = string.Empty;
    public string? ReceivedByEmail { get; set; }
    public string? ReceivedByDocument { get; set; }
    public string? ReceivedByPhone { get; set; }
    [JsonIgnore] public string SignaturePath { get; set; } = string.Empty;
    public string? Notes { get; set; }
    [JsonIgnore] public string IdempotencyKeyHash { get; set; } = string.Empty;
    [JsonIgnore] public string RequestHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public InternalRequest? Request { get; set; }
    public Event? Event { get; set; }
    public ICollection<DeliveryItem> Items { get; set; } = new List<DeliveryItem>();
}

public sealed class DeliveryItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeliveryId { get; set; }
    public Guid ItemId { get; set; }
    public int Quantity { get; set; }
    public int BalanceAfter { get; set; }
    public Delivery Delivery { get; set; } = null!;
    public CatalogItem Item { get; set; } = null!;
}
