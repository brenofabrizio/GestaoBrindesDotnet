using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Gbrindes.Web.Data;
using Gbrindes.Web.Inventory;
using Gbrindes.Web.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Route("api/v1/stock/adjustments")]
[Authorize(Policy = "stock.adjust")]
public sealed class StockAdjustmentsController(InventoryService inventory) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(AdjustmentRequest request, CancellationToken ct)
    {
        var key = Request.Headers["Idempotency-Key"].ToString();
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z0-9_-]{8,80}$"))
            return BadRequest(new { ok = false, error = new { code = "IDEMPOTENCY_KEY_REQUIRED", message = "Cabeçalho Idempotency-Key ausente ou inválido." } });
        try
        {
            var userId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (Guid?)null;
            var movement = await inventory.AdjustAsync(request.ItemId, request.Mode, request.Quantity, userId, request.Reason, request.Notes, key, ct);
            return StatusCode(201, new
            {
                ok = true,
                data = new StockMovementView(movement.Id, movement.ItemId, movement.Type,
                movement.Quantity, movement.BalanceAfter, movement.Reason, movement.Notes, movement.DocumentRef,
                movement.IdempotencyKey, movement.CreatedAt, movement.UserId, movement.Purpose, movement.Recipient, movement.PurchaseTicketNo,
                movement.IndustryId, movement.LocationId, movement.FromLocationId, movement.ToLocationId, movement.TransferId)
            });
        }
        catch (InventoryFailureException ex)
        {
            return StatusCode(ex.StatusCode, new { ok = false, error = new { code = ex.Code, message = ex.Message } });
        }
    }
}

public sealed class AdjustmentRequest
{
    [JsonPropertyName("item_id")] public Guid ItemId { get; set; }
    public string Mode { get; set; } = "";
    public int Quantity { get; set; }
    public string? Reason { get; set; }
    public string? Notes { get; set; }
}

[ApiController]
[Route("api/v1/stock/reversals")]
[Authorize(Policy = "stock.adjust")]
public sealed class StockReversalsController(InventoryService inventory) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(ReversalRequest request, CancellationToken ct)
    {
        var key = Request.Headers["Idempotency-Key"].ToString();
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z0-9_-]{8,80}$"))
            return BadRequest(new { ok = false, error = new { code = "IDEMPOTENCY_KEY_REQUIRED", message = "Cabeçalho Idempotency-Key ausente ou inválido." } });
        try
        {
            var userId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (Guid?)null;
            var movement = await inventory.ReverseAsync(request.MovementId, request.Reason, userId, key, ct);
            return StatusCode(201, new
            {
                ok = true,
                data = new StockMovementView(movement.Id, movement.ItemId, movement.Type,
                movement.Quantity, movement.BalanceAfter, movement.Reason, movement.Notes, movement.DocumentRef,
                movement.IdempotencyKey, movement.CreatedAt, movement.UserId, movement.Purpose, movement.Recipient, movement.PurchaseTicketNo,
                movement.IndustryId, movement.LocationId, movement.FromLocationId, movement.ToLocationId, movement.TransferId)
            });
        }
        catch (InventoryFailureException ex)
        {
            return StatusCode(ex.StatusCode, new { ok = false, error = new { code = ex.Code, message = ex.Message } });
        }
    }
}

public sealed class ReversalRequest
{
    [JsonPropertyName("movement_id")] public Guid MovementId { get; set; }
    public string? Reason { get; set; }
}

[ApiController]
[Route("api/v1/items/{itemId:guid}")]
public sealed class InventoryController(InventoryService inventory, StockAttachmentStorage attachments) : ControllerBase
{
    [HttpPost("entry"), Authorize(Policy = "stock.entry"), Consumes("application/json")]
    public Task<IActionResult> Entry(Guid itemId, EntryRequest request, CancellationToken ct) =>
        Move(itemId, "entrada", request.Quantity, request.Reason, request.Notes, request.DocumentRef,
            request.Purpose, request.Recipient, request.PurchaseTicketNo, request.SupplierId, request.IdempotencyKey, request.IndustryId, ct, request.UnitValue);

