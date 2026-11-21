using Gbrindes.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using Gbrindes.Web.Storage;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Route("api/v1/stock/exit-orders")]
[Authorize]
public sealed class StockExitOrderQueueController(ApplicationDbContext database, StockAttachmentStorage attachments) : ControllerBase
{
    [HttpGet("{id:guid}/qr")]
    public async Task<IActionResult> Qr(Guid id, CancellationToken ct)
    {
        if (!User.HasClaim("permission", "stock.exit") && !User.HasClaim("permission", "stock.exit_confirm"))
            return Forbid();

        var orderData = await database.StockExitOrders.AsNoTracking()
            .Where(order => order.Id == id)
            .Select(order => new { order.Code, order.IndustryId, order.DepartmentId })
            .SingleOrDefaultAsync(ct);
        if (orderData is null)
            return NotFound(new { ok = false, error = new { code = "EXIT_ORDER_NOT_FOUND", message = "Saída autorizada não encontrada." } });
        if (!await IsInViewerScopeAsync(orderData.IndustryId, orderData.DepartmentId, ct))
            return NotFound(new { ok = false, error = new { code = "EXIT_ORDER_NOT_FOUND", message = "Saída autorizada não encontrada." } });

        using var qrData = QRCodeGenerator.GenerateQrCode(orderData.Code, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(qrData).GetGraphic(6);
        Response.Headers.CacheControl = "private, max-age=120";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(png, "image/png");
    }

    [HttpGet("{id:guid}/attachment")]
    public async Task<IActionResult> Attachment(Guid id, CancellationToken ct)
    {
        if (!User.HasClaim("permission", "stock.exit") && !User.HasClaim("permission", "stock.exit_confirm"))
            return Forbid();

        var order = await database.StockExitOrders.AsNoTracking()
            .Where(value => value.Id == id)
            .Select(value => new { value.AttachmentPath, value.DocumentRef, value.Code, value.IndustryId, value.DepartmentId })
            .SingleOrDefaultAsync(ct);
        if (order is null || string.IsNullOrWhiteSpace(order.AttachmentPath))
            return NotFound(new { ok = false, error = new { code = "EXIT_ORDER_ATTACHMENT_NOT_FOUND", message = "Nota da saída não encontrada." } });
        if (!await IsInViewerScopeAsync(order.IndustryId, order.DepartmentId, ct))
            return NotFound(new { ok = false, error = new { code = "EXIT_ORDER_ATTACHMENT_NOT_FOUND", message = "Nota da saída não encontrada." } });

        var stream = attachments.OpenRead(order.AttachmentPath);
        if (stream is null)
            return NotFound(new { ok = false, error = new { code = "EXIT_ORDER_ATTACHMENT_NOT_FOUND", message = "Arquivo da nota não encontrado." } });

        var reference = string.IsNullOrWhiteSpace(order.DocumentRef) ? order.Code : order.DocumentRef;
        var safeReference = string.Concat(reference.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_'));
        var fileName = $"NF-{(string.IsNullOrWhiteSpace(safeReference) ? order.Code : safeReference)}{Path.GetExtension(order.AttachmentPath)}";
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(stream, StockAttachmentStorage.ContentTypeForPath(order.AttachmentPath), fileName);
    }

    [HttpGet("lookup")]
    public async Task<IActionResult> Lookup([FromQuery] string? code, CancellationToken ct)
    {
        if (!User.HasClaim("permission", "stock.exit") && !User.HasClaim("permission", "stock.exit_confirm"))
            return Forbid();
        code = code?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code))
            return BadRequest(new { ok = false, error = new { code = "VALIDATION_ERROR", message = "Informe o código da saída." } });

        var id = await database.StockExitOrders.AsNoTracking()
            .Where(order => order.Code == code)
            .Select(order => (Guid?)order.Id)
            .SingleOrDefaultAsync(ct);
        if (id is null)
            return NotFound(new { ok = false, error = new { code = "EXIT_ORDER_NOT_FOUND", message = "Nenhuma saída com este QR / código." } });
        return await Get(id.Value, ct);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!User.HasClaim("permission", "stock.exit") && !User.HasClaim("permission", "stock.exit_confirm"))
            return Forbid();

