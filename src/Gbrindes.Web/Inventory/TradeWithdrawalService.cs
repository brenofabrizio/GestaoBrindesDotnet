using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gbrindes.Web.Data;
using Gbrindes.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Gbrindes.Web.Inventory;

public sealed class TradeWithdrawalFailureException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed record TradeWithdrawalLine(Guid ItemId, int Quantity);
public sealed record TradeWithdrawalResult(Delivery Delivery, string RequestStatus, bool Replayed);

public sealed class TradeWithdrawalService(
    ApplicationDbContext database,
    InventoryService inventory,
    DigitalSignatureStorage signatures)
{
    private const string PostgresProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public async Task<TradeWithdrawalResult> WithdrawAsync(Guid requestId, string? receivedByName,
        string? receivedByEmail, string? receivedByDocument, string? receivedByPhone, string? recipient,
        string? signature, string? notes, IReadOnlyCollection<TradeWithdrawalLine>? lines, Guid userId,
        string userName, string? ip, string idempotencyKey, CancellationToken ct)
    {
        receivedByName = Normalize(receivedByName);
        receivedByEmail = Normalize(receivedByEmail);
        receivedByDocument = Normalize(receivedByDocument);
        receivedByPhone = Normalize(receivedByPhone);
        recipient = Normalize(recipient);
        notes = Normalize(notes);
        if (string.IsNullOrWhiteSpace(receivedByName) || receivedByName.Length > 150)
            throw new TradeWithdrawalFailureException(422, "VALIDATION_ERROR", "Informe o nome de quem está retirando, com até 150 caracteres.");
        if (receivedByEmail is not null && (receivedByEmail.Length > 190 || !System.Net.Mail.MailAddress.TryCreate(receivedByEmail, out _)))
            throw new TradeWithdrawalFailureException(422, "VALIDATION_ERROR", "E-mail de quem está retirando inválido.");
        if (receivedByDocument?.Length > 30 || receivedByPhone?.Length > 30 || recipient?.Length > 150 || notes?.Length > 1000)
            throw new TradeWithdrawalFailureException(422, "VALIDATION_ERROR", "Um ou mais campos excedem o tamanho permitido.");
        if (string.IsNullOrWhiteSpace(signature))
            throw new TradeWithdrawalFailureException(422, "VALIDATION_ERROR", "A assinatura de quem está retirando é obrigatória.");
        if (lines is null or { Count: 0 } || lines.Count > 100
            || lines.Any(line => line.ItemId == Guid.Empty || line.Quantity is < 1 or > 1_000_000))
            throw new TradeWithdrawalFailureException(422, "VALIDATION_ERROR", "Informe itens e quantidades válidos para retirada.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(idempotencyKey, "^[A-Za-z0-9_-]{8,80}$"))
            throw new TradeWithdrawalFailureException(400, "IDEMPOTENCY_KEY_REQUIRED", "Cabeçalho Idempotency-Key ausente ou inválido.");

        var quantities = lines.GroupBy(line => line.ItemId)
            .Select(group => new TradeWithdrawalLine(group.Key, group.Aggregate(0, (total, line) => checked(total + line.Quantity))))
            .OrderBy(line => line.ItemId).ToArray();
        var keyHash = Hash($"{requestId:N}:{idempotencyKey}");
        var signatureHash = Hash(signature);
        var requestHash = Hash(JsonSerializer.Serialize(new
        {
            receivedByName,
            receivedByEmail,
            receivedByDocument,
            receivedByPhone,
            recipient,
            notes,
            signatureHash,
            items = quantities
        }));

        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational())
            transaction = await database.Database.BeginTransactionAsync(ct);
        string? storedSignaturePath = null;
        try
        {
            InternalRequest? request;
            if (database.Database.ProviderName == PostgresProvider)
            {
                request = await database.Requests.FromSqlInterpolated(
                    $"SELECT * FROM \"Requests\" WHERE \"Id\" = {requestId} AND \"DeletedAt\" IS NULL AND \"Flow\" = 'trade' FOR UPDATE")
                    .SingleOrDefaultAsync(ct);
            }
            else
            {
                request = await database.Requests.SingleOrDefaultAsync(value => value.Id == requestId
                    && value.DeletedAt == null && value.Flow == "trade", ct);
            }
            if (request is null)
                throw new TradeWithdrawalFailureException(404, "NOT_FOUND", "Solicitação TRADE não encontrada.");

            var actorScope = await database.Users.AsNoTracking().Where(user => user.Id == userId)
                .Select(user => new { user.IndustryId, user.DepartmentId }).SingleOrDefaultAsync(ct);
            if (actorScope?.IndustryId is Guid industryId && request.IndustryId != industryId)
                throw new TradeWithdrawalFailureException(403, "INDUSTRY_FORBIDDEN", "Você só pode registrar retirada de solicitações da sua indústria.");
            if (actorScope?.DepartmentId is Guid departmentId && request.DepartmentId != departmentId)
                throw new TradeWithdrawalFailureException(404, "NOT_FOUND", "Solicitação TRADE não encontrada.");

            var prior = await database.Deliveries.Include(delivery => delivery.Items)
                .SingleOrDefaultAsync(delivery => delivery.IdempotencyKeyHash == keyHash, ct);
            if (prior is not null)
            {
                if (prior.RequestHash != requestHash)
                    throw new TradeWithdrawalFailureException(409, "IDEMPOTENCY_CONFLICT", "Chave já utilizada com dados diferentes.");
                if (transaction is not null)
                    await transaction.CommitAsync(ct);
                return new TradeWithdrawalResult(prior, request.Status, true);
            }

            if (request.Status is not ("pronta" or "recebido_cd" or "retirado"))
                throw new TradeWithdrawalFailureException(409, "INVALID_TRANSITION", "A retirada só pode ser registrada quando os brindes estiverem prontos para retirada.");

            await database.Entry(request).Collection(value => value.Items).LoadAsync(ct);
            var requestItems = request.Items.ToDictionary(item => item.ItemId);
            foreach (var line in quantities)
            {
                if (!requestItems.TryGetValue(line.ItemId, out var requestItem))
                    throw new TradeWithdrawalFailureException(422, "ITEM_NOT_IN_REQUEST", "O brinde não pertence à solicitação TRADE.");
                var eligible = requestItem.QuantityReceived > 0 ? requestItem.QuantityReceived : requestItem.QuantityRequested;
                var availableForRequest = Math.Max(0, eligible - requestItem.QuantityDelivered);
                if (line.Quantity > availableForRequest)
                    throw new TradeWithdrawalFailureException(422, "STOCK_INSUFFICIENT",
                        $"Não é possível realizar a retirada. Saldo disponível nesta solicitação: {availableForRequest} unidade(s).");
            }

            var signatureFile = signatures.Store(requestId, signature);
            storedSignaturePath = signatureFile.Path;
            var now = DateTimeOffset.UtcNow;
            var originalStatus = request.Status;
            var code = $"PROT-{now:yyyy}-{await NextSequenceAsync("protocol", now.Year.ToString(System.Globalization.CultureInfo.InvariantCulture), ct):D4}";
            var delivery = new Delivery
            {
                Code = code,
                Type = "dia_a_dia",
                RequestId = requestId,
                IndustryId = request.IndustryId,
                DeliveredById = userId,
                ReceivedByName = receivedByName,
                ReceivedByEmail = receivedByEmail,
                ReceivedByDocument = receivedByDocument,
                ReceivedByPhone = receivedByPhone,
                SignaturePath = signatureFile.Path,
                Notes = notes,
                IdempotencyKeyHash = keyHash,
                RequestHash = requestHash,
                CreatedAt = now
            };
            database.Deliveries.Add(delivery);
            await database.SaveChangesAsync(ct);

            foreach (var line in quantities)
            {
                var movementKey = Hash($"trade-withdraw:{keyHash}:{delivery.Id:N}:{line.ItemId:N}");
                var movement = await inventory.MoveAsync(line.ItemId, "saida", line.Quantity, userId,
                    "Retirada TRADE", notes, request.InvoiceNo, request.Purpose,
                    recipient ?? receivedByName, request.PurchaseTicketNo, null, movementKey, ct,
                    request.IndustryId, delivery.Id);
                requestItems[line.ItemId].QuantityDelivered = checked(requestItems[line.ItemId].QuantityDelivered + line.Quantity);
                database.DeliveryItems.Add(new DeliveryItem
                {
                    DeliveryId = delivery.Id,
                    Delivery = delivery,
                    ItemId = line.ItemId,
                    Quantity = line.Quantity,
                    BalanceAfter = movement.BalanceAfter
                });
            }

            var statusTime = now;
            if (request.Status == "recebido_cd")
            {
                AddStatus(request, "pronta", userId, "Retirada registrada.", statusTime);
                request.Status = "pronta";
                statusTime = statusTime.AddTicks(1);
            }
            if (request.Status == "pronta")
            {
                AddStatus(request, "retirado", userId, $"Protocolo {code}", statusTime);
                request.Status = "retirado";
                statusTime = statusTime.AddTicks(1);
            }

            var remaining = request.Items.Sum(item => Math.Max(0,
                (item.QuantityReceived > 0 ? item.QuantityReceived : item.QuantityRequested) - item.QuantityDelivered));
            if (remaining == 0 && request.Status == "retirado")
            {
                AddStatus(request, "entregue", userId, "Saldo da solicitação zerado.", statusTime);
                request.Status = "entregue";
            }
            request.StatusChangedAt = statusTime;
            request.UpdatedAt = statusTime;
            database.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                UserName = userName,
                Action = "withdraw",
                EntityType = "delivery",
                EntityId = delivery.Id,
                EntityLabel = delivery.Code,
                BeforeJson = JsonSerializer.Serialize(new { request_id = requestId, status = originalStatus }),
                AfterJson = JsonSerializer.Serialize(new { request_id = requestId, status = request.Status, code, items = quantities }),
                Ip = ip,
                CreatedAt = now
            });
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return new TradeWithdrawalResult(delivery, request.Status, false);
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(ct);
            if (storedSignaturePath is not null)
                signatures.Delete(storedSignaturePath);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private async Task<int> NextSequenceAsync(string name, string period, CancellationToken ct)
    {
        if (database.Database.ProviderName == PostgresProvider)
        {
            var connection = database.Database.GetDbConnection();
            var closeConnection = connection.State != ConnectionState.Open;
            if (closeConnection)
                await database.Database.OpenConnectionAsync(ct);
            try
            {
                await using var command = connection.CreateCommand();
                command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
                command.CommandText = """
                    INSERT INTO "Sequences" ("Name", "Period", "LastValue") VALUES (@name, @period, 1)
                    ON CONFLICT ("Name", "Period") DO UPDATE SET "LastValue" = "Sequences"."LastValue" + 1
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
                return Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
            }
            finally
            {
                if (closeConnection)
                    await database.Database.CloseConnectionAsync();
            }
        }

        var existing = await database.SequenceCounters.SingleOrDefaultAsync(
            counter => counter.Name == name && counter.Period == period, ct);
        if (existing is null)
        {
            database.SequenceCounters.Add(new SequenceCounter { Name = name, Period = period, LastValue = 1 });
            return 1;
        }
        existing.LastValue = checked(existing.LastValue + 1);
        return existing.LastValue;
    }

    private void AddStatus(InternalRequest request, string toStatus, Guid userId, string comment, DateTimeOffset createdAt)
    {
        database.RequestStatusHistory.Add(new InternalRequestStatusHistory
        {
            RequestId = request.Id,
            FromStatus = request.Status,
            ToStatus = toStatus,
            UserId = userId,
            Comment = comment,
            CreatedAt = createdAt
        });
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
