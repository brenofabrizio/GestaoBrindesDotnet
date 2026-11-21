using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gbrindes.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MovementEntity = Gbrindes.Web.Data.StockMovement;

namespace Gbrindes.Web.Inventory;

public sealed record EventReturnLine(Guid IndustryId, Guid ItemId, int Quantity);

public sealed class EventReturnService(ApplicationDbContext database)
{
    private const string PostgresProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public async Task<Event> ReturnAsync(Guid eventId, Guid userId, string? userName, string? ip,
        IReadOnlyCollection<EventReturnLine> requestedLines, string idempotencyKey, CancellationToken ct)
    {
        if (requestedLines.Count is 0 or > 100 || requestedLines.Any(line => line.IndustryId == Guid.Empty
            || line.ItemId == Guid.Empty || line.Quantity is < 1 or > 1_000_000))
            throw new InventoryFailureException(422, "VALIDATION_ERROR", "Informe itens e quantidades válidos para devolução.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(idempotencyKey, "^[A-Za-z0-9_-]{8,80}$"))
            throw new InventoryFailureException(400, "IDEMPOTENCY_KEY_REQUIRED", "Cabeçalho Idempotency-Key ausente ou inválido.");

        var lines = requestedLines.GroupBy(line => (line.IndustryId, line.ItemId))
            .Select(group => new EventReturnLine(group.Key.IndustryId, group.Key.ItemId,
                group.Aggregate(0, (quantity, line) => checked(quantity + line.Quantity))))
            .OrderBy(line => line.IndustryId).ThenBy(line => line.ItemId).ToArray();
        var assignedIndustryId = await database.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.IndustryId)
            .SingleOrDefaultAsync(ct);
        if (assignedIndustryId is Guid scopedIndustryId && lines.Any(line => line.IndustryId != scopedIndustryId))
            throw new InventoryFailureException(403, "INDUSTRY_FORBIDDEN", "Você só pode devolver estoque da sua indústria.");
        var keyHash = Hash($"event-return:{eventId:N}:{idempotencyKey}");
        var payloadHash = Hash(JsonSerializer.Serialize(lines));
        await using IDbContextTransaction? transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(ct) : null;
        try
        {
            Event? eventRecord = database.Database.ProviderName == PostgresProvider
                ? await database.Events.FromSqlInterpolated($"SELECT * FROM \"Events\" WHERE \"Id\" = {eventId} AND \"DeletedAt\" IS NULL FOR UPDATE").SingleOrDefaultAsync(ct)
                : await database.Events.SingleOrDefaultAsync(value => value.Id == eventId && value.DeletedAt == null, ct);
            if (eventRecord is null)
                throw new InventoryFailureException(404, "NOT_FOUND", "Evento não encontrado.");

            var prior = await database.AuditLogs.AsNoTracking().Where(log => log.EntityType == "event"
                && log.EntityId == eventId && log.Action == "return").Select(log => log.AfterJson).ToListAsync(ct);
            foreach (var json in prior)
            {
                if (string.IsNullOrEmpty(json)) continue;
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("idempotency_key_hash", out var keyElement)
                    || keyElement.GetString() != keyHash) continue;
                if (!document.RootElement.TryGetProperty("request_hash", out var requestElement)
                    || requestElement.GetString() != payloadHash)
                    throw new InventoryFailureException(409, "IDEMPOTENCY_CONFLICT", "Chave já utilizada com dados diferentes.");
                if (transaction is not null) await transaction.CommitAsync(ct);
                return eventRecord;
            }

            if (eventRecord.Status is not ("aberto" or "encerrado"))
                throw new InventoryFailureException(422, "EVENT_NOT_OPEN", "A devolução ao CD só vale para eventos abertos ou encerrados.");
            var now = DateTimeOffset.UtcNow;
            foreach (var line in lines)
            {
                var allocation = await database.EventAllocations.SingleOrDefaultAsync(value => value.EventId == eventId
                    && value.IndustryId == line.IndustryId && value.ItemId == line.ItemId, ct);
                if (allocation is null)
                    throw new InventoryFailureException(422, "ALLOCATION_EXCEEDED", "Este brinde não está na cota desta indústria.");
                var remaining = allocation.QuantityAllocated - allocation.QuantityWithdrawn;
                if (line.Quantity > remaining)
                    throw new InventoryFailureException(422, "STOCK_INSUFFICIENT", $"Não há essa quantidade para devolver. Saldo do evento: {remaining}.");
                if (eventRecord.Status == "aberto")
                {
                    var stock = database.Database.ProviderName == PostgresProvider
                        ? await database.Stocks.FromSqlInterpolated($"SELECT * FROM \"Stock\" WHERE \"ItemId\" = {line.ItemId} FOR UPDATE").SingleOrDefaultAsync(ct)
                        : await database.Stocks.SingleOrDefaultAsync(value => value.ItemId == line.ItemId, ct);
                    if (stock is null || stock.Reserved < line.Quantity)
                        throw new InventoryFailureException(422, "RESERVATION_INVALID", "Quantidade a liberar maior que a reservada.");
                    stock.Reserved -= line.Quantity;
                    stock.UpdatedAt = now;
                }

                if (eventRecord.LocationId is Guid fromLocationId)
                {
                    var cdLocationId = await database.Locations.AsNoTracking()
                        .Where(location => location.Active && location.DeletedAt == null && location.Kind == "cd")
                        .OrderBy(location => location.Id).Select(location => (Guid?)location.Id).FirstOrDefaultAsync(ct);
                    if (cdLocationId is Guid toLocationId && toLocationId != fromLocationId)
                    {
                        if (database.Database.ProviderName == PostgresProvider && eventRecord.Status != "aberto")
                            _ = await database.Stocks.FromSqlInterpolated(
                                $"SELECT * FROM \"Stock\" WHERE \"ItemId\" = {line.ItemId} FOR UPDATE").SingleOrDefaultAsync(ct);
                        var source = await database.StockPositions.SingleOrDefaultAsync(position =>
                            position.ItemId == line.ItemId && position.LocationId == fromLocationId, ct);
                        var transferQuantity = Math.Min(line.Quantity, source?.Quantity ?? 0);
                        if (transferQuantity > 0)
                        {
                            var destination = await database.StockPositions.SingleOrDefaultAsync(position =>
                                position.ItemId == line.ItemId && position.LocationId == toLocationId, ct);
                            if (destination is null)
                            {
                                destination = new StockPosition { ItemId = line.ItemId, LocationId = toLocationId };
                                database.StockPositions.Add(destination);
                            }
                            source!.Quantity -= transferQuantity;
                            destination.Quantity = checked(destination.Quantity + transferQuantity);
                            source.UpdatedAt = destination.UpdatedAt = now;
                            var transferId = Guid.NewGuid();
                            var balanceAfter = await database.Stocks.Where(value => value.ItemId == line.ItemId)
                                .Select(value => (int?)value.OnHand).SingleOrDefaultAsync(ct) ?? 0;
                            database.StockMovements.AddRange(
                                new MovementEntity
                                {
                                    ItemId = line.ItemId,
                                    UserId = userId,
                                    IndustryId = line.IndustryId,
                                    Type = "transferencia",
                                    Quantity = -transferQuantity,
                                    BalanceAfter = balanceAfter,
                                    Reason = "Devolução ao CD",
                                    Purpose = $"Devolução ao CD: {eventRecord.Name}",
                                    Notes = "Saldo do evento voltando para o estoque do CD",
                                    LocationId = fromLocationId,
                                    FromLocationId = fromLocationId,
                                    ToLocationId = toLocationId,
                                    TransferId = transferId,
                                    CreatedAt = now
                                },
                                new MovementEntity
                                {
                                    ItemId = line.ItemId,
                                    UserId = userId,
                                    IndustryId = line.IndustryId,
                                    Type = "transferencia",
                                    Quantity = transferQuantity,
                                    BalanceAfter = balanceAfter,
                                    Reason = "Devolução ao CD",
                                    Purpose = $"Devolução ao CD: {eventRecord.Name}",
                                    Notes = "Saldo do evento voltando para o estoque do CD",
                                    LocationId = toLocationId,
                                    FromLocationId = fromLocationId,
                                    ToLocationId = toLocationId,
                                    TransferId = transferId,
                                    CreatedAt = now
                                });
                        }
                    }
                }
                allocation.QuantityAllocated -= line.Quantity;
                allocation.UpdatedAt = now;
            }
            database.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                UserName = userName,
                Action = "return",
                EntityType = "event",
                EntityId = eventId,
                EntityLabel = eventRecord.Name,
                AfterJson = JsonSerializer.Serialize(new { idempotency_key_hash = keyHash, request_hash = payloadHash, items = lines }),
                Ip = ip,
                CreatedAt = now
            });
            await database.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return eventRecord;
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
