using System.Text.Json;
using Gbrindes.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Gbrindes.Web.Inventory;

public sealed class EventWorkflowService(ApplicationDbContext database)
{
    private const string PostgresProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public Task<Event> OpenAsync(Guid eventId, Guid userId, string? userName, CancellationToken ct) =>
        TransitionAsync(eventId, userId, userName, open: true, ct);

    public Task<Event> CloseAsync(Guid eventId, Guid userId, string? userName, CancellationToken ct) =>
        TransitionAsync(eventId, userId, userName, open: false, ct);

    private async Task<Event> TransitionAsync(Guid eventId, Guid userId, string? userName, bool open, CancellationToken ct)
    {
        await using IDbContextTransaction? transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            var eventRecord = await LockEventAsync(eventId, ct);
            var expectedStatus = open ? "planejado" : "aberto";
            if (eventRecord.Status != expectedStatus)
                throw new InventoryFailureException(422, open ? "EVENT_NOT_PLANNED" : "EVENT_NOT_OPEN",
                    open ? "Somente eventos planejados podem ser abertos." : "Somente eventos abertos podem ser encerrados.");

            var quantities = await database.EventAllocations.AsNoTracking()
                .Where(allocation => allocation.EventId == eventId)
                .GroupBy(allocation => allocation.ItemId)
                .Select(group => new
                {
                    ItemId = group.Key,
                    Quantity = group.Sum(allocation => open
                    ? allocation.QuantityAllocated
                    : allocation.QuantityAllocated - allocation.QuantityWithdrawn)
                })
                .ToListAsync(ct);
            if (open && quantities.Count == 0)
                throw new InventoryFailureException(422, "NO_ALLOCATIONS", "Defina as cotas por indústria antes de abrir o evento.");

            foreach (var quantity in quantities)
            {
                if (quantity.Quantity <= 0)
                    continue;
                var item = await LockItemAsync(quantity.ItemId, ct);
                if (item is null)
                    throw new InventoryFailureException(404, "ITEM_NOT_FOUND", "Brinde não encontrado.");
                var stock = await LockStockAsync(quantity.ItemId, ct);
                var available = stock is null ? 0 : stock.OnHand - stock.Reserved;
                if (open)
                {
                    if (quantity.Quantity > available)
                        throw new InventoryFailureException(422, "STOCK_INSUFFICIENT",
                            $"Estoque insuficiente para {item.Name}. Disponível: {available}.");
                    stock!.Reserved = checked(stock.Reserved + quantity.Quantity);
                    stock.UpdatedAt = DateTimeOffset.UtcNow;
                }
                else
                {
                    if (stock is null || stock.Reserved < quantity.Quantity)
                        throw new InventoryFailureException(422, "RESERVATION_INVALID", "Quantidade a liberar maior que a reservada.");
                    stock.Reserved -= quantity.Quantity;
                    stock.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }

            var now = DateTimeOffset.UtcNow;
            var previousStatus = eventRecord.Status;
            eventRecord.Status = open ? "aberto" : "encerrado";
            eventRecord.UpdatedAt = now;
            eventRecord.UpdatedBy = userId;
            if (open)
            {
                eventRecord.OpenedAt = now;
                eventRecord.OpenedBy = userId;
            }
            else
            {
                eventRecord.ClosedAt = now;
                eventRecord.ClosedBy = userId;
            }
            database.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                UserName = userName,
                Action = open ? "open" : "close",
                EntityType = "event",
                EntityId = eventId,
                EntityLabel = eventRecord.Name,
                BeforeJson = JsonSerializer.Serialize(new { status = previousStatus }),
                AfterJson = JsonSerializer.Serialize(new { status = eventRecord.Status }),
                CreatedAt = now
            });
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return eventRecord;
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<Event> LockEventAsync(Guid eventId, CancellationToken ct)
    {
        Event? eventRecord;
        if (database.Database.ProviderName == PostgresProvider)
        {
            eventRecord = await database.Events
                .FromSqlInterpolated($"SELECT * FROM \"Events\" WHERE \"Id\" = {eventId} AND \"DeletedAt\" IS NULL FOR UPDATE")
                .SingleOrDefaultAsync(ct);
        }
        else
        {
            eventRecord = await database.Events.SingleOrDefaultAsync(
                value => value.Id == eventId && value.DeletedAt == null, ct);
        }
        return eventRecord ?? throw new InventoryFailureException(404, "NOT_FOUND", "Evento não encontrado.");
    }

    private async Task<CatalogItem?> LockItemAsync(Guid itemId, CancellationToken ct)
    {
        if (database.Database.ProviderName == PostgresProvider)
            return await database.CatalogItems.FromSqlInterpolated(
                $"SELECT * FROM \"CatalogItems\" WHERE \"Id\" = {itemId} AND \"DeletedAt\" IS NULL FOR UPDATE")
                .SingleOrDefaultAsync(ct);
        return await database.CatalogItems.SingleOrDefaultAsync(value => value.Id == itemId && value.DeletedAt == null, ct);
    }

    private async Task<InventoryStock?> LockStockAsync(Guid itemId, CancellationToken ct)
    {
        if (database.Database.ProviderName == PostgresProvider)
            return await database.Stocks.FromSqlInterpolated($"SELECT * FROM \"Stock\" WHERE \"ItemId\" = {itemId} FOR UPDATE")
                .SingleOrDefaultAsync(ct);
        return await database.Stocks.SingleOrDefaultAsync(value => value.ItemId == itemId, ct);
    }
}