    [HttpPost("entry"), Authorize(Policy = "stock.entry"), Consumes("multipart/form-data")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 11 * 1024 * 1024)]
    public async Task<IActionResult> EntryWithAttachment(Guid itemId, [FromForm] EntryFormRequest request, CancellationToken ct)
    {
        StoredStockAttachment? stored = null;
        PreparedStockAttachment? prepared;
        try
        {
            prepared = await attachments.PrepareAsync(request.Attachment, ct);
            if (prepared is not null)
                stored = attachments.Store(itemId, prepared);
        }
        catch (StockAttachmentValidationException exception)
        {
            return UnprocessableEntity(new ProblemDetails { Status = 422, Title = "INVALID_ATTACHMENT", Detail = exception.Message });
        }

        return await Move(itemId, "entrada", request.Quantity, request.Reason, request.Notes, request.DocumentRef,
            request.Purpose, request.Recipient, request.PurchaseTicketNo, request.SupplierId, request.IdempotencyKey,
            request.IndustryId, ct, request.UnitValue, stored?.Path, prepared?.Sha256);
    }

    [HttpPost("exit"), Authorize(Policy = "stock.exit")]
    public Task<IActionResult> Exit(Guid itemId, ExitRequest request, CancellationToken ct) =>
        Move(itemId, "saida", request.Quantity, request.Reason, request.Notes, request.DocumentRef,
            request.Purpose, request.Recipient, request.PurchaseTicketNo, null, request.IdempotencyKey, null, ct);

    private async Task<IActionResult> Move(Guid itemId, string type, int quantity, string? reason, string? notes,
        string? documentRef, string? purpose, string? recipient, string? purchaseTicketNo, Guid? supplierId,
        string? key, Guid? industryId, CancellationToken ct, decimal? unitValue = null,
        string? attachmentPath = null, string? attachmentSha256 = null)
    {
        try
        {
            var userIdText = User.FindFirstValue(ClaimTypes.NameIdentifier);
            Guid? userId = Guid.TryParse(userIdText, out var id) ? id : null;
            var movement = await inventory.MoveAsync(itemId, type, quantity, userId, reason, notes, documentRef,
                purpose, recipient, purchaseTicketNo, supplierId, key, ct, industryId,
                unitValue: unitValue, attachmentPath: attachmentPath, attachmentSha256: attachmentSha256);
            if (!string.Equals(movement.AttachmentPath, attachmentPath, StringComparison.Ordinal))
                attachments.Delete(attachmentPath);
            return StatusCode(StatusCodes.Status201Created, movement);
        }
        catch (InventoryFailureException exception)
        {
            attachments.Delete(attachmentPath);
            return StatusCode(exception.StatusCode, new ProblemDetails
            {
                Status = exception.StatusCode,
                Title = exception.Code,
                Detail = exception.Message
            });
        }
        catch
        {
            attachments.Delete(attachmentPath);
            throw;
        }
    }
}

