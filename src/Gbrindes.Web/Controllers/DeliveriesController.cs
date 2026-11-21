using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Gbrindes.Web.Data;
using Gbrindes.Web.Protocols;
using Gbrindes.Web.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Route("api/v1/deliveries")]
public sealed class DeliveriesController(ApplicationDbContext database) : ControllerBase
{
    private const int DefaultPageSize = 25;
    private const int MaximumPageSize = 100;

    [HttpGet]
    [Authorize(Policy = "deliveries.view")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();
        var viewer = await database.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.IndustryId })
            .SingleOrDefaultAsync(ct);
        if (viewer is null)
            return Unauthorized();

        var query = database.Deliveries.AsNoTracking();
        if (viewer.IndustryId is Guid industryId)
            query = query.Where(delivery => delivery.IndustryId == industryId);

        var rawPage = Request.Query["page"].ToString();
        var currentPage = int.TryParse(rawPage, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPage) && parsedPage > 0
            ? parsedPage
            : 1;
        var rawPageSize = Request.Query["per_page"].ToString();
        var requestedPageSize = string.IsNullOrEmpty(rawPageSize) || rawPageSize == "0"
            ? DefaultPageSize
            : int.TryParse(rawPageSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPageSize)
                ? parsedPageSize
                : DefaultPageSize;
        var pageSize = Math.Clamp(requestedPageSize, 1, MaximumPageSize);
        var total = await query.CountAsync(ct);
        var lastPage = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        var offset = ((long)currentPage - 1) * pageSize;
        var deliveries = offset >= int.MaxValue
            ? []
            : await query
                .Include(delivery => delivery.Request)
                .Include(delivery => delivery.Items)
                    .ThenInclude(item => item.Item)
                .OrderByDescending(delivery => delivery.CreatedAt)
                .ThenByDescending(delivery => delivery.Id)
                .Skip((int)offset)
                .Take(pageSize)
                .ToListAsync(ct);

        var path = Request.Path.Value ?? "/api/v1/deliveries";
        string PageUrl(int page) => $"{path}?page={page}&per_page={pageSize}";
        var links = new List<PageLink> { new(currentPage > 1 ? PageUrl(currentPage - 1) : null, "&laquo; Previous", false) };
        foreach (var page in PageNumbers(currentPage, lastPage))
            links.Add(page is int number
                ? new PageLink(PageUrl(number), number.ToString(CultureInfo.InvariantCulture), number == currentPage)
                : new PageLink(null, "...", false));
        links.Add(new(currentPage < lastPage ? PageUrl(currentPage + 1) : null, "Next &raquo;", false));

        var from = deliveries.Count == 0 ? (int?)null : (int)offset + 1;
        var to = deliveries.Count == 0 ? (int?)null : (int)offset + deliveries.Count;
        return Ok(new
        {
            current_page = currentPage,
            data = deliveries.Select(PresentForList).ToArray(),
            first_page_url = PageUrl(1),
            from,
            last_page = lastPage,
            last_page_url = PageUrl(lastPage),
            links,
            next_page_url = currentPage < lastPage ? PageUrl(currentPage + 1) : null,
            path,
            per_page = pageSize,
            prev_page_url = currentPage > 1 ? PageUrl(currentPage - 1) : null,
            to,
            total
        });
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "deliveries.view")]
    public async Task<IActionResult> Show(Guid id, CancellationToken ct)
    {
        var delivery = await database.Deliveries.AsNoTracking()
            .Include(value => value.Request)
            .Include(value => value.Items)
                .ThenInclude(value => value.Item)
            .SingleOrDefaultAsync(value => value.Id == id, ct);
        if (delivery is null)
            return NotFound();

        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();
        var viewer = await database.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.IndustryId })
            .SingleOrDefaultAsync(ct);
        if (viewer is null)
            return Unauthorized();
        if (viewer.IndustryId is Guid industryId && delivery.IndustryId != industryId)
            return NotFound();

        var deliveredBy = delivery.DeliveredById is Guid deliveredById
            ? await database.Users.AsNoTracking()
                .Where(user => user.Id == deliveredById)
                .Select(user => new { id = user.Id, name = user.FullName })
                .SingleOrDefaultAsync(ct)
            : null;

        return Ok(new
        {
            id = delivery.Id,
            code = delivery.Code,
            type = delivery.Type,
            request_id = delivery.RequestId,
            request = delivery.Request is null
                ? null
                : new { id = delivery.Request.Id, code = delivery.Request.Code, purpose = delivery.Request.Purpose },
            event_id = delivery.EventId,
            industry_id = delivery.IndustryId,
            delivered_by = deliveredBy,
            received_by_name = delivery.ReceivedByName,
            received_by_email = delivery.ReceivedByEmail,
            received_by_document = delivery.ReceivedByDocument,
            received_by_phone = delivery.ReceivedByPhone,
            notes = delivery.Notes,
            created_at = delivery.CreatedAt,
            items = delivery.Items.OrderBy(value => value.Id).Select(value => new
            {
                id = value.Id,
                delivery_id = value.DeliveryId,
                item_id = value.ItemId,
                qty = value.Quantity,
                balance_after = value.BalanceAfter,
                item = new { id = value.Item.Id, code = value.Item.Code, name = value.Item.Name }
            })
        });
    }

    [HttpGet("~/api/deliveries/{id:guid}/signature")]
    [Authorize(Policy = "deliveries.view")]
    public async Task<IActionResult> LegacySignature(Guid id, [FromServices] DigitalSignatureStorage signatures, CancellationToken ct)
    {
        var delivery = await database.Deliveries.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == id, ct);
        if (delivery is null)
            return NotFound();

        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();
        var viewer = await database.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.IndustryId })
            .SingleOrDefaultAsync(ct);
        if (viewer is null)
            return Unauthorized();

        if (viewer.IndustryId is Guid industryId && delivery.IndustryId != industryId)
            return NotFound();

        var signature = signatures.Read(delivery.SignaturePath);
        if (signature is null)
            return NotFound();

        Response.Headers["Cache-Control"] = "private, max-age=86400";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(signature.Bytes, signature.ContentType);
    }

    [HttpGet("{id:guid}/pdf")]
    [Authorize(Policy = "deliveries.view")]
    public async Task<IActionResult> Pdf(Guid id, [FromServices] DigitalSignatureStorage signatures,
        [FromServices] DeliveryPdfGenerator pdfGenerator, [FromServices] DeliveryVerificationService verification, CancellationToken ct)
    {
        var delivery = await database.Deliveries.AsNoTracking()
            .Include(value => value.Request)
            .Include(value => value.Items)
                .ThenInclude(value => value.Item)
            .SingleOrDefaultAsync(value => value.Id == id, ct);
        if (delivery is null)
            return NotFound();

        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();
        var viewer = await database.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.IndustryId })
            .SingleOrDefaultAsync(ct);
        if (viewer is null)
            return Unauthorized();
        if (viewer.IndustryId is Guid industryId && delivery.IndustryId != industryId)
            return NotFound();

        var signature = signatures.Read(delivery.SignaturePath);
        var qr = verification.CreateQr(delivery.Code);
        var bytes = pdfGenerator.Generate(delivery, signature?.Bytes, qr.PngBytes);
        var fileName = string.Concat(delivery.Code.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
        if (string.IsNullOrEmpty(fileName))
            fileName = "protocol";

        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(bytes, "application/pdf", $"{fileName}.pdf");
    }

    [HttpGet("{id:guid}/qr")]
    [Authorize(Policy = "deliveries.view")]
    public async Task<IActionResult> Qr(Guid id, [FromServices] DeliveryVerificationService verification, CancellationToken ct)
    {
        var delivery = await database.Deliveries.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == id, ct);
        if (delivery is null)
            return NotFound();

        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();
        var viewer = await database.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.IndustryId })
            .SingleOrDefaultAsync(ct);
        if (viewer is null)
            return Unauthorized();
        if (viewer.IndustryId is Guid industryId && delivery.IndustryId != industryId)
            return NotFound();

        var qr = verification.CreateQr(delivery.Code);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(new
        {
            code = delivery.Code,
            url = qr.Url,
            data_uri = qr.DataUri,
            format = "svg",
            expires_at = qr.ExpiresAt
        });
    }

    [HttpGet("~/api/v1/verify/{code}")]
    [AllowAnonymous]
    public async Task<IActionResult> Verify(string code, [FromQuery] string? expires, [FromQuery] string? signature,
        [FromServices] DeliveryVerificationService verification, CancellationToken ct)
    {
        if (!verification.IsValid(code, expires, signature))
            return StatusCode(StatusCodes.Status403Forbidden);

        var delivery = await database.Deliveries.AsNoTracking()
            .Where(value => value.Code == code)
            .Select(value => new { value.Code, value.Type, value.CreatedAt })
            .SingleOrDefaultAsync(ct);
        if (delivery is null)
            return NotFound();

        Response.Headers.CacheControl = "no-store";
        return Ok(new
        {
            valid = true,
            protocol = delivery.Code,
            type = delivery.Type,
            created_at = delivery.CreatedAt
        });
    }

    private static object PresentForList(Delivery delivery) => new
    {
        id = delivery.Id,
        code = delivery.Code,
        type = delivery.Type,
        request_id = delivery.RequestId,
        event_id = delivery.EventId,
        industry_id = delivery.IndustryId,
        delivered_by = delivery.DeliveredById,
        received_by_name = delivery.ReceivedByName,
        received_by_email = delivery.ReceivedByEmail,
        received_by_document = delivery.ReceivedByDocument,
        received_by_phone = delivery.ReceivedByPhone,
        request = delivery.Request is null
            ? null
            : new { id = delivery.Request.Id, code = delivery.Request.Code, purpose = delivery.Request.Purpose },
        items = delivery.Items.OrderBy(value => value.Id).Select(value => new
        {
            id = value.Id,
            delivery_id = value.DeliveryId,
            item_id = value.ItemId,
            qty = value.Quantity,
            balance_after = value.BalanceAfter,
            item = new { id = value.Item.Id, code = value.Item.Code, name = value.Item.Name }
        }),
        created_at = delivery.CreatedAt
    };

    private static IEnumerable<int?> PageNumbers(int currentPage, int lastPage)
    {
        if (lastPage <= 7)
        {
            for (var page = 1; page <= lastPage; page++)
                yield return page;
            yield break;
        }

        if (currentPage <= 4)
        {
            for (var page = 1; page <= 7; page++)
                yield return page;
            yield return null;
            yield return lastPage;
            yield break;
        }

        if (currentPage > lastPage - 4)
        {
            yield return 1;
            yield return null;
            for (var page = lastPage - 6; page <= lastPage; page++)
                yield return page;
            yield break;
        }

        yield return 1;
        yield return null;
        for (var page = currentPage - 3; page <= currentPage + 3; page++)
            yield return page;
        yield return null;
        yield return lastPage;
    }

    private sealed record PageLink(
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("active")] bool Active);
}