        var order = await database.StockExitOrders.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, ct);
        if (order is null)
            return NotFound(new { ok = false, error = new { code = "EXIT_ORDER_NOT_FOUND", message = "Saída autorizada não encontrada." } });
        if (!await IsInViewerScopeAsync(order.IndustryId, order.DepartmentId, ct))
            return NotFound(new { ok = false, error = new { code = "EXIT_ORDER_NOT_FOUND", message = "Saída autorizada não encontrada." } });

        var item = await database.CatalogItems.AsNoTracking()
            .Where(value => value.Id == order.ItemId)
            .Select(value => new { id = value.Id, code = value.Code, name = value.Name })
            .SingleAsync(ct);
        var author = await database.Users.AsNoTracking()
            .Where(value => value.Id == order.AuthorizedBy)
            .Select(value => new { id = value.Id, name = value.FullName })
            .SingleAsync(ct);
        var industry = order.IndustryId is Guid industryId
            ? await database.Industries.AsNoTracking().Where(value => value.Id == industryId)
                .Select(value => new { id = value.Id, name = value.Name }).SingleOrDefaultAsync(ct)
            : null;
        var department = order.DepartmentId is Guid departmentId
            ? await database.Departments.AsNoTracking().Where(value => value.Id == departmentId)
                .Select(value => new { id = value.Id, name = value.Name }).SingleOrDefaultAsync(ct)
            : null;
        var confirmer = order.ConfirmedBy is Guid confirmerId
            ? await database.Users.AsNoTracking().Where(value => value.Id == confirmerId)
                .Select(value => new { id = (Guid?)value.Id, name = value.FullName }).SingleOrDefaultAsync(ct)
            : null;
        using var qrData = QRCodeGenerator.GenerateQrCode(order.Code, QRCodeGenerator.ECCLevel.M);
        var qrPng = new PngByteQRCode(qrData).GetGraphic(5);

        return Ok(new
        {
            ok = true,
            data = new
            {
                id = order.Id,
                code = order.Code,
                status = order.Status,
                status_label = order.Status == "autorizada" ? "Autorizada — aguardando CD" : order.Status,
                qty = order.Quantity,
                purpose = order.Purpose,
                recipient = order.Recipient,
                purchase_ticket_no = order.PurchaseTicketNo,
                document_ref = order.DocumentRef,
                notes = order.Notes,
                item,
                industry,
                department,
                authorized_by = author,
                authorized_at = order.AuthorizedAt,
                confirmed_by = confirmer,
                confirmed_at = order.ConfirmedAt,
                movement_id = order.MovementId,
                qr_png = $"data:image/png;base64,{Convert.ToBase64String(qrPng)}",
                qr_url = $"/api/v1/stock/exit-orders/{order.Id}/qr",
                attachment_url = order.AttachmentUrl,
                created_at = order.CreatedAt
            }
        });
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string status = "autorizada", CancellationToken ct = default)
    {
        if (!User.HasClaim("permission", "stock.exit") && !User.HasClaim("permission", "stock.exit_confirm"))
            return Forbid();

        var query = database.StockExitOrders.AsNoTracking();
        if (Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var viewerId))
        {
            var scope = await database.Users.AsNoTracking().Where(user => user.Id == viewerId)
                .Select(user => new { user.IndustryId, user.DepartmentId }).SingleOrDefaultAsync(ct);
            if (scope?.IndustryId is Guid industryId)
                query = query.Where(order => order.IndustryId == industryId);
            if (scope?.DepartmentId is Guid departmentId)
                query = query.Where(order => order.DepartmentId == departmentId);
        }
        if (!string.IsNullOrWhiteSpace(status) && status != "todas")
            query = query.Where(order => order.Status == status);

        var orders = await (from order in query
                            join item in database.CatalogItems.AsNoTracking() on order.ItemId equals item.Id
                            join author in database.Users.AsNoTracking() on order.AuthorizedBy equals author.Id
                            orderby order.CreatedAt descending, order.Id descending
                            select new
                            {
                                id = order.Id,
                                code = order.Code,
                                status = order.Status,
                                status_label = order.Status == "autorizada" ? "Autorizada — aguardando CD" : order.Status,
                                qty = order.Quantity,
                                purpose = order.Purpose,
                                recipient = order.Recipient,
                                purchase_ticket_no = order.PurchaseTicketNo,
                                document_ref = order.DocumentRef,
                                has_attachment = order.AttachmentPath != null,
                                notes = order.Notes,
                                item = new { id = order.ItemId, code = item.Code, name = item.Name },
                                authorized_by = new { id = order.AuthorizedBy, name = author.FullName },
                                authorized_at = order.AuthorizedAt,
                                confirmed_by = order.ConfirmedBy,
                                confirmed_at = order.ConfirmedAt,
                                movement_id = order.MovementId,
                                departmentId = order.DepartmentId,
                                industryId = order.IndustryId,
                                created_at = order.CreatedAt
                            })
            .Take(100)
            .ToListAsync(ct);

        var departmentIds = orders.Where(order => order.departmentId.HasValue).Select(order => order.departmentId!.Value).Distinct().ToArray();
        var industryIds = orders.Where(order => order.industryId.HasValue).Select(order => order.industryId!.Value).Distinct().ToArray();
        var departments = await database.Departments.AsNoTracking()
            .Where(value => departmentIds.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, value => value.Name, ct);
        var industries = await database.Industries.AsNoTracking()
            .Where(value => industryIds.Contains(value.Id))
            .ToDictionaryAsync(value => value.Id, value => value.Name, ct);
        var data = orders.Select(order =>
        {
            var department = order.departmentId is Guid departmentId && departments.TryGetValue(departmentId, out var departmentName)
                ? new { id = (Guid?)departmentId, name = departmentName }
                : null;
            var industry = order.industryId is Guid industryId && industries.TryGetValue(industryId, out var industryName)
                ? new { id = (Guid?)industryId, name = industryName }
                : null;
            return new
            {
                order.id,
                order.code,
                order.status,
                order.status_label,
                order.qty,
                order.purpose,
                order.recipient,
                order.purchase_ticket_no,
                order.document_ref,
                order.has_attachment,
                attachment_url = order.has_attachment ? $"/api/v1/stock/exit-orders/{order.id}/attachment" : null,
                order.notes,
                order.item,
                department,
                industry,
                order.authorized_by,
                order.authorized_at,
                order.confirmed_by,
                order.confirmed_at,
                order.movement_id,
                qr_png = QrDataUri(order.code),
                qr_url = $"/api/v1/stock/exit-orders/{order.id}/qr",
                order.created_at
            };
        }).ToArray();

        return Ok(new { ok = true, data });
    }

    private static string QrDataUri(string code)
    {
        using var qrData = QRCodeGenerator.GenerateQrCode(code, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(qrData).GetGraphic(5);
        return $"data:image/png;base64,{Convert.ToBase64String(png)}";
    }

    private async Task<bool> IsInViewerScopeAsync(Guid? industryId, Guid? departmentId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var viewerId))
            return false;
        var scope = await database.Users.AsNoTracking().Where(user => user.Id == viewerId)
            .Select(user => new { user.IndustryId, user.DepartmentId }).SingleOrDefaultAsync(ct);
        return scope is not null
            && (!scope.IndustryId.HasValue || scope.IndustryId == industryId)
            && (!scope.DepartmentId.HasValue || scope.DepartmentId == departmentId);
    }
}