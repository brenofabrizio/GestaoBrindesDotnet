using System.Data;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gbrindes.Web.Data;
using Gbrindes.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MovementEntity = Gbrindes.Web.Data.StockMovement;

namespace Gbrindes.Web.Inventory;

public sealed class InternalRequestDeliveryFailureException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed record InternalRequestDeliveryResult(Delivery Delivery, string RequestStatus, bool Replayed);

public sealed class InternalRequestDeliveryService(
    ApplicationDbContext database,
    DigitalSignatureStorage signatures)
{
    private const string PostgresProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public async Task<InternalRequestDeliveryResult> DeliverAsync(
        Guid requestId,
        string? receivedByName,
        string? receivedByEmail,
        string? receivedByDocument,
        string? receivedByPhone,
        string? signature,
        string? notes,
        Guid userId,
        string userName,
        string? ip,
        string idempotencyKey,
        CancellationToken ct)
    {
        receivedByName = Normalize(receivedByName);
        receivedByEmail = Normalize(receivedByEmail);
        receivedByDocument = Normalize(receivedByDocument);
        receivedByPhone = Normalize(receivedByPhone);
        notes = Normalize(notes);
        if (string.IsNullOrWhiteSpace(receivedByName) || receivedByName.Length > 150)
            throw new InternalRequestDeliveryFailureException(422, "VALIDATION_ERROR", "Informe o nome de quem recebeu, com até 150 caracteres.");
        if (receivedByEmail is not null && (receivedByEmail.Length > 190 || !MailAddress.TryCreate(receivedByEmail, out _)))
            throw new InternalRequestDeliveryFailureException(422, "VALIDATION_ERROR", "E-mail de quem recebeu inválido.");
        if (receivedByDocument?.Length > 30 || receivedByPhone?.Length > 30 || notes?.Length > 1000)
            throw new InternalRequestDeliveryFailureException(422, "VALIDATION_ERROR", "Um ou mais campos excedem o tamanho permitido.");
        if (string.IsNullOrWhiteSpace(signature))
            throw new InternalRequestDeliveryFailureException(422, "VALIDATION_ERROR", "A assinatura de quem recebeu é obrigatória.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(idempotencyKey, "^[A-Za-z0-9_-]{8,80}$"))
            throw new InternalRequestDeliveryFailureException(400, "IDEMPOTENCY_KEY_REQUIRED", "Cabeçalho Idempotency-Key ausente ou inválido.");

        var keyHash = Hash($"{requestId:N}:{idempotencyKey}");
        var requestHash = Hash(JsonSerializer.Serialize(new
        {
            receivedByName,
            receivedByEmail,
            receivedByDocument,
            receivedByPhone,
            notes,
            signatureHash = Hash(signature)
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
                    $"SELECT * FROM \"Requests\" WHERE \"Id\" = {requestId} AND \"DeletedAt\" IS NULL AND \"Flow\" = 'interna' FOR UPDATE")
                    .SingleOrDefaultAsync(ct);
                if (request is not null)
                    await database.Entry(request).Collection(value => value.Items).Query().Include(value => value.Item).LoadAsync(ct);
            }
            else
            {
                request = await database.Requests
                    .Include(value => value.Items).ThenInclude(value => value.Item)
                    .SingleOrDefaultAsync(value => value.Id == requestId && value.DeletedAt == null && value.Flow == "interna", ct);
            }
            if (request is null)
                throw new InternalRequestDeliveryFailureException(404, "NOT_FOUND", "Solicitação interna não encontrada.");

            var actorScope = await database.Users.AsNoTracking().Where(value => value.Id == userId)
                .Select(value => new { value.IndustryId, value.DepartmentId }).SingleOrDefaultAsync(ct);
            if (actorScope?.IndustryId is Guid industryId && request.IndustryId != industryId)
                throw new InternalRequestDeliveryFailureException(403, "INDUSTRY_FORBIDDEN", "Você só pode entregar solicitações da sua indústria.");
            if (actorScope?.DepartmentId is Guid departmentId && request.DepartmentId != departmentId)
                throw new InternalRequestDeliveryFailureException(404, "NOT_FOUND", "Solicitação interna não encontrada.");

            var prior = await database.Deliveries.Include(value => value.Items)
                .SingleOrDefaultAsync(value => value.IdempotencyKeyHash == keyHash, ct);
            if (prior is not null)
            {
                if (prior.RequestHash != requestHash)
                    throw new InternalRequestDeliveryFailureException(409, "IDEMPOTENCY_CONFLICT", "Chave já utilizada com dados diferentes.");
                if (transaction is not null)
                    await transaction.CommitAsync(ct);
                return new InternalRequestDeliveryResult(prior, request.Status, true);
            }

            var existing = await database.Deliveries.AsNoTracking()
                .AnyAsync(value => value.RequestId == requestId, ct);
            if (existing)
                throw new InternalRequestDeliveryFailureException(409, "DUPLICATE_DELIVERY", "Esta solicitação já possui um protocolo de entrega.");
            if (request.Status != "pronta")
                throw new InternalRequestDeliveryFailureException(409, "INVALID_TRANSITION", "A entrega só pode ser registrada quando a solicitação está pronta para retirada.");

            foreach (var line in request.Items.OrderBy(value => value.ItemId))
            {
                var quantity = line.QuantityReserved - line.QuantityDelivered;
                if (quantity <= 0)
                    continue;
                if (database.Database.ProviderName == PostgresProvider)
                    await database.CatalogItems.FromSqlInterpolated(
                        $"SELECT * FROM \"CatalogItems\" WHERE \"Id\" = {line.ItemId} AND \"DeletedAt\" IS NULL FOR UPDATE")
                        .SingleOrDefaultAsync(ct);
            }

            var signatureFile = signatures.Store(requestId, signature);
            storedSignaturePath = signatureFile.Path;
            var now = DateTimeOffset.UtcNow;
            var code = $"PROT-{now:yyyy}-{await NextSequenceAsync(now.Year.ToString(System.Globalization.CultureInfo.InvariantCulture), ct):D4}";
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

            foreach (var line in request.Items.OrderBy(value => value.ItemId))
            {
                var quantity = line.QuantityReserved - line.QuantityDelivered;
                if (quantity <= 0)
                    continue;
                var stock = database.Database.ProviderName == PostgresProvider
                    ? await database.Stocks.FromSqlInterpolated(
                        $"SELECT * FROM \"Stock\" WHERE \"ItemId\" = {line.ItemId} FOR UPDATE").SingleOrDefaultAsync(ct)
                    : await database.Stocks.SingleOrDefaultAsync(value => value.ItemId == line.ItemId, ct);
                if (stock is null || stock.OnHand < quantity || stock.Reserved < quantity)
                    throw new InternalRequestDeliveryFailureException(409, "RESERVATION_MISMATCH", "O estoque reservado não corresponde à quantidade pendente da solicitação.");

                var item = line.Item;
                var locationId = await StockPositionService.ResolveLocationIdAsync(database, item.LocationId, ct);
                await StockPositionService.ApplyDeltaAsync(database, line.ItemId, locationId, -quantity, ct);
                stock.OnHand = checked(stock.OnHand - quantity);
                stock.Reserved = checked(stock.Reserved - quantity);
                stock.UpdatedAt = now;
                var movementKey = Hash($"internal-delivery:{keyHash}:{delivery.Id:N}:{line.ItemId:N}");
                database.StockMovements.Add(new MovementEntity
                {
                    ItemId = line.ItemId,
                    UserId = userId,
                    IndustryId = request.IndustryId,
                    DepartmentId = request.DepartmentId,
                    DeliveryId = delivery.Id,
                    Type = "saida",
                    Quantity = -quantity,
                    BalanceAfter = stock.OnHand,
                    Reason = "Entrega de solicitação interna",
                    Notes = notes,
                    DocumentRef = request.Code,
                    Purpose = request.Purpose,
                    Recipient = receivedByName,
                    PurchaseTicketNo = request.PurchaseTicketNo,
                    LocationId = locationId,
                    IdempotencyKey = movementKey,
                    IdempotencyRequestHash = requestHash,
                    CreatedAt = now
                });
                database.DeliveryItems.Add(new DeliveryItem
                {
                    DeliveryId = delivery.Id,
                    Delivery = delivery,
                    ItemId = line.ItemId,
                    Quantity = quantity,
                    BalanceAfter = stock.OnHand
                });
                line.QuantityDelivered = checked(line.QuantityDelivered + quantity);
                line.QuantityReserved = line.QuantityDelivered;
            }

            request.Status = "finalizada";
            request.StatusChangedAt = now;
            request.UpdatedAt = now;
            database.RequestStatusHistory.Add(new InternalRequestStatusHistory
            {
                RequestId = request.Id,
                FromStatus = "pronta",
                ToStatus = "finalizada",
                UserId = userId,
                Comment = $"Protocolo {code}",
                CreatedAt = now
            });
            database.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                UserName = userName,
                Action = "deliver",
                EntityType = "delivery",
                EntityId = delivery.Id,
                EntityLabel = delivery.Code,
                BeforeJson = JsonSerializer.Serialize(new { request_id = request.Id, status = "pronta" }),
                AfterJson = JsonSerializer.Serialize(new { request_id = request.Id, status = request.Status, code }),
                Ip = ip,
                CreatedAt = now
            });
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return new InternalRequestDeliveryResult(delivery, request.Status, false);
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

    private async Task<int> NextSequenceAsync(string period, CancellationToken ct)
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
                    INSERT INTO "Sequences" ("Name", "Period", "LastValue") VALUES ('protocol', @period, 1)
                    ON CONFLICT ("Name", "Period") DO UPDATE SET "LastValue" = "Sequences"."LastValue" + 1
                    RETURNING "LastValue"
                    """;
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

        var counter = await database.SequenceCounters.SingleOrDefaultAsync(
            value => value.Name == "protocol" && value.Period == period, ct);
        if (counter is null)
        {
            database.SequenceCounters.Add(new SequenceCounter { Name = "protocol", Period = period, LastValue = 1 });
            return 1;
        }
        return counter.LastValue = checked(counter.LastValue + 1);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
