using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Gbrindes.Web.Inventory;
using Gbrindes.Web.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Route("api/v1/stock/exits")]
[Authorize(Policy = "stock.exit")]
public sealed class StockExitOrdersController(StockExitOrderService exitOrders, StockAttachmentStorage attachments) : ControllerBase
{
    [HttpPost, Consumes("application/json")]
    public Task<IActionResult> Create(StockExitOrderCreateRequest request, CancellationToken ct) =>
        CreateCoreAsync(request, null, null, ct);

    [HttpPost, Consumes("multipart/form-data")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 11 * 1024 * 1024)]
    public async Task<IActionResult> CreateWithAttachment([FromForm] StockExitOrderFormRequest form, CancellationToken ct)
    {
        var request = new StockExitOrderCreateRequest
        {
            ItemId = form.ItemId,
            Quantity = form.Quantity,
            Purpose = form.Purpose,
            Recipient = form.Recipient,
            IndustryId = form.IndustryId,
            DepartmentId = form.DepartmentId,
            RequesterId = form.RequesterId,
            PurchaseTicketNo = form.PurchaseTicketNo,
            DocumentRef = form.DocumentRef,
            Notes = form.Notes
        };
        StoredStockAttachment? stored = null;
        PreparedStockAttachment? prepared;
        try
        {
            prepared = await attachments.PrepareAsync(form.Attachment, ct);
            if (prepared is not null)
                stored = attachments.Store(request.ItemId, prepared);
        }
        catch (StockAttachmentValidationException exception)
        {
            return UnprocessableEntity(new ProblemDetails { Status = 422, Title = "INVALID_ATTACHMENT", Detail = exception.Message });
        }

        try
        {
            return await CreateCoreAsync(request, stored?.Path, prepared?.Sha256, ct);
        }
        catch
        {
            attachments.Delete(stored?.Path);
            throw;
        }
    }

    private async Task<IActionResult> CreateCoreAsync(StockExitOrderCreateRequest request, string? attachmentPath,
        string? attachmentSha256, CancellationToken ct)
    {
        if (IsCdOperations(User))
        {
            attachments.Delete(attachmentPath);
            return Forbid();
        }

        var userIdText = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdText, out var userId))
        {
            attachments.Delete(attachmentPath);
            return Unauthorized();
        }

        var key = Request.Headers["Idempotency-Key"].ToString();
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z0-9_-]{8,80}$"))
        {
            attachments.Delete(attachmentPath);
            return BadRequest(new { ok = false, error = new { code = "IDEMPOTENCY_KEY_REQUIRED", message = "Cabeçalho Idempotency-Key ausente ou inválido." } });
        }

        try
        {
            var result = await exitOrders.AuthorizeAsync(
                request.ItemId,
                request.Quantity,
                request.Purpose,
                request.Recipient,
                request.IndustryId,
                request.DepartmentId,
                request.RequesterId,
                request.PurchaseTicketNo,
                request.DocumentRef,
                request.Notes,
                userId,
                User.Identity?.Name ?? string.Empty,
                key,
                ct,
                attachmentPath,
                attachmentSha256);
            if (result.Replayed)
                Response.Headers["Idempotent-Replayed"] = "true";
            if (!string.Equals(result.Order.AttachmentPath, attachmentPath, StringComparison.Ordinal))
                attachments.Delete(attachmentPath);

            return StatusCode(StatusCodes.Status201Created, new
            {
                ok = true,
                data = new
                {
                    order = new StockExitOrderView(
                        result.Order.Id,
                        result.Order.Code,
                        result.Order.ItemId,
                        result.Order.Quantity,
                        result.Order.Purpose,
                        result.Order.Recipient,
                        result.Order.PurchaseTicketNo,
                        result.Order.DocumentRef,
                        result.Order.Notes,
                        result.Order.Status,
                        result.Order.AuthorizedBy,
                        result.Order.AuthorizedAt,
                        result.Order.AttachmentUrl),
                    stock = new { onHand = result.OnHand, reserved = result.Reserved, available = result.OnHand - result.Reserved }
                }
            });
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

    private static bool IsCdOperations(ClaimsPrincipal user)
    {
        if (user.IsInRole("admin"))
            return false;

        return user.FindAll(ClaimTypes.Role).Select(claim => claim.Value)
            .Concat(user.FindAll("role_slug").Select(claim => claim.Value))
            .Any(role => string.Equals(role, "operations", StringComparison.OrdinalIgnoreCase)
                || role.Contains("cd / estoque", StringComparison.OrdinalIgnoreCase)
                || role.Contains("cd/estoque", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class StockExitOrderCreateRequest
{
    [JsonPropertyName("item_id"), Required] public Guid ItemId { get; set; }
    [Range(1, 1_000_000)] public int Quantity { get; set; }
    [Required, StringLength(255, MinimumLength = 1)] public string Purpose { get; set; } = string.Empty;
    [StringLength(150)] public string? Recipient { get; set; }
    [JsonPropertyName("industry_id")] public Guid? IndustryId { get; set; }
    [JsonPropertyName("department_id")] public Guid? DepartmentId { get; set; }
    [JsonPropertyName("requester_id")] public Guid? RequesterId { get; set; }
    [JsonPropertyName("purchase_ticket_no"), StringLength(50)] public string? PurchaseTicketNo { get; set; }
    [JsonPropertyName("document_ref"), StringLength(60)] public string? DocumentRef { get; set; }
    [StringLength(1_000)] public string? Notes { get; set; }
}

public sealed class StockExitOrderFormRequest
{
    [FromForm(Name = "item_id"), Required] public Guid ItemId { get; set; }
    [FromForm(Name = "quantity"), Range(1, 1_000_000)] public int Quantity { get; set; }
    [FromForm(Name = "purpose"), Required, StringLength(255, MinimumLength = 1)] public string Purpose { get; set; } = string.Empty;
    [FromForm(Name = "recipient"), StringLength(150)] public string? Recipient { get; set; }
    [FromForm(Name = "industry_id")] public Guid? IndustryId { get; set; }
    [FromForm(Name = "department_id")] public Guid? DepartmentId { get; set; }
    [FromForm(Name = "requester_id")] public Guid? RequesterId { get; set; }
    [FromForm(Name = "purchase_ticket_no"), StringLength(50)] public string? PurchaseTicketNo { get; set; }
    [FromForm(Name = "document_ref"), StringLength(60)] public string? DocumentRef { get; set; }
    [FromForm(Name = "notes"), StringLength(1_000)] public string? Notes { get; set; }
    [FromForm(Name = "attachment")] public IFormFile? Attachment { get; set; }
}

public sealed record StockExitOrderView(
    Guid Id,
    string Code,
    Guid ItemId,
    int Quantity,
    string Purpose,
    string? Recipient,
    string? PurchaseTicketNo,
    string? DocumentRef,
    string? Notes,
    string Status,
    Guid AuthorizedBy,
    DateTimeOffset AuthorizedAt,
    [property: JsonPropertyName("attachment_url")] string? AttachmentUrl = null);
