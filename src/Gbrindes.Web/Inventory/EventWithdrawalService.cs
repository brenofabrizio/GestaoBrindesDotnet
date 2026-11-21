using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gbrindes.Web.Data;
using Gbrindes.Web.Inventory;
using Gbrindes.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MovementEntity = Gbrindes.Web.Data.StockMovement;

namespace Gbrindes.Web.Inventory;

public sealed class EventWithdrawalFailureException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed record EventWithdrawalLine(Guid ItemId, int Quantity);

public sealed class EventWithdrawalService(ApplicationDbContext database, DigitalSignatureStorage signatures)
{
    private const string PostgresProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public async Task<(Delivery Delivery, bool Replayed)> WithdrawAsync(Guid eventId, Guid userId, Guid industryId,
        string receivedByName, string? receivedByEmail, string? receivedByDocument, string? notes, string signature,
        IReadOnlyCollection<EventWithdrawalLine> lines, string idempotencyKey, string? userName, string? ip, CancellationToken ct)
    {
        receivedByName = receivedByName.Trim();
        receivedByEmail = string.IsNullOrWhiteSpace(receivedByEmail) ? null : receivedByEmail.Trim();
        receivedByDocument = string.IsNullOrWhiteSpace(receivedByDocument) ? null : receivedByDocument.Trim();
        notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (industryId == Guid.Empty || receivedByName.Length is < 1 or > 150 || receivedByEmail?.Length > 190
            || (receivedByEmail is not null && !System.Net.Mail.MailAddress.TryCreate(receivedByEmail, out _))
            || receivedByDocument?.Length > 30 || notes?.Length > 500 || string.IsNullOrWhiteSpace(signature)
            || lines.Count == 0 || lines.Count > 100 || lines.Any(line => line.ItemId == Guid.Empty || line.Quantity is < 1 or > 1_000_000))
            throw new EventWithdrawalFailureException(422, "VALIDATION_ERROR", "Dados de retirada inválidos.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(idempotencyKey, "^[A-Za-z0-9_-]{8,80}$"))
            throw new EventWithdrawalFailureException(400, "IDEMPOTENCY_KEY_REQUIRED", "Cabeçalho Idempotency-Key ausente ou inválido.");

        var quantities = lines.GroupBy(line => line.ItemId)
            .Select(group => new EventWithdrawalLine(group.Key, group.Aggregate(0, (total, line) => checked(total + line.Quantity))))
            .OrderBy(line => line.ItemId).ToArray();
        var keyHash = Hash($"event:{eventId:N}:{industryId:N}:{idempotencyKey}");
        var signatureHash = Hash(signature);
        var payloadHash = Hash(JsonSerializer.Serialize(new
        {
            eventId,
            industryId,
            receivedByName,
            receivedByEmail,
            receivedByDocument,
            notes,
            signatureHash,
            items = quantities
        }));
        await using IDbContextTransaction? transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct) : null;
        string? storedSignature = null;
        try
        {
            Event? eventRecord;
            if (database.Database.ProviderName == PostgresProvider)
                eventRecord = await database.Events.FromSqlInterpolated($"SELECT * FROM \"Events\" WHERE \"Id\" = {eventId} AND \"DeletedAt\" IS NULL FOR UPDATE").SingleOrDefaultAsync(ct);
            else
                eventRecord = await database.Events.SingleOrDefaultAsync(value => value.Id == eventId && value.DeletedAt == null, ct);
            if (eventRecord is null)
                throw new EventWithdrawalFailureException(404, "NOT_FOUND", "Evento não encontrado.");

            var prior = await database.Deliveries.Include(value => value.Items)
                .SingleOrDefaultAsync(value => value.IdempotencyKeyHash == keyHash, ct);
            if (prior is not null)
            {
                if (prior.RequestHash != payloadHash)
                    throw new EventWithdrawalFailureException(409, "IDEMPOTENCY_CONFLICT", "Chave já utilizada com dados diferentes.");
                if (transaction is not null) await transaction.CommitAsync(ct);
                return (prior, true);
            }
            if (eventRecord.Status != "aberto")
                throw new EventWithdrawalFailureException(409, "EVENT_NOT_OPEN", "O evento não está aberto para retiradas.");

            var allocations = await database.EventAllocations.Where(value => value.EventId == eventId && value.IndustryId == industryId).ToListAsync(ct);
            var byItem = allocations.ToDictionary(value => value.ItemId);
            foreach (var line in quantities)
            {
                if (!byItem.TryGetValue(line.ItemId, out var allocation))
                    throw new EventWithdrawalFailureException(422, "ALLOCATION_EXCEEDED", "Este brinde não está na cota desta indústria.");
                var available = allocation.QuantityAllocated - allocation.QuantityWithdrawn;
                if (line.Quantity > available)
                    throw new EventWithdrawalFailureException(422, "ALLOCATION_EXCEEDED", $"Cota insuficiente: saldo {available}, solicitado {line.Quantity}.");
            }

            var now = DateTimeOffset.UtcNow;
            var signatureFile = signatures.Store(eventId, signature);
            storedSignature = signatureFile.Path;
            var sequence = await NextSequenceAsync(now.Year.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
            var delivery = new Delivery
            {
                Code = $"PROT-{now:yyyy}-{sequence:D4}",
                Type = "evento",
                EventId = eventId,
                IndustryId = industryId,
                DeliveredById = userId,
                ReceivedByName = receivedByName,
                ReceivedByEmail = receivedByEmail,
                ReceivedByDocument = receivedByDocument,
                SignaturePath = storedSignature,
                Notes = notes,
                IdempotencyKeyHash = keyHash,
                RequestHash = payloadHash,
                CreatedAt = now
            };
            database.Deliveries.Add(delivery);
            foreach (var line in quantities)
            {
                var allocation = byItem[line.ItemId];
                var stock = await LockStockAsync(line.ItemId, ct);
                if (stock is null || stock.Reserved < line.Quantity || stock.OnHand < line.Quantity)
                    throw new EventWithdrawalFailureException(422, "RESERVATION_INVALID", "Estoque reservado insuficiente para a retirada.");
                var item = await database.CatalogItems.SingleAsync(value => value.Id == line.ItemId, ct);
                var locationId = await StockPositionService.ResolveLocationIdAsync(database, eventRecord.LocationId ?? item.LocationId, ct);
                await StockPositionService.ApplyDeltaAsync(database, line.ItemId, locationId, -line.Quantity, ct);
                stock.OnHand -= line.Quantity;
                stock.Reserved -= line.Quantity;
                stock.UpdatedAt = now;
                allocation.QuantityWithdrawn = checked(allocation.QuantityWithdrawn + line.Quantity);
                allocation.UpdatedAt = now;
                database.StockMovements.Add(new MovementEntity
                {
                    ItemId = line.ItemId,
                    UserId = userId,
                    IndustryId = industryId,
                    DeliveryId = delivery.Id,
                    Type = "saida",
                    Quantity = -line.Quantity,
                    BalanceAfter = stock.OnHand,
                    Reason = "Retirada de evento",
                    Purpose = $"Evento: {eventRecord.Name}",
                    Recipient = receivedByName,
                    LocationId = locationId,
                    CreatedAt = now
                });
                database.DeliveryItems.Add(new DeliveryItem
                {
                    Delivery = delivery,
                    ItemId = line.ItemId,
                    Quantity = line.Quantity,
                    BalanceAfter = allocation.QuantityAllocated - allocation.QuantityWithdrawn
                });
            }
            database.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                UserName = userName,
                Action = "withdraw",
                EntityType = "delivery",
                EntityId = delivery.Id,
                EntityLabel = delivery.Code,
                BeforeJson = JsonSerializer.Serialize(new { status = eventRecord.Status }),
                AfterJson = JsonSerializer.Serialize(new { code = delivery.Code, event_id = eventId, industry_id = industryId, items = quantities }),
                Ip = ip,
                CreatedAt = now
            });
            await database.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return (delivery, false);
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(ct);
            if (storedSignature is not null) signatures.Delete(storedSignature);
            throw;
        }
    }

    private async Task<int> NextSequenceAsync(string period, CancellationToken ct)
    {
        if (database.Database.ProviderName == PostgresProvider)
        {
            var connection = database.Database.GetDbConnection();
            var close = connection.State != ConnectionState.Open;
            if (close) await database.Database.OpenConnectionAsync(ct);
            try
            {
                await using var command = connection.CreateCommand();
                command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
                command.CommandText = "INSERT INTO \"Sequences\" (\"Name\", \"Period\", \"LastValue\") VALUES ('protocol', @period, 1) ON CONFLICT (\"Name\", \"Period\") DO UPDATE SET \"LastValue\" = \"Sequences\".\"LastValue\" + 1 RETURNING \"LastValue\"";
                var parameter = command.CreateParameter(); parameter.ParameterName = "period"; parameter.Value = period; command.Parameters.Add(parameter);
                return Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
            }
            finally { if (close) await database.Database.CloseConnectionAsync(); }
        }
        var counter = await database.SequenceCounters.SingleOrDefaultAsync(value => value.Name == "protocol" && value.Period == period, ct);
        if (counter is null) { database.SequenceCounters.Add(new SequenceCounter { Name = "protocol", Period = period, LastValue = 1 }); return 1; }
        return counter.LastValue = checked(counter.LastValue + 1);
    }

    private async Task<InventoryStock?> LockStockAsync(Guid itemId, CancellationToken ct) =>
        database.Database.ProviderName == PostgresProvider
            ? await database.Stocks.FromSqlInterpolated($"SELECT * FROM \"Stock\" WHERE \"ItemId\" = {itemId} FOR UPDATE").SingleOrDefaultAsync(ct)
            : await database.Stocks.SingleOrDefaultAsync(value => value.ItemId == itemId, ct);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
