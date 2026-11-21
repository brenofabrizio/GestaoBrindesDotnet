using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gbrindes.Web.Data;
using Gbrindes.Web.Inventory;
using Gbrindes.Web.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/events")]
public sealed class EventsController(ApplicationDbContext database, EventWorkflowService workflows, EventWithdrawalService withdrawals, EventReturnService returns) : ControllerBase
{
    private const int PageSize = 25;

    [HttpPost("{id:guid}/returns")]
    public async Task<IActionResult> ReturnToCd(Guid id, [FromBody] JsonElement body, CancellationToken ct)
    {
        if (!(User.IsInRole("admin") || User.HasClaim("permission", "events.manage") || User.HasClaim("permission", "stock.transfer")))
            return Forbid();
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();
        if (!body.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return ValidationFailure("items", "Informe o que está voltando para o CD.");

        var lines = new List<EventReturnLine>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("industry_id", out var industryValue)
                || ParseGuid(industryValue) is not Guid industryId || !item.TryGetProperty("item_id", out var itemValue)
                || ParseGuid(itemValue) is not Guid itemId || !item.TryGetProperty("qty", out var quantityValue)
                || !TryParseInteger(quantityValue, out var quantity))
                return ValidationFailure("items", "Informe indústria, brinde e quantidade válidos.");
            lines.Add(new EventReturnLine(industryId, itemId, quantity));
        }

