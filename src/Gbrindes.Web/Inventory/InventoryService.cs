using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gbrindes.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MovementEntity = Gbrindes.Web.Data.StockMovement;

namespace Gbrindes.Web.Inventory;

public sealed class InventoryFailureException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed class InventoryService(ApplicationDbContext database)
{
    private const string PostgresProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public async Task<MovementEntity> ReverseAsync(Guid movementId, string? reason, Guid? userId, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3 || reason.Trim().Length > 255)
            throw new InventoryFailureException(422, "VALIDATION_ERROR", "Motivo do estorno inválido.");
        key = key.Trim();
        reason = reason.Trim();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { movementId, reason }))));
        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational()) transaction = await database.Database.BeginTransactionAsync(ct);
        try
        {
            var source = await database.StockMovements.AsNoTracking().SingleOrDefaultAsync(x => x.Id == movementId, ct);
            if (source is null) throw new InventoryFailureException(404, "MOVEMENT_NOT_FOUND", "Movimentação não encontrada.");
            if (source.Type == "transferencia") throw new InventoryFailureException(422, "REVERSE_TRANSFER", "Transferências não podem ser estornadas.");
            var prior = await database.StockMovements.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
            if (prior is not null)
            {
                if (prior.Type == "estorno" && prior.IdempotencyRequestHash == hash)
                {
                    if (transaction is not null) await transaction.CommitAsync(ct);
                    return prior;
                }
                throw new InventoryFailureException(409, "IDEMPOTENCY_CONFLICT", "Chave já utilizada com dados diferentes.");
            }
            CatalogItem? item;
            if (database.Database.ProviderName == PostgresProvider)
                item = await database.CatalogItems.FromSqlInterpolated($"SELECT * FROM \"CatalogItems\" WHERE \"Id\" = {source.ItemId} AND \"DeletedAt\" IS NULL FOR UPDATE").SingleOrDefaultAsync(ct);
            else item = await database.CatalogItems.SingleOrDefaultAsync(x => x.Id == source.ItemId && x.DeletedAt == null, ct);
            if (item is null) throw new InventoryFailureException(404, "ITEM_NOT_FOUND", "Brinde não encontrado.");
            var stock = await database.Stocks.SingleOrDefaultAsync(x => x.ItemId == source.ItemId, ct);
            if (stock is null) throw new InventoryFailureException(422, "STOCK_INSUFFICIENT", "Estoque insuficiente.");
            var delta = checked(-source.Quantity);
            var available = stock.OnHand - stock.Reserved;
            if (delta < 0 && -delta > available) throw new InventoryFailureException(422, "STOCK_INSUFFICIENT", $"Estoque insuficiente. Disponível: {available}.");
            var locationId = source.LocationId ?? await StockPositionService.ResolveLocationIdAsync(database, item.LocationId, ct);
            await StockPositionService.ApplyDeltaAsync(database, source.ItemId, locationId, delta, ct);
            stock.OnHand = checked(stock.OnHand + delta);
            stock.UpdatedAt = DateTimeOffset.UtcNow;
            var reversal = new MovementEntity { ItemId = source.ItemId, UserId = userId, Type = "estorno", Quantity = delta, BalanceAfter = stock.OnHand, Reason = reason, Notes = $"Estorno da movimentação #{movementId}", DocumentRef = source.DocumentRef, LocationId = locationId, IdempotencyKey = key, IdempotencyRequestHash = hash };
            database.StockMovements.Add(reversal);
            await database.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return reversal;
        }
        catch (DbUpdateException) when (transaction is not null)
        {
            await transaction.RollbackAsync(ct);
            database.ChangeTracker.Clear();
            var prior = await database.StockMovements.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
            if (prior is not null && prior.Type == "estorno" && prior.IdempotencyRequestHash == hash)
                return prior;
            if (prior is not null)
                throw new InventoryFailureException(409, "IDEMPOTENCY_CONFLICT", "Chave já utilizada com dados diferentes.");
            throw;
        }
        catch { if (transaction is not null) await transaction.RollbackAsync(ct); throw; }
        finally { if (transaction is not null) await transaction.DisposeAsync(); }
    }

    public async Task<MovementEntity> AdjustAsync(Guid itemId, string mode, int quantity, Guid? userId, string? reason, string? notes, string key, CancellationToken ct)
    {
        if (mode is not ("set" or "delta") || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length is < 3 or > 255 || notes?.Length > 1000 || (mode == "set" && quantity < 0) || (mode == "delta" && quantity == 0))
            throw new InventoryFailureException(422, "VALIDATION_ERROR", "Dados de ajuste inválidos.");
        if (quantity is < -1_000_000 or > 1_000_000)
            throw new InventoryFailureException(422, "VALIDATION_ERROR", "Quantidade inválida.");
        if (itemId == Guid.Empty)
            throw new InventoryFailureException(422, "VALIDATION_ERROR", "Informe um brinde válido.");

        key = key.Trim();
        reason = reason.Trim();
        notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        var requestHash = ComputeAdjustmentRequestHash(itemId, mode, quantity, reason, notes);
        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational())
            transaction = await database.Database.BeginTransactionAsync(ct);

        try
        {
            CatalogItem? item;
            if (database.Database.ProviderName == PostgresProvider)
            {
                item = await database.CatalogItems
                    .FromSqlInterpolated($"SELECT * FROM \"CatalogItems\" WHERE \"Id\" = {itemId} AND \"DeletedAt\" IS NULL FOR UPDATE")
                    .SingleOrDefaultAsync(ct);
            }
            else
            {
                item = await database.CatalogItems.SingleOrDefaultAsync(x => x.Id == itemId && x.DeletedAt == null, ct);
            }

            if (item is null)
                throw new InventoryFailureException(422, "VALIDATION_ERROR", "Brinde inválido.");

            var prior = await database.StockMovements.AsNoTracking()
                .SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
            if (prior is not null)
            {
                if (prior.ItemId == itemId && prior.Type == "ajuste" && prior.IdempotencyRequestHash == requestHash)
                {
                    if (transaction is not null)
                        await transaction.CommitAsync(ct);
                    return prior;
                }

                throw new InventoryFailureException(409, "IDEMPOTENCY_CONFLICT", "Chave já utilizada com dados diferentes.");
            }

            var stock = await database.Stocks.SingleOrDefaultAsync(x => x.ItemId == itemId, ct);
            if (stock is null)
            {
                stock = new InventoryStock { ItemId = itemId, Item = item };
                database.Stocks.Add(stock);
            }

            var previousOnHand = stock.OnHand;
            var target = mode == "set" ? quantity : checked(previousOnHand + quantity);
            if (target < 0)
                throw new InventoryFailureException(422, "STOCK_INSUFFICIENT", "O ajuste deixaria o estoque negativo.");
            if (target < stock.Reserved)
                throw new InventoryFailureException(422, "STOCK_BELOW_RESERVED", "O ajuste deixaria o estoque abaixo do reservado.");
            if (target == stock.OnHand)
                throw new InventoryFailureException(422, "ADJUSTMENT_NO_CHANGE", "O saldo já corresponde ao ajuste.");

            var delta = target - previousOnHand;
            var locationId = await StockPositionService.ResolveLocationIdAsync(database, item.LocationId, ct);
            await StockPositionService.ApplyDeltaAsync(database, itemId, locationId, delta, ct);
            stock.OnHand = target;
            stock.UpdatedAt = DateTimeOffset.UtcNow;
            var movement = new MovementEntity
            {
                ItemId = itemId,
                Item = item,
                UserId = userId,
                Type = "ajuste",
                Quantity = delta,
                BalanceAfter = target,
                Reason = reason,
                Notes = notes,
                LocationId = locationId,
                IdempotencyKey = key,
                IdempotencyRequestHash = requestHash,
                CreatedAt = DateTimeOffset.UtcNow
            };
            database.StockMovements.Add(movement);
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return movement;
        }
        catch (DbUpdateException) when (transaction is not null)
        {
            await transaction.RollbackAsync(ct);
            database.ChangeTracker.Clear();
            var prior = await database.StockMovements.AsNoTracking()
                .SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
            if (prior is not null && prior.ItemId == itemId && prior.Type == "ajuste" && prior.IdempotencyRequestHash == requestHash)
                return prior;
            if (prior is not null)
                throw new InventoryFailureException(409, "IDEMPOTENCY_CONFLICT", "Chave já utilizada com dados diferentes.");
            throw;
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

    private static string ComputeAdjustmentRequestHash(Guid itemId, string mode, int quantity, string reason, string? notes)
    {
        var payload = JsonSerializer.Serialize(new { itemId, mode, quantity, reason, notes });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    public async Task<MovementEntity> MoveAsync(
        Guid itemId,
        string type,
        int quantity,
        Guid? userId,
        string? reason,
        string? notes,
        string? documentRef,
        string? purpose,
        string? recipient,
        string? purchaseTicketNo,
        Guid? supplierId,
        string? idempotencyKey,
        CancellationToken cancellationToken,
        Guid? industryId = null,
        Guid? deliveryId = null,
        decimal? unitValue = null,
        string? attachmentPath = null,
        string? attachmentSha256 = null)
    {
        if (quantity <= 0)
            throw new InventoryFailureException(422, "INVALID_QUANTITY", "A quantidade deve ser maior que zero.");
        if (unitValue is < 0 or > 9_999_999_999m)
            throw new InventoryFailureException(422, "INVALID_UNIT_VALUE", "O valor unitário deve estar entre 0 e 9.999.999.999,00.");
        if (type == "saida" && string.IsNullOrWhiteSpace(reason))
            throw new InventoryFailureException(422, "REASON_REQUIRED", "Informe o motivo da saída.");

        idempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        documentRef = string.IsNullOrWhiteSpace(documentRef) ? null : documentRef.Trim();
        purpose = string.IsNullOrWhiteSpace(purpose) ? null : purpose.Trim();
        recipient = string.IsNullOrWhiteSpace(recipient) ? null : recipient.Trim();
        purchaseTicketNo = string.IsNullOrWhiteSpace(purchaseTicketNo) ? null : purchaseTicketNo.Trim();
        var requestHash = idempotencyKey is null
            ? null
            : ComputeMovementRequestHash(itemId, type, quantity, reason, notes, documentRef, purpose, recipient, purchaseTicketNo, supplierId, industryId, deliveryId, unitValue, attachmentSha256);

        var transaction = database.Database.CurrentTransaction;
        var ownsTransaction = false;
        if (database.Database.IsRelational() && transaction is null)
        {
            transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            ownsTransaction = true;
        }

        try
        {
            CatalogItem? item;
            if (database.Database.ProviderName == PostgresProvider)
            {
                item = await database.CatalogItems
                    .FromSqlInterpolated($"SELECT * FROM \"CatalogItems\" WHERE \"Id\" = {itemId} AND \"DeletedAt\" IS NULL FOR UPDATE")
                    .SingleOrDefaultAsync(cancellationToken);
            }
            else
            {
                item = await database.CatalogItems.SingleOrDefaultAsync(x => x.Id == itemId && x.DeletedAt == null, cancellationToken);
            }

            if (item is null)
                throw new InventoryFailureException(404, "ITEM_NOT_FOUND", "Brinde não encontrado.");
            if (supplierId is Guid requestedSupplierId && !await database.Suppliers.AnyAsync(supplier => supplier.Id == requestedSupplierId, cancellationToken))
                throw new InventoryFailureException(422, "SUPPLIER_NOT_FOUND", "Fornecedor inválido.");
            if (industryId is Guid requestedIndustryId)
            {
                if (!await database.Industries.AnyAsync(industry => industry.Id == requestedIndustryId, cancellationToken))
                    throw new InventoryFailureException(422, "INDUSTRY_NOT_FOUND", "Indústria inválida.");
                if (userId is Guid actorId)
                {
                    var actorIndustryId = await database.Users.AsNoTracking()
                        .Where(user => user.Id == actorId)
                        .Select(user => user.IndustryId)
                        .SingleOrDefaultAsync(cancellationToken);
                    if (actorIndustryId is Guid scopedIndustryId && scopedIndustryId != requestedIndustryId)
                        throw new InventoryFailureException(403, "INDUSTRY_FORBIDDEN", "Você só pode movimentar estoque pela sua indústria.");
                }
            }

            if (idempotencyKey is not null)
            {
                var prior = await database.StockMovements.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken);
                if (prior is not null)
                    return ExistingOrConflict(prior, itemId, type, quantity, requestHash!, reason, notes,
                        documentRef, purpose, recipient, purchaseTicketNo, supplierId, industryId, deliveryId, unitValue, attachmentSha256);
            }

            var stock = await database.Stocks.SingleOrDefaultAsync(x => x.ItemId == itemId, cancellationToken);
            if (stock is null)
            {
                stock = new InventoryStock { ItemId = itemId, Item = item };
                database.Stocks.Add(stock);
            }

            var delta = type == "saida" ? -quantity : quantity;
            var available = stock.OnHand - stock.Reserved;
            if (delta < 0 && quantity > available)
                throw new InventoryFailureException(422, "STOCK_INSUFFICIENT", $"Estoque insuficiente. Disponível: {available}.");

            var locationId = await StockPositionService.ResolveLocationIdAsync(database, item.LocationId, cancellationToken);
            await StockPositionService.ApplyDeltaAsync(database, itemId, locationId, delta, cancellationToken);
            stock.OnHand = checked(stock.OnHand + delta);
            stock.UpdatedAt = DateTimeOffset.UtcNow;
            var movement = new MovementEntity
            {
                ItemId = itemId,
                Item = item,
                UserId = userId,
                IndustryId = industryId,
                Type = type,
                Quantity = delta,
                BalanceAfter = stock.OnHand,
                LocationId = locationId,
                Reason = reason,
                Notes = notes,
                DocumentRef = documentRef,
                SupplierId = supplierId,
                DeliveryId = deliveryId,
                Purpose = purpose,
                Recipient = recipient,
                PurchaseTicketNo = purchaseTicketNo,
                UnitValue = unitValue ?? item.UnitValue,
                AttachmentPath = attachmentPath,
                IdempotencyKey = idempotencyKey,
                IdempotencyRequestHash = requestHash,
                CreatedAt = DateTimeOffset.UtcNow
            };
            database.StockMovements.Add(movement);
            await database.SaveChangesAsync(cancellationToken);
            if (ownsTransaction)
                await transaction!.CommitAsync(cancellationToken);
            return movement;
        }
        catch (DbUpdateException) when (idempotencyKey is not null && ownsTransaction)
        {
            if (ownsTransaction)
                await transaction!.RollbackAsync(cancellationToken);
            database.ChangeTracker.Clear();
            var prior = await database.StockMovements.AsNoTracking()
                .SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken);
            if (prior is not null)
                return ExistingOrConflict(prior, itemId, type, quantity, requestHash!, reason, notes,
                    documentRef, purpose, recipient, purchaseTicketNo, supplierId, industryId, deliveryId, unitValue, attachmentSha256);
            throw;
        }
        catch
        {
            if (ownsTransaction)
                await transaction!.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (ownsTransaction && transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private static MovementEntity ExistingOrConflict(MovementEntity movement, Guid itemId, string type, int quantity,
        string requestHash, string? reason, string? notes, string? documentRef, string? purpose,
        string? recipient, string? purchaseTicketNo, Guid? supplierId, Guid? industryId, Guid? deliveryId,
        decimal? unitValue, string? attachmentSha256)
    {
        var delta = type == "saida" ? -quantity : quantity;
        if (movement.ItemId == itemId && movement.Type == type && movement.Quantity == delta)
        {
            if (movement.IdempotencyRequestHash is not null && movement.IdempotencyRequestHash == requestHash)
                return movement;
            if (movement.IdempotencyRequestHash is null && unitValue is null && attachmentSha256 is null && movement.Reason == reason && movement.Notes == notes
                && movement.DocumentRef == documentRef && movement.Purpose == purpose
                && movement.Recipient == recipient && movement.PurchaseTicketNo == purchaseTicketNo
                && movement.SupplierId == supplierId && movement.IndustryId == industryId && movement.DeliveryId == deliveryId)
                return movement;
        }
        throw new InventoryFailureException(409, "IDEMPOTENCY_CONFLICT", "A chave de idempotência já foi usada com dados diferentes.");
    }

    private static string ComputeMovementRequestHash(Guid itemId, string type, int quantity, string? reason,
        string? notes, string? documentRef, string? purpose, string? recipient, string? purchaseTicketNo,
        Guid? supplierId, Guid? industryId, Guid? deliveryId, decimal? unitValue, string? attachmentSha256)
    {
        var payload = attachmentSha256 is not null
            ? JsonSerializer.Serialize(new { itemId, type, quantity, reason, notes, documentRef, purpose, recipient, purchaseTicketNo, supplierId, industryId, deliveryId, unitValue, attachmentSha256 })
            : unitValue.HasValue
            ? JsonSerializer.Serialize(new { itemId, type, quantity, reason, notes, documentRef, purpose, recipient, purchaseTicketNo, supplierId, industryId, deliveryId, unitValue })
            : (supplierId, industryId, deliveryId) switch
        {
            (null, null, null) => JsonSerializer.Serialize(new { itemId, type, quantity, reason, notes, documentRef, purpose, recipient, purchaseTicketNo }),
            (not null, null, null) => JsonSerializer.Serialize(new { itemId, type, quantity, reason, notes, documentRef, purpose, recipient, purchaseTicketNo, supplierId }),
            (null, not null, null) => JsonSerializer.Serialize(new { itemId, type, quantity, reason, notes, documentRef, purpose, recipient, purchaseTicketNo, industryId }),
            (not null, not null, null) => JsonSerializer.Serialize(new { itemId, type, quantity, reason, notes, documentRef, purpose, recipient, purchaseTicketNo, supplierId, industryId }),
            _ => JsonSerializer.Serialize(new { itemId, type, quantity, reason, notes, documentRef, purpose, recipient, purchaseTicketNo, supplierId, industryId, deliveryId })
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