[ApiController]
[Route("api/v1/stock/movements")]
[Authorize(Policy = "stock.view")]
public sealed class StockMovementsController(ApplicationDbContext database, StockAttachmentStorage attachments) : ControllerBase
{
    [HttpGet("{movementId:guid}/attachment")]
    public async Task<IActionResult> Attachment(Guid movementId, CancellationToken ct)
    {
        var query = database.StockMovements.AsNoTracking().Where(movement => movement.Id == movementId);
        if (Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            var scope = await database.Users.AsNoTracking().Where(user => user.Id == userId)
                .Select(user => new { user.IndustryId, user.DepartmentId }).SingleOrDefaultAsync(ct);
            if (scope?.IndustryId is Guid industryId)
                query = query.Where(movement => movement.IndustryId == industryId);
            if (scope?.DepartmentId is Guid departmentId)
                query = query.Where(movement => movement.DepartmentId == departmentId);
        }
        var path = await query.Select(movement => movement.AttachmentPath).SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(path) || attachments.OpenRead(path) is not { } stream)
            return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "private, no-store";
        var extension = Path.GetExtension(path);
        return File(stream, StockAttachmentStorage.ContentTypeForPath(path), $"NF-{movementId:N}{extension}");
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? itemId, [FromQuery] string? type, [FromQuery] string? q,
        [FromQuery(Name = "item_id")] Guid? itemIdSnake,
        [FromQuery(Name = "user_id")] Guid? userIdFilter, [FromQuery(Name = "industry_id")] Guid? industryIdFilter,
        [FromQuery(Name = "department_id")] Guid? departmentIdFilter,
        [FromQuery(Name = "supplier_id")] Guid? supplierIdFilter,
        [FromQuery(Name = "category_id")] Guid? categoryIdFilter,
        [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = database.StockMovements.AsNoTracking().AsQueryable();
        if (Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var viewerId))
        {
            var scope = await database.Users.AsNoTracking()
                .Where(user => user.Id == viewerId)
                .Select(user => new { user.IndustryId, user.DepartmentId })
                .SingleOrDefaultAsync(ct);
            if (scope?.IndustryId is Guid scopedIndustryId)
                query = query.Where(movement => movement.IndustryId == scopedIndustryId);
            if (scope?.DepartmentId is Guid scopedDepartmentId)
                query = query.Where(movement => movement.DepartmentId == scopedDepartmentId);
        }
        if (itemId.HasValue)
            query = query.Where(x => x.ItemId == itemId.Value);
        if (itemIdSnake.HasValue)
            query = query.Where(x => x.ItemId == itemIdSnake.Value);
        if (userIdFilter.HasValue)
            query = query.Where(x => x.UserId == userIdFilter.Value);
        if (industryIdFilter.HasValue)
            query = query.Where(x => x.IndustryId == industryIdFilter.Value);
        if (departmentIdFilter.HasValue)
            query = query.Where(x => x.DepartmentId == departmentIdFilter.Value);
        if (supplierIdFilter.HasValue)
            query = query.Where(x => x.SupplierId == supplierIdFilter.Value);
        if (categoryIdFilter.HasValue)
            query = query.Where(x => x.Item.CategoryId == categoryIdFilter.Value);
        if (type is "entrada" or "saida" or "ajuste" or "transferencia" or "estorno")
            query = query.Where(x => x.Type == type);
        if (DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startDate))
        {
            var start = new DateTimeOffset(startDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            query = query.Where(x => x.CreatedAt >= start);
        }
        if (DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDate))
        {
            var end = new DateTimeOffset(endDate.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc));
            query = query.Where(x => x.CreatedAt <= end);
        }
        var search = q?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(search))
            query = query.Where(x => x.Item.Code.ToLower().Contains(search) || x.Item.Name.ToLower().Contains(search)
                || (x.Recipient != null && x.Recipient.ToLower().Contains(search))
                || (x.Purpose != null && x.Purpose.ToLower().Contains(search))
                || (x.PurchaseTicketNo != null && x.PurchaseTicketNo.ToLower().Contains(search))
                || (x.DocumentRef != null && x.DocumentRef.ToLower().Contains(search)));
        if (Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            var industryId = await database.Users.AsNoTracking().Where(user => user.Id == userId)
                .Select(user => user.IndustryId).SingleOrDefaultAsync(ct);
            if (industryId.HasValue)
                query = query.Where(movement => movement.IndustryId == industryId.Value);
        }
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                id = x.Id,
                itemId = x.ItemId,
                type = x.Type,
                quantity = x.Quantity,
                balanceAfter = x.BalanceAfter,
                reason = x.Reason,
                notes = x.Notes,
                documentRef = x.DocumentRef,
                idempotencyKey = x.IdempotencyKey,
                createdAt = x.CreatedAt,
                userId = x.UserId,
                purpose = x.Purpose,
                recipient = x.Recipient,
                purchaseTicketNo = x.PurchaseTicketNo,
                unit_value = x.UnitValue,
                hasAttachment = x.AttachmentPath != null,
                industryId = x.IndustryId,
                locationId = x.LocationId,
                fromLocationId = x.FromLocationId,
                toLocationId = x.ToLocationId,
                transferId = x.TransferId,
                supplier_id = x.SupplierId,
                department_id = x.DepartmentId,
                type_label = x.Type == "entrada" ? "Entrada" : x.Type == "saida" ? "Saída" : x.Type == "ajuste" ? "Ajuste"
                    : x.Type == "transferencia" ? "Transferência" : x.Type == "estorno" ? "Estorno / correção" : x.Type,
                qty = x.Quantity,
                balance_after = x.BalanceAfter,
                created_at = x.CreatedAt,
                item = new { id = x.ItemId, code = x.Item.Code, name = x.Item.Name },
                user = new
                {
                    id = x.UserId,
                    name = database.Users.Where(user => user.Id == x.UserId).Select(user => user.FullName).FirstOrDefault()
                }
            })
            .ToListAsync(ct);
        return Ok(new { items, page, pageSize, total });
    }
}

