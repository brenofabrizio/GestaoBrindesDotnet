using Gbrindes.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gbrindes.Web.Inventory;

public static class StockPositionService
{
    public static async Task<Guid?> ResolveLocationIdAsync(ApplicationDbContext database, Guid? preferredLocationId, CancellationToken ct)
    {
        if (preferredLocationId is Guid preferred && await database.Locations.AnyAsync(location => location.Id == preferred && location.Active && location.DeletedAt == null, ct))
            return preferred;
        return await database.Locations.AsNoTracking().Where(location => location.Active && location.DeletedAt == null)
            .OrderBy(location => location.Kind == "cd" ? 0 : 1).ThenBy(location => location.Id)
            .Select(location => (Guid?)location.Id).FirstOrDefaultAsync(ct);
    }

    public static async Task ApplyDeltaAsync(ApplicationDbContext database, Guid itemId, Guid? locationId, int delta, CancellationToken ct)
    {
        if (delta == 0) return;
        var item = await database.CatalogItems.AsNoTracking().SingleOrDefaultAsync(value => value.Id == itemId, ct);
        if (item is null) throw new InventoryFailureException(404, "ITEM_NOT_FOUND", "Brinde não encontrado.");

        var targetLocationId = locationId ?? await ResolveLocationIdAsync(database, item.LocationId, ct);
        if (targetLocationId is null) return;

        var position = await database.StockPositions.SingleOrDefaultAsync(value => value.ItemId == itemId && value.LocationId == targetLocationId, ct);
        if (position is null)
        {
            if (delta < 0) throw new InventoryFailureException(422, "STOCK_INSUFFICIENT", "Não há saldo neste local para a saída.");
            position = new StockPosition { ItemId = itemId, LocationId = targetLocationId.Value };
            database.StockPositions.Add(position);
        }
        if (position.Quantity + delta < 0)
            throw new InventoryFailureException(422, "STOCK_INSUFFICIENT", $"Saldo insuficiente no local. Disponível: {position.Quantity}.");
        position.Quantity = checked(position.Quantity + delta);
        position.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
