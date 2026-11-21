namespace Gbrindes.Web.Inventory;

public sealed record InventoryItem(Guid Id, Guid IndustryId, string Sku, string Name, string Unit);
