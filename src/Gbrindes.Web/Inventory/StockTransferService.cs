using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gbrindes.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MovementEntity = Gbrindes.Web.Data.StockMovement;

namespace Gbrindes.Web.Inventory;

public sealed record StockTransferResult(MovementEntity Outgoing, MovementEntity Incoming, InventoryStock Stock);

public sealed class StockTransferService(ApplicationDbContext database)
{
    private const string PostgresProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public async Task<StockTransferResult> TransferAsync(
        Guid itemId, int quantity, Guid industryId, Guid? requestedFromLocationId, Guid? toLocationId,
        Guid? eventId, string? notes, string idempotencyKey, Guid? userId, string? userName, CancellationToken ct)
    {
        if (itemId == Guid.Empty || industryId == Guid.Empty || quantity is < 1 or > 1_000_000 || notes?.Length > 1000)
            throw new InventoryFailureException(422, "VALIDATION_ERROR", "Dados da transferência inválidos.");
        if ((!eventId.HasValue && !toLocationId.HasValue) || (toLocationId.HasValue && toLocationId == requestedFromLocationId))
            throw new InventoryFailureException(422, "VALIDATION_ERROR", "Informe locais de origem e destino diferentes.");

        notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        idempotencyKey = idempotencyKey.Trim();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            itemId,
            quantity,
            industryId,
            fromLocationId = requestedFromLocationId,
            toLocationId,
            eventId,
            notes
        }))));
        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational()) transaction = await database.Database.BeginTransactionAsync(ct);

        try
        {
            var prior = await database.StockMovements.AsNoTracking()
                .SingleOrDefaultAsync(movement => movement.IdempotencyKey == idempotencyKey, ct);
            if (prior is not null)
            {
                if (prior.Type != "transferencia" || prior.IdempotencyRequestHash != hash || prior.TransferId is null)
                    throw new InventoryFailureException(409, "IDEMPOTENCY_CONFLICT", "Chave já utilizada com dados diferentes.");
                var replay = await LoadResultAsync(prior.TransferId.Value, itemId, ct);
                if (transaction is not null) await transaction.CommitAsync(ct);
                return replay;
            }

            CatalogItem? item;
            if (database.Database.ProviderName == PostgresProvider)
                item = await database.CatalogItems.FromSqlInterpolated($"SELECT * FROM \"CatalogItems\" WHERE \"Id\" = {itemId} AND \"DeletedAt\" IS NULL FOR UPDATE").SingleOrDefaultAsync(ct);
            else
                item = await database.CatalogItems.SingleOrDefaultAsync(value => value.Id == itemId && value.DeletedAt == null, ct);
            if (item is null) throw new InventoryFailureException(404, "ITEM_NOT_FOUND", "Brinde não encontrado.");
            if (item.Status != "ativo") throw new InventoryFailureException(422, "ITEM_INACTIVE", "Brindes inativos não podem ser transferidos.");

            Event? eventRecord = null;
            if (eventId is Guid linkedEventId)
            {
                eventRecord = database.Database.ProviderName == PostgresProvider
                    ? await database.Events.FromSqlInterpolated($"SELECT * FROM \"Events\" WHERE \"Id\" = {linkedEventId} AND \"DeletedAt\" IS NULL FOR UPDATE").SingleOrDefaultAsync(ct)
                    : await database.Events.SingleOrDefaultAsync(value => value.Id == linkedEventId && value.DeletedAt == null, ct);
                if (eventRecord is null) throw new InventoryFailureException(404, "NOT_FOUND", "Evento não encontrado.");
                if (eventRecord.Status is "encerrado" or "cancelado")
                    throw new InventoryFailureException(409, "EVENT_CLOSED", "Não é possível transferir para um evento encerrado.");
                if (eventRecord.LocationId is null)
                {
                    var nameKey = $"EVENT:{eventRecord.Id:N}";
                    var eventLocation = await database.Locations.SingleOrDefaultAsync(location => location.NameKey == nameKey
                        && location.Active && location.DeletedAt == null, ct);
                    if (eventLocation is null)
                    {
                        var now = DateTimeOffset.UtcNow;
                        var locationName = $"Evento: {eventRecord.Name}";
                        if (locationName.Length > 150) locationName = locationName[..150];
                        eventLocation = new Location
                        {
                            Name = locationName,
                            NameKey = nameKey,
                            Description = "Estoque temporário do evento (não é retirada)",
                            Kind = "evento",
                            Active = true,
                            CreatedBy = userId,
                            UpdatedBy = userId,
                            CreatedAt = now,
                            UpdatedAt = now
                        };
                        database.Locations.Add(eventLocation);
                    }
                    eventRecord.LocationId = eventLocation.Id;
                    eventRecord.UpdatedAt = DateTimeOffset.UtcNow;
                    eventRecord.UpdatedBy = userId;
                }
                toLocationId = eventRecord.LocationId;
                if (toLocationId == requestedFromLocationId)
                    throw new InventoryFailureException(422, "VALIDATION_ERROR", "O local de destino deve ser diferente da origem.");
            }

            var industry = await database.Industries.AsNoTracking().SingleOrDefaultAsync(value => value.Id == industryId && value.Active && value.DeletedAt == null, ct);
            if (industry is null) throw new InventoryFailureException(422, "INDUSTRY_NOT_FOUND", "Indústria inválida.");
            var actorIndustryId = userId is Guid actorId
                ? await database.Users.AsNoTracking().Where(value => value.Id == actorId).Select(value => value.IndustryId).SingleOrDefaultAsync(ct)
                : null;
            if (actorIndustryId is Guid scopedIndustryId && scopedIndustryId != industryId)
                throw new InventoryFailureException(403, "INDUSTRY_FORBIDDEN", "Você só pode transferir estoque para sua indústria.");

            var destinationIsTracked = database.Locations.Local.Any(location => location.Id == toLocationId
                && location.Active && location.DeletedAt == null);
            if (!destinationIsTracked && !await database.Locations.AnyAsync(location => location.Id == toLocationId
                && location.Active && location.DeletedAt == null, ct))
                throw new InventoryFailureException(422, "LOCATION_INVALID", "Local de destino inválido.");
            var fromLocationId = requestedFromLocationId
                ?? await database.Locations.AsNoTracking().Where(location => location.Active && location.DeletedAt == null)
                    .OrderBy(location => location.Kind == "cd" ? 0 : 1).ThenBy(location => location.Id)
                    .Select(location => (Guid?)location.Id).FirstOrDefaultAsync(ct);
            if (fromLocationId is null || !await database.Locations.AnyAsync(location => location.Id == fromLocationId && location.Active && location.DeletedAt == null, ct))
                throw new InventoryFailureException(422, "LOCATION_INVALID", "Local de origem inválido ou não configurado.");
            if (toLocationId is not Guid destinationLocationId)
                throw new InventoryFailureException(422, "LOCATION_INVALID", "Local de destino inválido ou não configurado.");
            if (fromLocationId == toLocationId)
                throw new InventoryFailureException(422, "VALIDATION_ERROR", "O local de destino deve ser diferente da origem.");

            var stock = await database.Stocks.SingleOrDefaultAsync(value => value.ItemId == itemId, ct);
            if (stock is null) throw new InventoryFailureException(422, "STOCK_INSUFFICIENT", "Estoque insuficiente.");
            var source = await database.StockPositions.SingleOrDefaultAsync(position => position.ItemId == itemId && position.LocationId == fromLocationId, ct);
            if (source is null || source.Quantity < quantity)
                throw new InventoryFailureException(422, "STOCK_INSUFFICIENT", $"Saldo insuficiente no local de origem. Disponível: {source?.Quantity ?? 0}.");
            if (stock.OnHand - quantity < stock.Reserved)
                throw new InventoryFailureException(422, "STOCK_INSUFFICIENT", "A transferência reduziria o saldo agregado abaixo do reservado.");

            var destination = await database.StockPositions.SingleOrDefaultAsync(position => position.ItemId == itemId && position.LocationId == destinationLocationId, ct);
            if (destination is null)
            {
                destination = new StockPosition { ItemId = itemId, LocationId = destinationLocationId };
                database.StockPositions.Add(destination);
            }
            var sourceBefore = source.Quantity;
            var destinationBefore = destination.Quantity;
            source.Quantity -= quantity;
            source.UpdatedAt = destination.UpdatedAt = DateTimeOffset.UtcNow;
            destination.Quantity = checked(destination.Quantity + quantity);

            var transferId = Guid.NewGuid();
            var createdAt = DateTimeOffset.UtcNow;
            var outgoing = new MovementEntity
            {
                ItemId = itemId,
                UserId = userId,
                Type = "transferencia",
                Quantity = -quantity,
                BalanceAfter = stock.OnHand,
                Reason = "Transferência entre locais",
                Notes = notes,
                IndustryId = industryId,
                LocationId = fromLocationId,
                FromLocationId = fromLocationId,
                ToLocationId = destinationLocationId,
                TransferId = transferId,
                IdempotencyKey = idempotencyKey,
                IdempotencyRequestHash = hash,
                CreatedAt = createdAt
            };
            var incoming = new MovementEntity
            {
                ItemId = itemId,
                UserId = userId,
                Type = "transferencia",
                Quantity = quantity,
                BalanceAfter = stock.OnHand,
                Reason = "Transferência entre locais",
                Notes = notes,
                IndustryId = industryId,
                LocationId = destinationLocationId,
                FromLocationId = fromLocationId,
                ToLocationId = destinationLocationId,
                TransferId = transferId,
                CreatedAt = createdAt
            };
            database.StockMovements.AddRange(outgoing, incoming);
            if (eventRecord is not null)
            {
                var allocation = await database.EventAllocations.SingleOrDefaultAsync(value => value.EventId == eventRecord.Id
                    && value.IndustryId == industryId && value.ItemId == itemId, ct);
                if (allocation is null)
                {
                    allocation = new EventAllocation { EventId = eventRecord.Id, IndustryId = industryId, ItemId = itemId };
                    database.EventAllocations.Add(allocation);
                }
                allocation.QuantityAllocated = checked(allocation.QuantityAllocated + quantity);
                allocation.UpdatedAt = createdAt;
                if (eventRecord.Status == "aberto")
                {
                    stock.Reserved = checked(stock.Reserved + quantity);
                    stock.UpdatedAt = createdAt;
                }
            }
            database.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                UserName = userName,
                Action = "stock_transfer",
                EntityType = "item",
                EntityId = itemId,
                EntityLabel = $"{item.Code} - {item.Name}",
                BeforeJson = JsonSerializer.Serialize(new
                {
                    on_hand = stock.OnHand,
                    from_location_id = fromLocationId,
                    from_quantity = sourceBefore,
                    to_location_id = toLocationId,
                    to_quantity = destinationBefore
                }),
                AfterJson = JsonSerializer.Serialize(new
                {
                    on_hand = stock.OnHand,
                    from_location_id = fromLocationId,
                    from_quantity = source.Quantity,
                    to_location_id = toLocationId,
                    to_quantity = destination.Quantity,
                    quantity,
                    transfer_id = transferId
                }),
                CreatedAt = createdAt
            });
            await database.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return new StockTransferResult(outgoing, incoming, stock);
        }
        catch (DbUpdateException) when (transaction is not null)
        {
            await transaction.RollbackAsync(ct);
            database.ChangeTracker.Clear();
            var prior = await database.StockMovements.AsNoTracking().SingleOrDefaultAsync(movement => movement.IdempotencyKey == idempotencyKey, ct);
            if (prior is not null && prior.Type == "transferencia" && prior.IdempotencyRequestHash == hash && prior.TransferId is Guid transferId)
                return await LoadResultAsync(transferId, itemId, ct);
            if (prior is not null) throw new InventoryFailureException(409, "IDEMPOTENCY_CONFLICT", "Chave já utilizada com dados diferentes.");
            throw;
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(ct);
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    private async Task<StockTransferResult> LoadResultAsync(Guid transferId, Guid itemId, CancellationToken ct)
    {
        var movements = await database.StockMovements.AsNoTracking().Where(movement => movement.TransferId == transferId)
            .OrderBy(movement => movement.Quantity).ToListAsync(ct);
        if (movements.Count != 2) throw new InventoryFailureException(409, "TRANSFER_INCOMPLETE", "Registro de transferência inconsistente.");
        var stock = await database.Stocks.AsNoTracking().SingleAsync(value => value.ItemId == itemId, ct);
        return new StockTransferResult(movements[0], movements[1], stock);
    }
}