public sealed class EntryRequest
{
    [Range(1, 1_000_000)] public int Quantity { get; set; }
    [JsonPropertyName("unit_value"), Range(typeof(decimal), "0", "9999999999", ParseLimitsInInvariantCulture = true)] public decimal? UnitValue { get; set; }
    [StringLength(255)] public string? Reason { get; set; }
    [StringLength(2_000)] public string? Notes { get; set; }
    [StringLength(255)] public string? Purpose { get; set; }
    [StringLength(150)] public string? Recipient { get; set; }
    [JsonPropertyName("industry_id")] public Guid? IndustryId { get; set; }
    [JsonPropertyName("purchase_ticket_no"), StringLength(50)] public string? PurchaseTicketNo { get; set; }
    [JsonPropertyName("supplier_id")] public Guid? SupplierId { get; set; }
    [JsonPropertyName("document_ref"), StringLength(255)] public string? DocumentRef { get; set; }
    [JsonPropertyName("idempotency_key"), StringLength(80)] public string? IdempotencyKey { get; set; }
}

public sealed class EntryFormRequest
{
    [FromForm(Name = "quantity"), Range(1, 1_000_000)] public int Quantity { get; set; }
    [FromForm(Name = "unit_value"), Range(typeof(decimal), "0", "9999999999", ParseLimitsInInvariantCulture = true)] public decimal? UnitValue { get; set; }
    [FromForm(Name = "reason"), StringLength(255)] public string? Reason { get; set; }
    [FromForm(Name = "notes"), StringLength(2_000)] public string? Notes { get; set; }
    [FromForm(Name = "purpose"), StringLength(255)] public string? Purpose { get; set; }
    [FromForm(Name = "recipient"), StringLength(150)] public string? Recipient { get; set; }
    [FromForm(Name = "industry_id")] public Guid? IndustryId { get; set; }
    [FromForm(Name = "purchase_ticket_no"), StringLength(50)] public string? PurchaseTicketNo { get; set; }
    [FromForm(Name = "supplier_id")] public Guid? SupplierId { get; set; }
    [FromForm(Name = "document_ref"), StringLength(255)] public string? DocumentRef { get; set; }
    [FromForm(Name = "idempotency_key"), StringLength(80)] public string? IdempotencyKey { get; set; }
    [FromForm(Name = "attachment")] public IFormFile? Attachment { get; set; }
}

public sealed class ExitRequest
{
    [Range(1, 1_000_000)] public int Quantity { get; set; }
    [Required, StringLength(255, MinimumLength = 1)] public string Reason { get; set; } = string.Empty;
    [StringLength(2_000)] public string? Notes { get; set; }
    [StringLength(255)] public string? Purpose { get; set; }
    [StringLength(150)] public string? Recipient { get; set; }
    [JsonPropertyName("purchase_ticket_no"), StringLength(50)] public string? PurchaseTicketNo { get; set; }
    [JsonPropertyName("document_ref"), StringLength(255)] public string? DocumentRef { get; set; }
    [JsonPropertyName("idempotency_key"), StringLength(80)] public string? IdempotencyKey { get; set; }
}

public sealed record StockMovementView(Guid Id, Guid ItemId, string Type, int Quantity, int BalanceAfter,
    string? Reason, string? Notes, string? DocumentRef, string? IdempotencyKey, DateTimeOffset CreatedAt,
    Guid? UserId, string? Purpose = null, string? Recipient = null, string? PurchaseTicketNo = null,
    Guid? IndustryId = null, Guid? LocationId = null, Guid? FromLocationId = null, Guid? ToLocationId = null, Guid? TransferId = null,
    [property: JsonPropertyName("supplier_id")] Guid? SupplierId = null,
    [property: JsonPropertyName("department_id")] Guid? DepartmentId = null);
