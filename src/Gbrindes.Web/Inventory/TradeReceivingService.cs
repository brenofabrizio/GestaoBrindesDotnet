using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gbrindes.Web.Data;
using Gbrindes.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Gbrindes.Web.Inventory;

public sealed class TradeReceivingFailureException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed record TradeReceiptLine(Guid ItemId, int Quantity);
public sealed record TradeReceiptResult(InternalRequest Request, bool Replayed);

public sealed class TradeReceivingService(ApplicationDbContext database, InventoryService inventory, TradeInvoiceStorage invoices)
{
    private const string PostgresProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public async Task<TradeReceiptResult> ReceiveAsync(Guid requestId, string? invoiceNo, IFormFile? invoiceFile,
        IReadOnlyCollection<TradeReceiptLine>? lines, Guid userId, string idempotencyKey, CancellationToken ct)
    {
        invoiceNo = invoiceNo?.Trim();
        var preparedInvoice = await invoices.PrepareAsync(invoiceFile, ct);
        if (invoiceNo?.Length > 60 || (string.IsNullOrWhiteSpace(invoiceNo) && preparedInvoice is null))
            throw new TradeReceivingFailureException(422, "VALIDATION_ERROR", "Informe o número da NF ou anexe um arquivo válido de até 10 MB.");
        if (string.IsNullOrWhiteSpace(invoiceNo))
            invoiceNo = null;
        if (lines is null or { Count: 0 } || lines.Count > 100
            || lines.Any(line => line.ItemId == Guid.Empty || line.Quantity is < 1 or > 1_000_000)
            || lines.Select(line => line.ItemId).Distinct().Count() != lines.Count)
            throw new TradeReceivingFailureException(422, "VALIDATION_ERROR", "Itens ou quantidades recebidas inválidos ou duplicados.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(idempotencyKey, "^[A-Za-z0-9_-]{8,80}$"))
            throw new TradeReceivingFailureException(400, "IDEMPOTENCY_KEY_REQUIRED", "Cabeçalho Idempotency-Key ausente ou inválido.");

        var orderedLines = lines.OrderBy(line => line.ItemId).ToArray();
        var payload = preparedInvoice is null
            ? JsonSerializer.Serialize(new { invoiceNo, items = orderedLines })
            : JsonSerializer.Serialize(new { invoiceNo, items = orderedLines, invoiceSha256 = preparedInvoice.Sha256 });
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)));

        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational())
            transaction = await database.Database.BeginTransactionAsync(ct);
        string? newInvoicePath = null;
        string? previousInvoicePath = null;
        var committed = false;
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
                throw new TradeReceivingFailureException(404, "NOT_FOUND", "Solicitação TRADE não encontrada.");

            var actorScope = await database.Users.AsNoTracking().Where(user => user.Id == userId)
                .Select(user => new { user.IndustryId, user.DepartmentId }).SingleOrDefaultAsync(ct);
            if (actorScope?.IndustryId is Guid industryId && request.IndustryId != industryId)
                throw new TradeReceivingFailureException(403, "INDUSTRY_FORBIDDEN", "Você só pode receber solicitações da sua indústria.");
            if (actorScope?.DepartmentId is Guid departmentId && request.DepartmentId != departmentId)
                throw new TradeReceivingFailureException(404, "NOT_FOUND", "Solicitação TRADE não encontrada.");

            await database.Entry(request).Collection(value => value.Items).LoadAsync(ct);
            var priorReceipts = await database.AuditLogs.AsNoTracking()
                .Where(log => log.EntityType == "request" && log.EntityId == requestId && log.Action == "receive")
                .Select(log => log.AfterJson).ToListAsync(ct);
            foreach (var receiptJson in priorReceipts)
            {
                if (string.IsNullOrWhiteSpace(receiptJson))
                    continue;
                using var prior = JsonDocument.Parse(receiptJson);
                var root = prior.RootElement;
                if (!root.TryGetProperty("idempotency_key_hash", out var savedKey)
                    || savedKey.GetString() != keyHash)
                    continue;
                if (!root.TryGetProperty("request_hash", out var savedPayload)
                    || savedPayload.GetString() != payloadHash)
                    throw new TradeReceivingFailureException(409, "IDEMPOTENCY_CONFLICT", "Chave já utilizada com dados diferentes.");
                if (transaction is not null)
                    await transaction.CommitAsync(ct);
                return new TradeReceiptResult(request, true);
            }

            if (request.Status is not ("aguardando_recebimento" or "compra_realizada" or "recebido_cd"))
                throw new TradeReceivingFailureException(409, "INVALID_TRANSITION", "Só é possível receber brindes após a aprovação e o chamado de compra.");

            var requestItems = request.Items.ToDictionary(item => item.ItemId);
            foreach (var line in orderedLines)
            {
                if (!requestItems.TryGetValue(line.ItemId, out var requestItem))
                    throw new TradeReceivingFailureException(422, "ITEM_NOT_IN_REQUEST", "O brinde não pertence à solicitação TRADE.");
                if (line.Quantity > requestItem.QuantityRequested - requestItem.QuantityReceived)
                    throw new TradeReceivingFailureException(422, "RECEIPT_EXCEEDS_REQUEST", "A quantidade recebida excede o saldo pendente da solicitação.");
            }

            previousInvoicePath = request.InvoicePath;
            if (preparedInvoice is not null)
            {
                newInvoicePath = invoices.Store(requestId, preparedInvoice).Path;
                request.InvoicePath = newInvoicePath;
            }

            var fromStatus = request.Status;
            var previousInvoice = request.InvoiceNo;
            var now = DateTimeOffset.UtcNow;
            foreach (var line in orderedLines)
            {
                var movementKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"trade-receive:{idempotencyKey}:{requestId}:{line.ItemId}")));
                await inventory.MoveAsync(line.ItemId, "entrada", line.Quantity, userId,
                    "Recebimento TRADE no CD", null, invoiceNo ?? request.InvoiceNo, "Recebimento no CD",
                    request.Recipient, request.PurchaseTicketNo, null, movementKey, ct, request.IndustryId);
                var requestItem = requestItems[line.ItemId];
                requestItem.QuantityReceived = checked(requestItem.QuantityReceived + line.Quantity);
            }

            request.InvoiceNo = invoiceNo ?? request.InvoiceNo;
            request.UpdatedAt = now;
            var complete = request.Items.All(item => item.QuantityReceived >= item.QuantityRequested);
            if (request.Status == "compra_realizada")
            {
                request.Status = "aguardando_recebimento";
                request.StatusChangedAt = now;
                database.RequestStatusHistory.Add(new InternalRequestStatusHistory
                {
                    RequestId = requestId,
                    FromStatus = "compra_realizada",
                    ToStatus = "aguardando_recebimento",
                    UserId = userId,
                    Comment = "NF registrada no recebimento do CD.",
                    CreatedAt = now
                });
            }
            if (complete && request.Status == "aguardando_recebimento")
            {
                database.RequestStatusHistory.AddRange(
                    new InternalRequestStatusHistory { RequestId = requestId, FromStatus = "aguardando_recebimento", ToStatus = "recebido_cd", UserId = userId, Comment = "Recebido no CD.", CreatedAt = now },
                    new InternalRequestStatusHistory { RequestId = requestId, FromStatus = "recebido_cd", ToStatus = "pronta", UserId = userId, Comment = "Disponível para retirada.", CreatedAt = now });
                request.Status = "pronta";
                request.StatusChangedAt = now;
            }

            database.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                Action = "receive",
                EntityType = "request",
                EntityId = requestId,
                EntityLabel = request.Code,
                BeforeJson = JsonSerializer.Serialize(new { status = fromStatus, invoice_no = previousInvoice, invoice_attached = previousInvoicePath is not null }),
                AfterJson = JsonSerializer.Serialize(new
                {
                    status = request.Status,
                    invoice_no = request.InvoiceNo,
                    invoice_attached = request.InvoicePath is not null,
                    idempotency_key_hash = keyHash,
                    request_hash = payloadHash
                }),
                CreatedAt = now
            });
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            committed = true;
            if (previousInvoicePath != request.InvoicePath)
                invoices.Delete(previousInvoicePath);
            return new TradeReceiptResult(request, false);
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(ct);
            if (!committed)
                invoices.Delete(newInvoicePath);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }
}