        try
        {
            await returns.ReturnAsync(id, userId, User.Identity?.Name, HttpContext.Connection.RemoteIpAddress?.ToString(),
                lines, Request.Headers["Idempotency-Key"].ToString(), ct);
            return await Show(id, ct);
        }
        catch (InventoryFailureException failure)
        {
            return StatusCode(failure.StatusCode, new { error = failure.Code, message = failure.Message });
        }
    }

    [HttpPost("{id:guid}/withdrawals")]
    [Authorize(Policy = "events.withdraw")]
    public async Task<IActionResult> Withdraw(Guid id, [FromBody] EventWithdrawalRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        var industryId = ParseGuid(request.IndustryId);
        if (industryId is null) return ValidationFailure("industry_id", "Informe uma indústria válida.");
        var assignedIndustry = await database.Users.AsNoTracking().Where(user => user.Id == userId)
            .Select(user => user.IndustryId).SingleOrDefaultAsync(ct);
        if (assignedIndustry is Guid scope && scope != industryId)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "INDUSTRY_FORBIDDEN", message = "Você só pode retirar pela sua indústria." });
        if (request.Items.ValueKind != JsonValueKind.Array)
            return ValidationFailure("items", "Informe ao menos um item para retirada.");
        var lines = new List<EventWithdrawalLine>();
        foreach (var row in request.Items.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("item_id", out var itemValue)
                || ParseGuid(itemValue) is not Guid itemId)
                return ValidationFailure("items", "Informe itens e quantidades inteiros válidos.");
            JsonElement qtyValue;
            if (!row.TryGetProperty("qty", out qtyValue) && !row.TryGetProperty("quantity", out qtyValue)
                || !TryParseInteger(qtyValue, out var quantity))
                return ValidationFailure("items", "Informe itens e quantidades inteiros válidos.");
            lines.Add(new EventWithdrawalLine(itemId, quantity));
        }
        try
        {
            var result = await withdrawals.WithdrawAsync(id, userId, industryId.Value, request.ReceivedByName ?? "",
                request.ReceivedByEmail, request.ReceivedByDocument, request.Notes, request.Signature ?? "", lines,
                Request.Headers["Idempotency-Key"].ToString(), User.Identity?.Name, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
            var payload = new
            {
                id = result.Delivery.Id,
                code = result.Delivery.Code,
                type = result.Delivery.Type,
                event_id = result.Delivery.EventId,
                industry_id = result.Delivery.IndustryId,
                received_by_name = result.Delivery.ReceivedByName,
                received_by_email = result.Delivery.ReceivedByEmail,
                received_by_document = result.Delivery.ReceivedByDocument,
                notes = result.Delivery.Notes,
                items = result.Delivery.Items.Select(item => new { item_id = item.ItemId, qty = item.Quantity, balance_after = item.BalanceAfter })
            };
            return StatusCode(result.Replayed ? StatusCodes.Status200OK : StatusCodes.Status201Created, new { data = payload });
        }
        catch (EventWithdrawalFailureException failure)
        {
            return StatusCode(failure.StatusCode, new { error = failure.Code, message = failure.Message });
        }
        catch (DigitalSignatureValidationException failure)
        {
            return ValidationFailure("signature", failure.Message);
        }
    }

    [HttpGet("~/api/events")]
    [Authorize(Policy = "events.view")]
    public async Task<IActionResult> LegacyList(CancellationToken ct)
    {
        var query = database.Events.AsNoTracking().Where(eventRecord => eventRecord.DeletedAt == null);
        var search = Request.Query["q"].ToString();
        if (search.Length > 0)
        {
            var normalizedSearch = search.ToLowerInvariant();
            query = query.Where(eventRecord =>
                eventRecord.Name.ToLower().Contains(normalizedSearch)
                || (eventRecord.Venue != null && eventRecord.Venue.ToLower().Contains(normalizedSearch)));
        }

        var status = Request.Query["status"].ToString();
        if (status is "planejado" or "aberto" or "encerrado" or "cancelado")
            query = query.Where(eventRecord => eventRecord.Status == status);

        var rawPage = Request.Query["page"].ToString();
        var currentPage = int.TryParse(rawPage, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPage) && parsedPage > 0
            ? parsedPage
            : 1;
        var rawPerPage = Request.Query["per_page"].ToString();
        var requestedPageSize = string.IsNullOrEmpty(rawPerPage) || rawPerPage == "0"
            ? 25
            : int.TryParse(rawPerPage, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPageSize) ? parsedPageSize : 0;
        var perPage = Math.Clamp(requestedPageSize, 1, 100);
        var total = await query.CountAsync(ct);
        var lastPage = Math.Max(1, (int)Math.Ceiling(total / (double)perPage));
        var offset = ((long)currentPage - 1) * perPage;
        var rows = offset > int.MaxValue
            ? []
            : await query
                .OrderByDescending(eventRecord => eventRecord.StartsOn != null)
                .ThenByDescending(eventRecord => eventRecord.StartsOn)
                .ThenByDescending(eventRecord => eventRecord.Id)
                .Skip((int)offset)
                .Take(perPage)
                .Select(eventRecord => new
                {
                    id = eventRecord.Id,
                    name = eventRecord.Name,
                    description = eventRecord.Description,
                    venue = eventRecord.Venue,
                    starts_on = eventRecord.StartsOn,
                    ends_on = eventRecord.EndsOn,
                    status = eventRecord.Status,
                    status_label = eventRecord.Status == "planejado" ? "Planejado"
                        : eventRecord.Status == "aberto" ? "Aberto"
                        : eventRecord.Status == "encerrado" ? "Encerrado"
                        : eventRecord.Status == "cancelado" ? "Cancelado" : eventRecord.Status,
                    notes = eventRecord.Notes,
                    opened_at = eventRecord.OpenedAt,
                    closed_at = eventRecord.ClosedAt,
                    location_id = eventRecord.LocationId,
                    created_at = eventRecord.CreatedAt
                })
                .ToListAsync(ct);

        return Ok(new
        {
            ok = true,
            data = rows,
            meta = new { page = currentPage, per_page = perPage, total, last_page = lastPage }
        });
    }

    [HttpGet("~/api/events/{id:guid}")]
    [Authorize(Policy = "events.view")]
    public async Task<IActionResult> LegacyShow(Guid id, CancellationToken ct)
    {
        var eventRecord = await database.Events.AsNoTracking()
            .Where(value => value.Id == id && value.DeletedAt == null)
            .Select(value => new
            {
                value.Id,
                value.Name,
                value.Description,
                value.Venue,
                value.StartsOn,
                value.EndsOn,
                value.Status,
                value.Notes,
                value.OpenedAt,
                value.ClosedAt,
                value.LocationId,
                value.CreatedAt
            })
            .SingleOrDefaultAsync(ct);
        if (eventRecord is null)
            return NotFound(new { ok = false, error = new { code = "NOT_FOUND", message = "Evento não encontrado." } });

        Guid? scopedIndustryId = null;
        if (Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var viewerId))
            scopedIndustryId = await database.Users.AsNoTracking()
                .Where(user => user.Id == viewerId)
                .Select(user => user.IndustryId)
                .SingleOrDefaultAsync(ct);

        var allocations = await (from allocation in database.EventAllocations.AsNoTracking()
                                 join industry in database.Industries.AsNoTracking() on allocation.IndustryId equals industry.Id
                                 join item in database.CatalogItems.AsNoTracking() on allocation.ItemId equals item.Id
                                 where allocation.EventId == id
                                     && (!scopedIndustryId.HasValue || allocation.IndustryId == scopedIndustryId.Value)
                                 orderby industry.Name, item.Name
                                 select new
                                 {
                                     industry = new { id = industry.Id, name = industry.Name },
                                     item = new { id = item.Id, code = item.Code, name = item.Name },
                                     qty_allocated = allocation.QuantityAllocated,
                                     qty_withdrawn = allocation.QuantityWithdrawn,
                                     saldo = allocation.QuantityAllocated - allocation.QuantityWithdrawn
                                 })
            .ToListAsync(ct);

        var industries = await (from allocation in database.EventAllocations.AsNoTracking()
                                join industry in database.Industries.AsNoTracking() on allocation.IndustryId equals industry.Id
                                where allocation.EventId == id
                                    && (!scopedIndustryId.HasValue || allocation.IndustryId == scopedIndustryId.Value)
                                group allocation by new { industry.Id, industry.Name } into rows
                                orderby rows.Key.Name
                                select new
                                {
                                    id = rows.Key.Id,
                                    name = rows.Key.Name,
                                    allocated = rows.Sum(value => value.QuantityAllocated),
                                    withdrawn = rows.Sum(value => value.QuantityWithdrawn),
                                    saldo = rows.Sum(value => value.QuantityAllocated - value.QuantityWithdrawn)
                                })
            .ToListAsync(ct);

        return Ok(new
        {
            ok = true,
            data = new
            {
                id = eventRecord.Id,
                name = eventRecord.Name,
                description = eventRecord.Description,
                venue = eventRecord.Venue,
                starts_on = eventRecord.StartsOn,
                ends_on = eventRecord.EndsOn,
                status = eventRecord.Status,
                status_label = eventRecord.Status == "planejado" ? "Planejado"
                    : eventRecord.Status == "aberto" ? "Aberto"
                    : eventRecord.Status == "encerrado" ? "Encerrado"
                    : eventRecord.Status == "cancelado" ? "Cancelado" : eventRecord.Status,
                notes = eventRecord.Notes,
                opened_at = eventRecord.OpenedAt,
                closed_at = eventRecord.ClosedAt,
                location_id = eventRecord.LocationId,
                created_at = eventRecord.CreatedAt,
                allocations,
                industries,
                returns = allocations.Where(allocation => allocation.saldo > 0).ToList()
            }
        });
    }

    [HttpGet("~/api/events/{id:guid}/balance")]
    [Authorize(Policy = "events.view")]
    public async Task<IActionResult> LegacyIndustryBalance(Guid id, CancellationToken ct)
    {
        var rawIndustryId = Request.Query["industry_id"].ToString();
        if (!Guid.TryParse(rawIndustryId, out var industryId))
        {
            return UnprocessableEntity(new
            {
                ok = false,
                error = new
                {
                    code = "VALIDATION_ERROR",
                    message = "Verifique os campos destacados.",
                    fields = new { industry_id = "Informe a indústria." }
                }
            });
        }

        if (Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var balanceViewerId))
        {
            var assignedIndustryId = await database.Users.AsNoTracking()
                .Where(user => user.Id == balanceViewerId)
                .Select(user => user.IndustryId)
                .SingleOrDefaultAsync(ct);
            if (assignedIndustryId is Guid scopedIndustryId && scopedIndustryId != industryId)
                return Forbid();
        }

        var rows = await (from allocation in database.EventAllocations.AsNoTracking()
                          join item in database.CatalogItems.AsNoTracking() on allocation.ItemId equals item.Id
                          where allocation.EventId == id && allocation.IndustryId == industryId
                          orderby item.Name
                          select new
                          {
                              ItemId = item.Id,
                              item.Code,
                              item.Name,
                              item.PhotoPath,
                              item.ThumbPath,
                              allocation.QuantityAllocated,
                              allocation.QuantityWithdrawn
                          }).ToListAsync(ct);

        var data = rows.Select(row => new
        {
            item = new
            {
                id = row.ItemId,
                code = row.Code,
                name = row.Name,
                thumb_url = ThumbUrl(row.ItemId, row.PhotoPath, row.ThumbPath)
            },
            qty_allocated = row.QuantityAllocated,
            qty_withdrawn = row.QuantityWithdrawn,
            saldo = row.QuantityAllocated - row.QuantityWithdrawn
        }).ToList();

        return Ok(new { ok = true, data });
    }

    [HttpGet]
    [Authorize(Policy = "events.view")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var query = database.Events.AsNoTracking().Where(eventRecord => eventRecord.DeletedAt == null);
        var total = await query.CountAsync(ct);
        var rawPage = Request.Query["page"].ToString();
        var currentPage = int.TryParse(rawPage, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPage) && parsedPage > 0
            ? parsedPage
            : 1;
        var lastPage = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        var path = $"{Request.Scheme}://{Request.Host}{Request.PathBase}{Request.Path}";
        var offset = (long)(currentPage - 1) * PageSize;
        List<EventResponse> events;
        if (offset > int.MaxValue)
        {
            events = [];
        }
        else
        {
            events = await query
                .OrderByDescending(eventRecord => eventRecord.CreatedAt)
                .ThenByDescending(eventRecord => eventRecord.Id)
                .Skip((int)offset)
                .Take(PageSize)
                .Select(eventRecord => new EventResponse(
                    eventRecord.Id,
                    eventRecord.Name,
                    eventRecord.Description,
                    eventRecord.Venue,
                    eventRecord.LocationId,
                    eventRecord.StartsOn,
                    eventRecord.EndsOn,
                    eventRecord.Status,
                    eventRecord.Notes,
                    eventRecord.OpenedAt,
                    eventRecord.ClosedAt,
                    eventRecord.CreatedAt,
                    eventRecord.UpdatedAt,
                    eventRecord.DeletedAt,
                    database.EventAllocations.Count(allocation => allocation.EventId == eventRecord.Id)))
                .ToListAsync(ct);
        }

        string PageUrl(int page) => $"{path}?page={page}";
        var links = new List<PageLink> { new(currentPage > 1 ? PageUrl(currentPage - 1) : null, "&laquo; Previous", false) };
        foreach (var page in PageNumbers(currentPage, lastPage))
            links.Add(page is int number
                ? new PageLink(PageUrl(number), number.ToString(CultureInfo.InvariantCulture), number == currentPage)
                : new PageLink(null, "...", false));
        links.Add(new(currentPage < lastPage ? PageUrl(currentPage + 1) : null, "Next &raquo;", false));

        var from = events.Count == 0 ? (int?)null : (int)offset + 1;
        var to = events.Count == 0 ? (int?)null : (int)offset + events.Count;
        return Ok(new
        {
            current_page = currentPage,
            data = events,
            first_page_url = PageUrl(1),
            from,
            last_page = lastPage,
            last_page_url = PageUrl(lastPage),
            links,
            next_page_url = currentPage < lastPage ? PageUrl(currentPage + 1) : null,
            path,
            per_page = PageSize,
            prev_page_url = currentPage > 1 ? PageUrl(currentPage - 1) : null,
            to,
            total
        });
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "events.view")]
    public async Task<IActionResult> Show(Guid id, CancellationToken ct)
    {
        var eventResponse = await database.Events.AsNoTracking()
            .Where(eventRecord => eventRecord.Id == id && eventRecord.DeletedAt == null)
            .Select(eventRecord => new EventResponse(
                eventRecord.Id,
                eventRecord.Name,
                eventRecord.Description,
                eventRecord.Venue,
                eventRecord.LocationId,
                eventRecord.StartsOn,
                eventRecord.EndsOn,
                eventRecord.Status,
                eventRecord.Notes,
                eventRecord.OpenedAt,
                eventRecord.ClosedAt,
                eventRecord.CreatedAt,
                eventRecord.UpdatedAt,
                eventRecord.DeletedAt))
            .SingleOrDefaultAsync(ct);
        if (eventResponse is null)
            return NotFound();

        Guid? scopedIndustryId = null;
        if (Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var viewerId))
            scopedIndustryId = await database.Users.AsNoTracking()
                .Where(user => user.Id == viewerId)
                .Select(user => user.IndustryId)
                .SingleOrDefaultAsync(ct);

        var allocations = await database.EventAllocations.AsNoTracking()
            .Where(allocation => allocation.EventId == id
                && (!scopedIndustryId.HasValue || allocation.IndustryId == scopedIndustryId.Value))
            .OrderBy(allocation => allocation.CreatedAt)
            .ThenBy(allocation => allocation.Id)
            .Select(allocation => new EventAllocationResponse(
                allocation.Id,
                allocation.EventId,
                allocation.IndustryId,
                allocation.ItemId,
                allocation.QuantityAllocated,
                allocation.QuantityWithdrawn,
                allocation.CreatedAt,
                allocation.UpdatedAt))
            .ToListAsync(ct);

        var industries = await (from allocation in database.EventAllocations.AsNoTracking()
                                join industry in database.Industries.AsNoTracking() on allocation.IndustryId equals industry.Id
                                where allocation.EventId == id
                                    && (!scopedIndustryId.HasValue || allocation.IndustryId == scopedIndustryId.Value)
                                group allocation by new { industry.Id, industry.Name } into industryAllocations
                                orderby industryAllocations.Key.Name, industryAllocations.Key.Id
                                select new EventIndustryBalanceResponse(
                                    industryAllocations.Key.Id,
                                    industryAllocations.Key.Name,
                                    industryAllocations.Sum(allocation => allocation.QuantityAllocated),
                                    industryAllocations.Sum(allocation => allocation.QuantityWithdrawn),
                                    industryAllocations.Sum(allocation => allocation.QuantityAllocated - allocation.QuantityWithdrawn)))
            .ToListAsync(ct);

        var returns = await (from allocation in database.EventAllocations.AsNoTracking()
                             join industry in database.Industries.AsNoTracking() on allocation.IndustryId equals industry.Id
                             join item in database.CatalogItems.AsNoTracking() on allocation.ItemId equals item.Id
                             where allocation.EventId == id && allocation.QuantityAllocated > allocation.QuantityWithdrawn
                                 && (!scopedIndustryId.HasValue || allocation.IndustryId == scopedIndustryId.Value)
                             orderby industry.Name, industry.Id, item.Name, item.Id
                             select new EventReturnResponse(
                                 new EventIndustryReference(industry.Id, industry.Name),
                                 new EventItemReference(item.Id, item.Code, item.Name),
                                 allocation.QuantityAllocated,
                                 allocation.QuantityWithdrawn,
                                 allocation.QuantityAllocated - allocation.QuantityWithdrawn))
            .ToListAsync(ct);

        return Ok(eventResponse with { Allocations = allocations, Industries = industries, Returns = returns });
    }

    [HttpPost("{id:guid}/open")]
    [Authorize(Policy = "events.manage")]
    public async Task<IActionResult> Open(Guid id, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();
        try
        {
            await workflows.OpenAsync(id, userId, User.Identity?.Name, ct);
            return await Show(id, ct);
        }
        catch (InventoryFailureException failure)
        {
            return StatusCode(failure.StatusCode, new { error = failure.Code, message = failure.Message });
        }
    }

    [HttpPost("{id:guid}/close")]
    [Authorize(Policy = "events.manage")]
    public async Task<IActionResult> Close(Guid id, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();
        try
        {
            await workflows.CloseAsync(id, userId, User.Identity?.Name, ct);
            return await Show(id, ct);
        }
        catch (InventoryFailureException failure)
        {
            return StatusCode(failure.StatusCode, new { error = failure.Code, message = failure.Message });
        }
    }

    [HttpPost("~/api/events/{id:guid}/open")]
    [Authorize(Policy = "events.manage")]
    public async Task<IActionResult> LegacyOpen(Guid id, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();
        try
        {
            await workflows.OpenAsync(id, userId, User.Identity?.Name, ct);
            return await LegacyShow(id, ct);
        }
        catch (InventoryFailureException failure)
        {
            return LegacyFailure(failure.StatusCode, failure.Code, failure.Message);
        }
    }

    [HttpPost("~/api/events/{id:guid}/close")]
    [Authorize(Policy = "events.manage")]
    public async Task<IActionResult> LegacyClose(Guid id, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();
        try
        {
            await workflows.CloseAsync(id, userId, User.Identity?.Name, ct);
            return await LegacyShow(id, ct);
        }
        catch (InventoryFailureException failure)
        {
            return LegacyFailure(failure.StatusCode, failure.Code, failure.Message);
        }
    }

    [HttpGet("{id:guid}/balance")]
    [Authorize(Policy = "events.view")]
    public async Task<IActionResult> IndustryBalance(Guid id, CancellationToken ct)
    {
        if (!await database.Events.AnyAsync(eventRecord => eventRecord.Id == id && eventRecord.DeletedAt == null, ct))
            return NotFound();

        var rawIndustryId = Request.Query["industry_id"].ToString();
        var requestedIndustryId = Guid.TryParse(rawIndustryId, out var parsedIndustryId)
            ? (Guid?)parsedIndustryId
            : null;
        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var assignedIndustryId = Guid.TryParse(userIdValue, out var userId)
            ? await database.Users.AsNoTracking().Where(user => user.Id == userId).Select(user => user.IndustryId).SingleOrDefaultAsync(ct)
            : null;
        var industryId = assignedIndustryId ?? requestedIndustryId;
        if (industryId is null)
            return ValidationFailure("industry_id", "Informe a indústria.");
        if (!await database.Industries.AnyAsync(industry => industry.Id == industryId, ct))
            return ValidationFailure("industry_id", "The selected industry_id is invalid.");

        var rows = await (from allocation in database.EventAllocations.AsNoTracking()
                          join item in database.CatalogItems.AsNoTracking() on allocation.ItemId equals item.Id
                          where allocation.EventId == id && allocation.IndustryId == industryId
                          orderby item.Name
                          select new
                          {
                              ItemId = item.Id,
                              item.Code,
                              item.Name,
                              item.PhotoPath,
                              item.ThumbPath,
                              allocation.QuantityAllocated,
                              allocation.QuantityWithdrawn
                          }).ToListAsync(ct);

        return Ok(rows.Select(row => new EventBalanceRow(
            new EventBalanceItem(row.ItemId, row.Code, row.Name, ThumbUrl(row.ItemId, row.PhotoPath, row.ThumbPath)),
            row.QuantityAllocated,
            row.QuantityWithdrawn,
            row.QuantityAllocated - row.QuantityWithdrawn)));
    }

    [HttpPut("{id:guid}/allocations")]
    [Authorize(Policy = "events.manage")]
    public async Task<IActionResult> ReplaceAllocations(Guid id, [FromBody] JsonElement body, CancellationToken ct)
        => await ReplaceAllocationsCore(id, body, ct, legacyContract: false);

    [HttpPut("~/api/events/{id:guid}/allocations")]
    [Authorize(Policy = "events.manage")]
    public async Task<IActionResult> LegacyReplaceAllocations(Guid id, [FromBody] JsonElement body, CancellationToken ct)
        => await ReplaceAllocationsCore(id, body, ct, legacyContract: true);

    private async Task<IActionResult> ReplaceAllocationsCore(Guid id, JsonElement body, CancellationToken ct, bool legacyContract)
    {
        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(ct)
            : null;
        Event? eventRecord;
        if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            eventRecord = await database.Events
                .FromSqlInterpolated($"SELECT * FROM \"Events\" WHERE \"Id\" = {id} AND \"DeletedAt\" IS NULL FOR UPDATE")
                .SingleOrDefaultAsync(ct);
        }
        else
        {
            eventRecord = await database.Events.SingleOrDefaultAsync(
                value => value.Id == id && value.DeletedAt == null, ct);
        }

        if (eventRecord is null)
            return legacyContract
                ? LegacyFailure(StatusCodes.Status404NotFound, "NOT_FOUND", "Evento não encontrado.")
                : NotFound();
        if (eventRecord.Status != "planejado")
            return legacyContract
                ? LegacyFailure(StatusCodes.Status422UnprocessableEntity, "EVENT_NOT_PLANNED", "As cotas só podem ser alteradas enquanto o evento está planejado.")
                : UnprocessableEntity(new { error = "EVENT_NOT_PLANNED", message = "As cotas só podem ser alteradas enquanto o evento está planejado." });

        var rows = body;
        if (body.ValueKind == JsonValueKind.Object)
        {
            rows = body.TryGetProperty("allocations", out var allocationProperty)
                ? allocationProperty
                : default;
        }
        if (rows.ValueKind != JsonValueKind.Array)
            return legacyContract
                ? LegacyValidationFailure(new Dictionary<string, string> { ["allocations"] = "Informe uma lista de cotas." })
                : UnprocessableEntity(new { message = "The given data was invalid.", errors = new { allocations = new[] { "Informe uma lista de cotas." } } });

        var errors = new Dictionary<string, string[]>();
        var parsedRows = new List<(Guid IndustryId, Guid ItemId, int Quantity)>();
        var keys = new HashSet<(Guid IndustryId, Guid ItemId)>();
        var index = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object
                || !row.TryGetProperty("industry_id", out var industryValue)
                || ParseGuid(industryValue) is not Guid industryId
                || !row.TryGetProperty("item_id", out var itemValue)
                || ParseGuid(itemValue) is not Guid itemId
                || !row.TryGetProperty("qty_allocated", out var quantityValue)
                || !TryParseInteger(quantityValue, out var quantity)
                || quantity < 0 || quantity > 1_000_000)
            {
                var field = $"allocations.{index}";
                var message = "Informe indústria, brinde e cota inteira entre 0 e 1000000.";
                if (legacyContract && row.ValueKind == JsonValueKind.Object
                    && row.TryGetProperty("industry_id", out var legacyIndustryValue)
                    && ParseGuid(legacyIndustryValue) is Guid
                    && row.TryGetProperty("item_id", out var legacyItemValue)
                    && ParseGuid(legacyItemValue) is Guid
                    && row.TryGetProperty("qty_allocated", out var legacyQuantityValue))
                {
                    if (!TryParseInteger(legacyQuantityValue, out var legacyQuantity))
                    {
                        field = "qty_allocated";
                        message = "Informe um número inteiro.";
                    }
                    else if (legacyQuantity < 0)
                    {
                        field = "qty_allocated";
                        message = "Deve ser no mínimo 0.";
                    }
                    else if (legacyQuantity > 1_000_000)
                    {
                        field = "qty_allocated";
                        message = "Deve ser no máximo 1000000.";
                    }
                }
                errors[field] = [message];
                index++;
                continue;
            }

            if (!keys.Add((industryId, itemId)))
                errors[$"allocations.{index}"] = ["A combinação de indústria e brinde não pode se repetir."];
            else
                parsedRows.Add((industryId, itemId, quantity));
            index++;
        }

        foreach (var row in parsedRows)
        {
            if (!await database.Industries.AnyAsync(industry => industry.Id == row.IndustryId, ct))
                errors["industry_id"] = ["The selected industry_id is invalid."];
            if (!await database.CatalogItems.AnyAsync(item => item.Id == row.ItemId, ct))
                errors["item_id"] = ["The selected item_id is invalid."];
        }
        if (errors.Count > 0)
            return legacyContract
                ? LegacyValidationFailure(errors.ToDictionary(pair => pair.Key, pair => pair.Value[0]))
                : UnprocessableEntity(new { message = "The given data was invalid.", errors });

        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        var existing = await database.EventAllocations.Where(allocation => allocation.EventId == id).ToListAsync(ct);
        var existingByKey = existing.ToDictionary(allocation => (allocation.IndustryId, allocation.ItemId));
        var retainedKeys = new HashSet<(Guid IndustryId, Guid ItemId)>();
        var now = DateTimeOffset.UtcNow;
        foreach (var row in parsedRows)
        {
            if (row.Quantity == 0)
                continue;
            var key = (row.IndustryId, row.ItemId);
            retainedKeys.Add(key);
            if (existingByKey.TryGetValue(key, out var allocation))
            {
                allocation.QuantityAllocated = row.Quantity;
                allocation.QuantityWithdrawn = 0;
                allocation.UpdatedAt = now;
            }
            else
            {
                database.EventAllocations.Add(new EventAllocation
                {
                    EventId = id,
                    IndustryId = row.IndustryId,
                    ItemId = row.ItemId,
                    QuantityAllocated = row.Quantity,
                    QuantityWithdrawn = 0,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }

        database.EventAllocations.RemoveRange(existing.Where(allocation => !retainedKeys.Contains((allocation.IndustryId, allocation.ItemId))));
        database.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            UserName = User.Identity?.Name,
            Action = "update",
            EntityType = "event",
            EntityId = id,
            EntityLabel = eventRecord.Name,
            AfterJson = JsonSerializer.Serialize(new { allocations = rows.GetArrayLength() }),
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Request.Headers.UserAgent.ToString(),
            CreatedAt = now
        });

        await database.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return legacyContract ? await LegacyShow(id, ct) : await Show(id, ct);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "events.manage")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateEventRequest request, CancellationToken ct)
    {
        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(ct)
            : null;
        Event? eventRecord;
        if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            eventRecord = await database.Events
                .FromSqlInterpolated($"SELECT * FROM \"Events\" WHERE \"Id\" = {id} AND \"DeletedAt\" IS NULL FOR UPDATE")
                .SingleOrDefaultAsync(ct);
        }
        else
        {
            eventRecord = await database.Events.SingleOrDefaultAsync(
                value => value.Id == id && value.DeletedAt == null, ct);
        }

        if (eventRecord is null)
            return NotFound();
        if (eventRecord.Status == "encerrado")
            return UnprocessableEntity(new { error = "EVENT_CLOSED", message = "Evento encerrado não pode ser editado." });

        var errors = new Dictionary<string, string[]>();
        var name = ReadOptionalString(request.Name, "name", 150, eventRecord.Name, required: true, errors);
        var description = ReadOptionalString(request.Description, "description", 2000, eventRecord.Description, required: false, errors);
        var venue = ReadOptionalString(request.Venue, "venue", 190, eventRecord.Venue, required: false, errors);
        var notes = ReadOptionalString(request.Notes, "notes", 2000, eventRecord.Notes, required: false, errors);
        var startsOn = ReadOptionalDate(request.StartsOn, "starts_on", eventRecord.StartsOn, errors);
        var endsOn = ReadOptionalDate(request.EndsOn, "ends_on", eventRecord.EndsOn, errors);
        if (errors.Count > 0)
            return UnprocessableEntity(new { message = "The given data was invalid.", errors });

        var before = EventAuditValues(eventRecord);
        if (request.Name.ValueKind != JsonValueKind.Undefined) eventRecord.Name = name!;
        if (request.Description.ValueKind != JsonValueKind.Undefined) eventRecord.Description = description;
        if (request.Venue.ValueKind != JsonValueKind.Undefined) eventRecord.Venue = venue;
        if (request.Notes.ValueKind != JsonValueKind.Undefined) eventRecord.Notes = notes;
        if (request.StartsOn.ValueKind != JsonValueKind.Undefined) eventRecord.StartsOn = startsOn;
        if (request.EndsOn.ValueKind != JsonValueKind.Undefined) eventRecord.EndsOn = endsOn;

        var after = EventAuditValues(eventRecord);
        var changedKeys = before.Keys.Where(key => !Equals(before[key], after[key])).ToArray();
        if (changedKeys.Length > 0)
        {
            if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                return Unauthorized();
            eventRecord.UpdatedAt = DateTimeOffset.UtcNow;
            eventRecord.UpdatedBy = userId;
            database.AuditLogs.Add(new AuditLog
            {
                UserId = userId,
                UserName = User.Identity?.Name,
                Action = "update",
                EntityType = "event",
                EntityId = eventRecord.Id,
                EntityLabel = eventRecord.Name,
                BeforeJson = JsonSerializer.Serialize(changedKeys.ToDictionary(key => key, key => before[key])),
                AfterJson = JsonSerializer.Serialize(changedKeys.ToDictionary(key => key, key => after[key])),
                Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = Request.Headers.UserAgent.ToString(),
                CreatedAt = eventRecord.UpdatedAt
            });
        }

        await database.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return await Show(id, ct);
    }

    [HttpPost("{id:guid}/allocations")]
    [Authorize(Policy = "events.manage")]
    public async Task<IActionResult> Allocate(Guid id, [FromBody] AllocateEventRequest request, CancellationToken ct)
    {
        var initialStatus = await database.Events.AsNoTracking()
            .Where(eventRecord => eventRecord.Id == id && eventRecord.DeletedAt == null)
            .Select(eventRecord => eventRecord.Status)
            .SingleOrDefaultAsync(ct);
        if (initialStatus is null)
            return NotFound();
        if (initialStatus is not ("planejado" or "aberto"))
            return UnprocessableEntity(new { message = "O evento não está disponível para cotas." });

        var errors = new Dictionary<string, string[]>();
        var industryId = ParseGuid(request.IndustryId);
        if (industryId is null)
            errors["industry_id"] = request.IndustryId.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? ["The industry_id field is required."]
                : ["The industry_id must be a valid UUID."];

        var itemId = ParseGuid(request.ItemId);
        if (itemId is null)
            errors["item_id"] = request.ItemId.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? ["The item_id field is required."]
                : ["The item_id must be a valid UUID."];

        int? quantityAllocated = TryParseInteger(request.QuantityAllocated, out var parsedQuantity)
            ? parsedQuantity
            : null;
        if (quantityAllocated is null)
            errors["qty_allocated"] = request.QuantityAllocated.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? ["The qty_allocated field is required."]
                : ["The qty_allocated field must be an integer."];
        else if (quantityAllocated < 1)
            errors["qty_allocated"] = ["The qty_allocated field must be at least 1."];

        if (errors.Count > 0)
            return UnprocessableEntity(new { message = "The given data was invalid.", errors });

        if (!await database.Industries.AnyAsync(industry => industry.Id == industryId, ct))
            errors["industry_id"] = ["The selected industry_id is invalid."];
        if (!await database.CatalogItems.AnyAsync(item => item.Id == itemId, ct))
            errors["item_id"] = ["The selected item_id is invalid."];
        if (errors.Count > 0)
            return UnprocessableEntity(new { message = "The given data was invalid.", errors });

        await using var transaction = database.Database.IsRelational()
            ? await database.Database.BeginTransactionAsync(ct)
            : null;
        Event? eventRecord;
        if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            eventRecord = await database.Events
                .FromSqlInterpolated($"SELECT * FROM \"Events\" WHERE \"Id\" = {id} AND \"DeletedAt\" IS NULL FOR UPDATE")
                .SingleOrDefaultAsync(ct);
        }
        else
        {
            eventRecord = await database.Events.SingleOrDefaultAsync(
                value => value.Id == id && value.DeletedAt == null, ct);
        }

        if (eventRecord is null)
            return NotFound();
        if (eventRecord.Status is not ("planejado" or "aberto"))
            return UnprocessableEntity(new { message = "O evento não está disponível para cotas." });

        var allocation = await database.EventAllocations.SingleOrDefaultAsync(value =>
            value.EventId == id && value.IndustryId == industryId && value.ItemId == itemId, ct);
        if (allocation is not null && quantityAllocated!.Value < allocation.QuantityWithdrawn)
            return UnprocessableEntity(new { message = "A cota não pode ser menor que o já retirado." });

        var now = DateTimeOffset.UtcNow;
        if (allocation is null)
        {
            allocation = new EventAllocation
            {
                EventId = id,
                IndustryId = industryId!.Value,
                ItemId = itemId!.Value,
                QuantityAllocated = quantityAllocated!.Value,
                QuantityWithdrawn = 0,
                CreatedAt = now,
                UpdatedAt = now
            };
            database.EventAllocations.Add(allocation);
        }
        else
        {
            allocation.QuantityAllocated = quantityAllocated!.Value;
            allocation.UpdatedAt = now;
        }

        await database.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return StatusCode(StatusCodes.Status201Created, new EventAllocationResponse(
            allocation.Id,
            allocation.EventId,
            allocation.IndustryId,
            allocation.ItemId,
            allocation.QuantityAllocated,
            allocation.QuantityWithdrawn,
            allocation.CreatedAt,
            allocation.UpdatedAt));
    }

    [HttpPost]
    [Authorize(Policy = "events.manage")]
    public async Task<IActionResult> Create([FromBody] CreateEventRequest request, CancellationToken ct)
    {
        if (request.StartsOn.HasValue && request.EndsOn.HasValue && request.EndsOn < request.StartsOn)
            return ValidationFailure("ends_on", "The ends_on field must be a date after or equal to starts_on.");

        if (request.LocationId is Guid locationId && !await database.Locations.AnyAsync(location => location.Id == locationId, ct))
            return ValidationFailure("location_id", "The selected location_id is invalid.");

        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        var now = DateTimeOffset.UtcNow;
        var eventRecord = new Event
        {
            Name = request.Name.Trim(),
            Description = request.Description,
            Venue = request.Venue,
            LocationId = request.LocationId,
            StartsOn = request.StartsOn,
            EndsOn = request.EndsOn,
            Notes = request.Notes,
            Status = "planejado",
            CreatedBy = userId,
            UpdatedBy = userId,
            CreatedAt = now,
            UpdatedAt = now
        };
        var audit = new AuditLog
        {
            UserId = userId,
            UserName = User.Identity?.Name,
            Action = "create",
            EntityType = "event",
            EntityId = eventRecord.Id,
            EntityLabel = eventRecord.Name,
            AfterJson = JsonSerializer.Serialize(new
            {
                name = eventRecord.Name,
                description = eventRecord.Description,
                venue = eventRecord.Venue,
                location_id = eventRecord.LocationId,
                starts_on = eventRecord.StartsOn,
                ends_on = eventRecord.EndsOn,
                status = eventRecord.Status,
                notes = eventRecord.Notes
            }),
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Request.Headers.UserAgent.ToString(),
            CreatedAt = now
        };

        database.Events.Add(eventRecord);
        database.AuditLogs.Add(audit);
        await database.SaveChangesAsync(ct);

        return StatusCode(StatusCodes.Status201Created, new EventResponse(
            eventRecord.Id,
            eventRecord.Name,
            eventRecord.Description,
            eventRecord.Venue,
            eventRecord.LocationId,
            eventRecord.StartsOn,
            eventRecord.EndsOn,
            eventRecord.Status,
            eventRecord.Notes,
            eventRecord.OpenedAt,
            eventRecord.ClosedAt,
            eventRecord.CreatedAt,
            eventRecord.UpdatedAt,
            eventRecord.DeletedAt));
    }

    [HttpPost("~/api/events")]
    [Authorize(Policy = "events.manage")]
    public async Task<IActionResult> LegacyCreate([FromBody] LegacyCreateEventRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return ValidationFailure("name", "Informe o nome do evento.");
        if (request.LocationId is Guid locationId && !await database.Locations.AnyAsync(location => location.Id == locationId, ct))
            return ValidationFailure("location_id", "A localização informada não existe.");
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        var now = DateTimeOffset.UtcNow;
        var eventRecord = new Event
        {
            Name = request.Name.Trim(),
            Description = request.Description,
            Venue = request.Venue,
            LocationId = request.LocationId,
            StartsOn = request.StartsOn,
            EndsOn = request.EndsOn,
            Notes = request.Notes,
            Status = "planejado",
            CreatedBy = userId,
            UpdatedBy = userId,
            CreatedAt = now,
            UpdatedAt = now
        };
        database.Events.Add(eventRecord);
        database.AuditLogs.Add(new AuditLog
        {
            UserId = userId,
            UserName = User.Identity?.Name,
            Action = "create",
            EntityType = "event",
            EntityId = eventRecord.Id,
            EntityLabel = eventRecord.Name,
            AfterJson = JsonSerializer.Serialize(new
            {
                name = eventRecord.Name,
                description = eventRecord.Description,
                venue = eventRecord.Venue,
                location_id = eventRecord.LocationId,
                starts_on = eventRecord.StartsOn,
                ends_on = eventRecord.EndsOn,
                status = eventRecord.Status,
                notes = eventRecord.Notes
            }),
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Request.Headers.UserAgent.ToString(),
            CreatedAt = now
        });
        await database.SaveChangesAsync(ct);

        return StatusCode(StatusCodes.Status201Created, new
        {
            ok = true,
            data = new
            {
                id = eventRecord.Id,
                name = eventRecord.Name,
                description = eventRecord.Description,
                venue = eventRecord.Venue,
                starts_on = eventRecord.StartsOn,
                ends_on = eventRecord.EndsOn,
                status = eventRecord.Status,
                status_label = "Planejado",
                notes = eventRecord.Notes,
                opened_at = eventRecord.OpenedAt,
                closed_at = eventRecord.ClosedAt,
                location_id = eventRecord.LocationId,
                created_at = eventRecord.CreatedAt,
                allocations = Array.Empty<object>(),
                industries = Array.Empty<object>(),
                returns = Array.Empty<object>()
            }
        });
    }

    private IActionResult LegacyFailure(int statusCode, string code, string message) => StatusCode(statusCode, new
    {
        ok = false,
        error = new { code, message }
    });

    private IActionResult LegacyValidationFailure(Dictionary<string, string> fields) => UnprocessableEntity(new
    {
        ok = false,
        error = new { code = "VALIDATION_ERROR", message = "Verifique os campos destacados.", fields }
    });

    private IActionResult ValidationFailure(string field, string message) => UnprocessableEntity(new
    {
        message = "The given data was invalid.",
        errors = new Dictionary<string, string[]> { [field] = [message] }
    });

    private static string? ReadOptionalString(JsonElement value, string field, int maxLength, string? current, bool required, Dictionary<string, string[]> errors)
    {
        if (value.ValueKind == JsonValueKind.Undefined) return current;
        if (value.ValueKind == JsonValueKind.Null && !required) return null;
        if (value.ValueKind != JsonValueKind.String)
        {
            errors[field] = [$"The {field} field must be a string."];
            return current;
        }

        var parsed = value.GetString()?.Trim() ?? string.Empty;
        if (required && parsed.Length == 0)
            errors[field] = [$"The {field} field is required."];
        else if (parsed.Length > maxLength)
            errors[field] = [$"The {field} field must not be greater than {maxLength} characters."];
        return parsed;
    }

    private static DateOnly? ReadOptionalDate(JsonElement value, string field, DateOnly? current, Dictionary<string, string[]> errors)
    {
        if (value.ValueKind == JsonValueKind.Undefined) return current;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String && DateOnly.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;
        errors[field] = [$"The {field} field must be a valid date."];
        return current;
    }

    private static Dictionary<string, object?> EventAuditValues(Event eventRecord) => new()
    {
        ["name"] = eventRecord.Name,
        ["description"] = eventRecord.Description,
        ["venue"] = eventRecord.Venue,
        ["starts_on"] = eventRecord.StartsOn,
        ["ends_on"] = eventRecord.EndsOn,
        ["notes"] = eventRecord.Notes
    };

    private static Guid? ParseGuid(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;

    private static bool TryParseInteger(JsonElement value, out int parsed)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetInt32(out parsed);
        if (value.ValueKind == JsonValueKind.String)
            return int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
        parsed = 0;
        return false;
    }

    private static string? ThumbUrl(Guid itemId, string? photoPath, string? thumbPath)
    {
        var versionPath = photoPath ?? thumbPath;
        if (thumbPath is null || versionPath is null)
            return null;
        var version = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(versionPath)))[..8].ToLowerInvariant();
        return $"/api/v1/items/{itemId}/photo?size=thumb&v={version}";
    }

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

    public sealed class LegacyCreateEventRequest
    {
        [Required, MaxLength(150)]
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [MaxLength(2000)]
        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [MaxLength(190)]
        [JsonPropertyName("venue")]
        public string? Venue { get; init; }

        [JsonPropertyName("location_id")]
        public Guid? LocationId { get; init; }

        [JsonPropertyName("starts_on")]
        public DateOnly? StartsOn { get; init; }

        [JsonPropertyName("ends_on")]
        public DateOnly? EndsOn { get; init; }

        [MaxLength(2000)]
        [JsonPropertyName("notes")]
        public string? Notes { get; init; }
    }

    public sealed class CreateEventRequest
    {
        [Required, MaxLength(150)]
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [MaxLength(4000)]
        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [MaxLength(190)]
        [JsonPropertyName("venue")]
        public string? Venue { get; init; }

        [JsonPropertyName("location_id")]
        public Guid? LocationId { get; init; }

        [JsonPropertyName("starts_on")]
        public DateOnly? StartsOn { get; init; }

        [JsonPropertyName("ends_on")]
        public DateOnly? EndsOn { get; init; }

        [MaxLength(2000)]
        [JsonPropertyName("notes")]
        public string? Notes { get; init; }
    }

    public sealed class AllocateEventRequest
    {
        [JsonPropertyName("industry_id")]
        public JsonElement IndustryId { get; init; }

        [JsonPropertyName("item_id")]
        public JsonElement ItemId { get; init; }

        [JsonPropertyName("qty_allocated")]
        public JsonElement QuantityAllocated { get; init; }
    }

    public sealed class UpdateEventRequest
    {
        [JsonPropertyName("name")] public JsonElement Name { get; init; }
        [JsonPropertyName("description")] public JsonElement Description { get; init; }
        [JsonPropertyName("venue")] public JsonElement Venue { get; init; }
        [JsonPropertyName("starts_on")] public JsonElement StartsOn { get; init; }
        [JsonPropertyName("ends_on")] public JsonElement EndsOn { get; init; }
        [JsonPropertyName("notes")] public JsonElement Notes { get; init; }
    }

    public sealed class EventWithdrawalRequest
    {
        [JsonPropertyName("industry_id")] public JsonElement IndustryId { get; init; }
        [JsonPropertyName("received_by_name")] public string? ReceivedByName { get; init; }
        [JsonPropertyName("received_by_email")] public string? ReceivedByEmail { get; init; }
        [JsonPropertyName("received_by_document")] public string? ReceivedByDocument { get; init; }
        [JsonPropertyName("signature")] public string? Signature { get; init; }
        [JsonPropertyName("notes")] public string? Notes { get; init; }
        [JsonPropertyName("items")] public JsonElement Items { get; init; }
    }

    private sealed record EventResponse(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("venue")] string? Venue,
        [property: JsonPropertyName("location_id")] Guid? LocationId,
        [property: JsonPropertyName("starts_on")] DateOnly? StartsOn,
        [property: JsonPropertyName("ends_on")] DateOnly? EndsOn,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("notes")] string? Notes,
        [property: JsonPropertyName("opened_at")] DateTimeOffset? OpenedAt,
        [property: JsonPropertyName("closed_at")] DateTimeOffset? ClosedAt,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
        [property: JsonPropertyName("deleted_at")] DateTimeOffset? DeletedAt,
        [property: JsonPropertyName("allocations_count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? AllocationsCount = null,
        [property: JsonPropertyName("allocations"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<EventAllocationResponse>? Allocations = null,
        [property: JsonPropertyName("industries"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<EventIndustryBalanceResponse>? Industries = null,
        [property: JsonPropertyName("returns"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<EventReturnResponse>? Returns = null);

    private sealed record EventIndustryBalanceResponse(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("allocated")] int Allocated,
        [property: JsonPropertyName("withdrawn")] int Withdrawn,
        [property: JsonPropertyName("saldo")] int Balance);

    private sealed record EventIndustryReference(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("name")] string Name);

    private sealed record EventItemReference(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("name")] string Name);

    private sealed record EventReturnResponse(
        [property: JsonPropertyName("industry")] EventIndustryReference Industry,
        [property: JsonPropertyName("item")] EventItemReference Item,
        [property: JsonPropertyName("qty_allocated")] int QuantityAllocated,
        [property: JsonPropertyName("qty_withdrawn")] int QuantityWithdrawn,
        [property: JsonPropertyName("saldo")] int Balance);

    private sealed record EventAllocationResponse(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("event_id")] Guid EventId,
        [property: JsonPropertyName("industry_id")] Guid IndustryId,
        [property: JsonPropertyName("item_id")] Guid ItemId,
        [property: JsonPropertyName("qty_allocated")] int QuantityAllocated,
        [property: JsonPropertyName("qty_withdrawn")] int QuantityWithdrawn,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

    private sealed record EventBalanceItem(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("thumb_url")] string? ThumbUrl);

    private sealed record EventBalanceRow(
        [property: JsonPropertyName("item")] EventBalanceItem Item,
        [property: JsonPropertyName("qty_allocated")] int QuantityAllocated,
        [property: JsonPropertyName("qty_withdrawn")] int QuantityWithdrawn,
        [property: JsonPropertyName("saldo")] int Balance);

    private sealed record PageLink(
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("active")] bool Active);
}
