using Gbrindes.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gbrindes.Web.Inventory;

public static class StockIndustryAttribution
{
    public static async Task<Dictionary<(Guid IndustryId, Guid ItemId), int>> GetUnassignedReceiptAdditionsAsync(
        ApplicationDbContext database,
        CancellationToken cancellationToken)
    {
        var unassignedReceiptsQuery =
            from movement in database.StockMovements.AsNoTracking()
            join item in database.CatalogItems.AsNoTracking() on movement.ItemId equals item.Id
            where movement.IndustryId == null
                && movement.Type == "entrada"
                && item.DeletedAt == null
                && item.IndustryId == null
            group movement by movement.ItemId
            into grouped
            select new { ItemId = grouped.Key, Quantity = grouped.Sum(movement => movement.Quantity) };

        var receipts = await unassignedReceiptsQuery.ToListAsync(cancellationToken);
        if (receipts.Count == 0)
            return [];

        var industryPairs = await (
            from movement in database.StockMovements.AsNoTracking()
            join receipt in unassignedReceiptsQuery on movement.ItemId equals receipt.ItemId
            where movement.IndustryId != null
            select new { movement.ItemId, IndustryId = movement.IndustryId!.Value })
            .Distinct()
            .ToListAsync(cancellationToken);

        var uniqueIndustries = industryPairs
            .GroupBy(pair => pair.ItemId)
            .Where(group => group.Select(pair => pair.IndustryId).Distinct().Take(2).Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().IndustryId);

        return receipts
            .Where(receipt => uniqueIndustries.ContainsKey(receipt.ItemId))
            .ToDictionary(
                receipt => (uniqueIndustries[receipt.ItemId], receipt.ItemId),
                receipt => receipt.Quantity);
    }
}
