using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gbrindes.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MovementEntity = Gbrindes.Web.Data.StockMovement;

namespace Gbrindes.Web.Inventory;

public sealed record ExitAuthorizationResult(StockExitOrder Order, int OnHand, int Reserved, bool Replayed);

public sealed class StockExitOrderService(ApplicationDbContext database)
{
    private const string PostgresProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public async Task<ExitAuthorizationResult> AuthorizeAsync(
        Guid itemId,
        int quantity,
        string purpose,
        string? recipient,
        Guid? industryId,
        Guid? departmentId,
        Guid? requesterId,
        string? purchaseTicketNo,
        string? documentRef,
        string? notes,
        Guid userId,
        string userName,
        string idempotencyKey,
        CancellationToken cancellationToken,
        string? attachmentPath = null,
        string? attachmentSha256 = null)
    {
        purpose = purpose.Trim();
        recipient = Normalize(recipient);
        purchaseTicketNo = Normalize(purchaseTicketNo);
        documentRef = Normalize(documentRef);
        notes = Normalize(notes);
        idempotencyKey = idempotencyKey.Trim();
        if (itemId == Guid.Empty || quantity is < 1 or > 1_000_000 || purpose.Length == 0 || purpose.Length > 255
            || recipient?.Length > 150 || purchaseTicketNo?.Length > 50 || documentRef?.Length > 60 || notes?.Length > 1_000)
            throw new InventoryFailureException(422, "VALIDATION_ERROR", "Dados da saída autorizada inválidos.");

        var requestHash = ComputeRequestHash(itemId, quantity, purpose, recipient, industryId, departmentId,
            requesterId, purchaseTicketNo, documentRef, notes, attachmentSha256);
        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational())
            transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var prior = await database.StockExitOrders.AsNoTracking()
                .SingleOrDefaultAsync(order => order.AuthorizedBy == userId && order.IdempotencyKey == idempotencyKey, cancellationToken);
            if (prior is not null)
            {
                if (prior.IdempotencyRequestHash != requestHash)
                    throw new InventoryFailureException(409, "IDEMPOTENCY_CONFLICT", "A chave de idempotência já foi usada com dados diferentes.");
                var priorStock = await database.Stocks.AsNoTracking()
                    .SingleAsync(stock => stock.ItemId == prior.ItemId, cancellationToken);
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);
                return new ExitAuthorizationResult(prior, priorStock.OnHand, priorStock.Reserved, true);
            }

            CatalogItem? item;
            if (database.Database.ProviderName == PostgresProvider)
                item = await database.CatalogItems.FromSqlInterpolated(
                    $"SELECT * FROM \"CatalogItems\" WHERE \"Id\" = {itemId} AND \"DeletedAt\" IS NULL FOR UPDATE")
                    .SingleOrDefaultAsync(cancellationToken);
            else
                item = await database.CatalogItems.SingleOrDefaultAsync(
                    candidate => candidate.Id == itemId && candidate.DeletedAt == null, cancellationToken);
            if (item is null)
                throw new InventoryFailureException(404, "ITEM_NOT_FOUND", "Brinde não encontrado.");
            if (item.Status != "ativo")
                throw new InventoryFailureException(422, "ITEM_INACTIVE", "Brindes inativos não podem ter saída autorizada.");

            if (industryId.HasValue && !await database.Industries.AnyAsync(value => value.Id == industryId.Value, cancellationToken))
                throw new InventoryFailureException(422, "VALIDATION_ERROR", "Indústria inválida.");
            if (departmentId.HasValue && !await database.Departments.AnyAsync(value => value.Id == departmentId.Value, cancellationToken))
                throw new InventoryFailureException(422, "VALIDATION_ERROR", "Departamento inválido.");
            if (requesterId.HasValue && !await database.Users.AnyAsync(value => value.Id == requesterId.Value, cancellationToken))
                throw new InventoryFailureException(422, "VALIDATION_ERROR", "Solicitante inválido.");

            InventoryStock? stock;
            if (database.Database.ProviderName == PostgresProvider)
                stock = await database.Stocks.FromSqlInterpolated(
                    $"SELECT * FROM \"Stock\" WHERE \"ItemId\" = {itemId} FOR UPDATE")
                    .SingleOrDefaultAsync(cancellationToken);
            else
                stock = await database.Stocks.SingleOrDefaultAsync(value => value.ItemId == itemId, cancellationToken);
            var available = stock is null ? 0 : stock.OnHand - stock.Reserved;
            if (quantity > available)
                throw new InventoryFailureException(422, "STOCK_INSUFFICIENT", $"Estoque insuficiente. Disponível: {available}.");

            var year = DateTimeOffset.UtcNow.Year;
            var sequence = await NextSequenceAsync("exit", year.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var order = new StockExitOrder
            {
                Code = $"SAI-{year}-{sequence:0000}",
                ItemId = itemId,
                Quantity = quantity,
                Purpose = purpose,
                Recipient = recipient,
                IndustryId = industryId,
                DepartmentId = departmentId,
                RequesterId = requesterId,
                PurchaseTicketNo = purchaseTicketNo,
                DocumentRef = documentRef,
                AttachmentPath = attachmentPath,
                Notes = notes,
                AuthorizedBy = userId,
                AuthorizedAt = now,
                IdempotencyKey = idempotencyKey,
                IdempotencyRequestHash = requestHash,
                CreatedAt = now,
                UpdatedAt = now
            };
            stock!.Reserved = checked(stock.Reserved + quantity);
            stock.UpdatedAt = now;
            database.StockExitOrders.Add(order);
            database.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                UserName = userName,
                Action = "stock_exit_authorize",
                EntityType = "stock_exit_order",
                EntityId = order.Id,
                EntityLabel = order.Code,
                AfterJson = JsonSerializer.Serialize(new { code = order.Code, item_id = itemId, quantity, status = order.Status }),
                CreatedAt = now
            });
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return new ExitAuthorizationResult(order, stock.OnHand, stock.Reserved, false);
        }
        catch (DbUpdateException) when (transaction is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            database.ChangeTracker.Clear();
            var prior = await database.StockExitOrders.AsNoTracking()
                .SingleOrDefaultAsync(order => order.AuthorizedBy == userId && order.IdempotencyKey == idempotencyKey, cancellationToken);
            if (prior is not null)
            {
                if (prior.IdempotencyRequestHash != requestHash)
                    throw new InventoryFailureException(409, "IDEMPOTENCY_CONFLICT", "A chave de idempotência já foi usada com dados diferentes.");
                var priorStock = await database.Stocks.AsNoTracking()
                    .SingleAsync(stock => stock.ItemId == prior.ItemId, cancellationToken);
                return new ExitAuthorizationResult(prior, priorStock.OnHand, priorStock.Reserved, true);
            }
            throw;
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    public async Task<(StockExitOrder Order, int OnHand, int Reserved, bool Replayed)> ConfirmAsync(
        Guid orderId, Guid userId, string userName, string idempotencyKey, CancellationToken cancellationToken)
    {
        idempotencyKey = idempotencyKey.Trim();
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { orderId }))));
        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational())
            transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            StockExitOrder? order;
            if (database.Database.ProviderName == PostgresProvider)
                order = await database.StockExitOrders.FromSqlInterpolated(
                    $"SELECT * FROM \"StockExitOrders\" WHERE \"Id\" = {orderId} FOR UPDATE")
                    .SingleOrDefaultAsync(cancellationToken);
            else
                order = await database.StockExitOrders.SingleOrDefaultAsync(value => value.Id == orderId, cancellationToken);
            if (order is null)
                throw new InventoryFailureException(404, "EXIT_ORDER_NOT_FOUND", "Saída autorizada não encontrada.");
            await EnsureViewerScopeAsync(order, userId, cancellationToken);

            var priorMovement = await database.StockMovements.AsNoTracking()
                .SingleOrDefaultAsync(value => value.IdempotencyKey == idempotencyKey, cancellationToken);
            if (priorMovement is not null)
            {
                if (order.Status == "confirmada" && order.ConfirmedBy == userId
                    && order.MovementId == priorMovement.Id
                    && priorMovement.IdempotencyRequestHash == requestHash)
                {
                    var priorStock = await database.Stocks.AsNoTracking()
                        .SingleAsync(value => value.ItemId == order.ItemId, cancellationToken);
                    if (transaction is not null)
                        await transaction.CommitAsync(cancellationToken);
                    return (order, priorStock.OnHand, priorStock.Reserved, true);
                }
                throw new InventoryFailureException(409, "IDEMPOTENCY_CONFLICT", "A chave de idempotência já foi usada com dados diferentes.");
            }

            if (order.Status != "autorizada")
                throw new InventoryFailureException(409, "EXIT_NOT_PENDING", "Esta saída já foi confirmada ou cancelada.");

            InventoryStock? stock;
            if (database.Database.ProviderName == PostgresProvider)
                stock = await database.Stocks.FromSqlInterpolated(
                    $"SELECT * FROM \"Stock\" WHERE \"ItemId\" = {order.ItemId} FOR UPDATE")
                    .SingleOrDefaultAsync(cancellationToken);
            else
                stock = await database.Stocks.SingleOrDefaultAsync(value => value.ItemId == order.ItemId, cancellationToken);
            if (stock is null || stock.Reserved < order.Quantity || stock.OnHand < order.Quantity)
                throw new InventoryFailureException(422, "RESERVATION_INVALID", "Quantidade entregue maior que a reservada.");

            var item = await database.CatalogItems.AsNoTracking()
                .SingleOrDefaultAsync(value => value.Id == order.ItemId, cancellationToken);
            if (item is null)
                throw new InventoryFailureException(404, "ITEM_NOT_FOUND", "Brinde não encontrado.");

            var now = DateTimeOffset.UtcNow;
            var beforeOnHand = stock.OnHand;
            var locationId = await StockPositionService.ResolveLocationIdAsync(database, item.LocationId, cancellationToken);
            await StockPositionService.ApplyDeltaAsync(database, item.Id, locationId, -order.Quantity, cancellationToken);
            stock.OnHand = checked(stock.OnHand - order.Quantity);
            stock.Reserved = checked(stock.Reserved - order.Quantity);
            stock.UpdatedAt = now;
            var movement = new MovementEntity
            {
                ItemId = order.ItemId,
                UserId = userId,
                Type = "saida",
                Quantity = -order.Quantity,
                BalanceAfter = stock.OnHand,
                LocationId = locationId,
                IndustryId = order.IndustryId,
                DepartmentId = order.DepartmentId,
                Notes = order.Notes,
                DocumentRef = order.DocumentRef,
                Purpose = order.Purpose,
                Recipient = order.Recipient,
                PurchaseTicketNo = order.PurchaseTicketNo,
                UnitValue = item.UnitValue,
                AttachmentPath = order.AttachmentPath,
                IdempotencyKey = idempotencyKey,
                IdempotencyRequestHash = requestHash,
                CreatedAt = now
            };
            order.Status = "confirmada";
            order.ConfirmedBy = userId;
            order.ConfirmedAt = now;
            order.MovementId = movement.Id;
            order.UpdatedAt = now;
            database.StockMovements.Add(movement);
            database.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                UserName = userName,
                Action = "stock_saida",
                EntityType = "item",
                EntityId = item.Id,
                EntityLabel = item.Code,
                BeforeJson = JsonSerializer.Serialize(new { on_hand = beforeOnHand }),
                AfterJson = JsonSerializer.Serialize(new
                {
                    on_hand = stock.OnHand,
                    quantity = -order.Quantity,
                    movement_id = movement.Id
                }),
                CreatedAt = now
            });
            database.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                UserName = userName,
                Action = "stock_exit_confirm",
                EntityType = "stock_exit_order",
                EntityId = order.Id,
                EntityLabel = order.Code,
                BeforeJson = JsonSerializer.Serialize(new { status = "autorizada" }),
                AfterJson = JsonSerializer.Serialize(new { status = "confirmada", movement_id = movement.Id }),
                CreatedAt = now
            });
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return (order, stock.OnHand, stock.Reserved, false);
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    public async Task<ExitAuthorizationResult> CancelAsync(Guid orderId, string? reason, Guid userId, string userName, CancellationToken cancellationToken)
    {
        reason = Normalize(reason);
        if (reason?.Length > 255)
            throw new InventoryFailureException(422, "VALIDATION_ERROR", "O motivo do cancelamento deve ter no máximo 255 caracteres.");

        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational())
            transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            StockExitOrder? order;
            if (database.Database.ProviderName == PostgresProvider)
                order = await database.StockExitOrders.FromSqlInterpolated(
                    $"SELECT * FROM \"StockExitOrders\" WHERE \"Id\" = {orderId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
            else
                order = await database.StockExitOrders.SingleOrDefaultAsync(value => value.Id == orderId, cancellationToken);
            if (order is null)
                throw new InventoryFailureException(404, "EXIT_ORDER_NOT_FOUND", "Saída autorizada não encontrada.");
            await EnsureViewerScopeAsync(order, userId, cancellationToken);
            if (order.Status != "autorizada")
                throw new InventoryFailureException(409, "EXIT_NOT_PENDING", "Só é possível cancelar uma saída ainda não confirmada.");

            InventoryStock? stock;
            if (database.Database.ProviderName == PostgresProvider)
                stock = await database.Stocks.FromSqlInterpolated(
                    $"SELECT * FROM \"Stock\" WHERE \"ItemId\" = {order.ItemId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
            else
                stock = await database.Stocks.SingleOrDefaultAsync(value => value.ItemId == order.ItemId, cancellationToken);
            if (stock is null || stock.Reserved < order.Quantity)
                throw new InventoryFailureException(422, "RESERVATION_INVALID", "Reserva inválida para cancelar a saída.");

            var now = DateTimeOffset.UtcNow;
            stock.Reserved -= order.Quantity;
            stock.UpdatedAt = now;
            order.Status = "cancelada";
            order.Notes = string.Join("\n", new[] { order.Notes, reason is null ? null : $"Cancelada: {reason}" }.Where(value => !string.IsNullOrWhiteSpace(value)));
            order.UpdatedAt = now;
            database.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                UserName = userName,
                Action = "stock_exit_cancel",
                EntityType = "stock_exit_order",
                EntityId = order.Id,
                EntityLabel = order.Code,
                BeforeJson = JsonSerializer.Serialize(new { status = "autorizada", quantity = order.Quantity }),
                AfterJson = JsonSerializer.Serialize(new { status = "cancelada", quantity = order.Quantity, reason }),
                CreatedAt = now
            });
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return new ExitAuthorizationResult(order, stock.OnHand, stock.Reserved, false);
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private async Task<int> NextSequenceAsync(string name, string period, CancellationToken cancellationToken)
    {
        if (database.Database.ProviderName == PostgresProvider)
        {
            var connection = database.Database.GetDbConnection();
            var closeConnection = connection.State != ConnectionState.Open;
            if (closeConnection)
                await database.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await using var command = connection.CreateCommand();
                command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
                command.CommandText = """
                    INSERT INTO "Sequences" ("Name", "Period", "LastValue")
                    VALUES (@name, @period, 1)
                    ON CONFLICT ("Name", "Period")
                    DO UPDATE SET "LastValue" = "Sequences"."LastValue" + 1
                    RETURNING "LastValue"
                    """;
                var nameParameter = command.CreateParameter();
                nameParameter.ParameterName = "name";
                nameParameter.Value = name;
                command.Parameters.Add(nameParameter);
                var periodParameter = command.CreateParameter();
                periodParameter.ParameterName = "period";
                periodParameter.Value = period;
                command.Parameters.Add(periodParameter);
                var value = await command.ExecuteScalarAsync(cancellationToken);
                return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            finally
            {
                if (closeConnection)
                    await database.Database.CloseConnectionAsync();
            }
        }

        var existing = await database.SequenceCounters.SingleOrDefaultAsync(
            counter => counter.Name == name && counter.Period == period, cancellationToken);
        if (existing is not null)
        {
            existing.LastValue = checked(existing.LastValue + 1);
            return existing.LastValue;
        }

        database.SequenceCounters.Add(new SequenceCounter { Name = name, Period = period, LastValue = 1 });
        return 1;
    }

    private async Task EnsureViewerScopeAsync(StockExitOrder order, Guid userId, CancellationToken cancellationToken)
    {
        var scope = await database.Users.AsNoTracking().Where(user => user.Id == userId)
            .Select(user => new { user.IndustryId, user.DepartmentId })
            .SingleOrDefaultAsync(cancellationToken);
        if (scope?.IndustryId is Guid industryId && order.IndustryId != industryId)
            throw new InventoryFailureException(404, "EXIT_ORDER_NOT_FOUND", "Saída autorizada não encontrada.");
        if (scope?.DepartmentId is Guid departmentId && order.DepartmentId != departmentId)
            throw new InventoryFailureException(404, "EXIT_ORDER_NOT_FOUND", "Saída autorizada não encontrada.");
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ComputeRequestHash(Guid itemId, int quantity, string purpose, string? recipient,
        Guid? industryId, Guid? departmentId, Guid? requesterId, string? purchaseTicketNo,
        string? documentRef, string? notes, string? attachmentSha256)
    {
        var payload = attachmentSha256 is null
            ? JsonSerializer.Serialize(new
        {
            itemId,
            quantity,
            purpose,
            recipient,
            industryId,
            departmentId,
            requesterId,
            purchaseTicketNo,
            documentRef,
            notes
        })
            : JsonSerializer.Serialize(new
            {
                itemId,
                quantity,
                purpose,
                recipient,
                industryId,
                departmentId,
                requesterId,
                purchaseTicketNo,
                documentRef,
                notes,
                attachmentSha256
            });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
