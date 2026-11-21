using Gbrindes.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using StockMovementEntity = Gbrindes.Web.Data.StockMovement;

namespace Gbrindes.Web.Inventory;

public sealed class StockReconciliationService(ApplicationDbContext database)
{
    private const string PostgreSqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public async Task<StockReconciliationResult> ReconcileAllAsync(Guid userId, CancellationToken ct)
    {
        IDbContextTransaction? transaction = null;
        var ownsTransaction = database.Database.IsRelational();
        if (ownsTransaction)
            transaction = await database.Database.BeginTransactionAsync(ct);

        try
        {
            var itemsQuery = database.Database.ProviderName == PostgreSqlProvider
                ? database.CatalogItems.FromSqlRaw("SELECT * FROM \"CatalogItems\" WHERE \"DeletedAt\" IS NULL ORDER BY \"Id\" FOR UPDATE")
                : database.CatalogItems.Where(item => item.DeletedAt == null).OrderBy(item => item.Id);
            var items = await itemsQuery.ToListAsync(ct);
            var totalItems = 0;
            var fixedPositions = 0;
            var fixedMovements = 0;
            var now = DateTimeOffset.UtcNow;

            foreach (var item in items)
            {
                var stock = await database.Stocks.SingleOrDefaultAsync(value => value.ItemId == item.Id, ct);
                if (stock is null)
                    continue;

                totalItems++;
                if (stock.OnHand <= 0)
                    continue;

                var needsPosition = !await database.StockPositions.AnyAsync(position => position.ItemId == item.Id, ct);
                var needsMovement = !await database.StockMovements.AnyAsync(movement => movement.ItemId == item.Id, ct);
                if (!needsPosition && !needsMovement)
                    continue;

                var locationId = await StockPositionService.ResolveLocationIdAsync(database, item.LocationId, ct);
                if (locationId is null)
                    throw new InventoryFailureException(409, "STOCK_LOCATION_NOT_FOUND",
                        $"Não foi possível reconciliar o brinde {item.Code}: não há local ativo para registrar o saldo.");

                if (needsPosition)
                {
                    database.StockPositions.Add(new StockPosition
                    {
                        ItemId = item.Id,
                        LocationId = locationId.Value,
                        Quantity = stock.OnHand,
                        UpdatedAt = now
                    });
                    fixedPositions++;
                }

                if (needsMovement)
                {
                    database.StockMovements.Add(new StockMovementEntity
                    {
                        ItemId = item.Id,
                        UserId = userId,
                        Type = "entrada",
                        Quantity = stock.OnHand,
                        BalanceAfter = stock.OnHand,
                        UnitValue = item.UnitValue,
                        LocationId = locationId,
                        Reason = "Saldo inicial",
                        Notes = "Saldo inicial (conciliação automática de estoque)",
                        CreatedAt = now
                    });
                    fixedMovements++;
                }
            }

            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);

            return new StockReconciliationResult(totalItems, fixedPositions, fixedMovements);
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(ct);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }
}

public sealed record StockReconciliationResult(int TotalItems, int FixedPositions, int FixedMovements);
