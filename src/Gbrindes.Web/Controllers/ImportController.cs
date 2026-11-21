using Gbrindes.Web.Imports;
using Gbrindes.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text.Json;
using Gbrindes.Web.Inventory;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Authorize(Policy = "import.run")]
[Route("api/v1/import")]
public sealed class ImportController(ImportTemplateService templates, ImportPreviewService previews, ApplicationDbContext database, InventoryService inventory) : ControllerBase
{
    private const string SpreadsheetContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    [HttpGet("template/{type}")]
    public IActionResult Template(string type)
    {
        if (!templates.TryCreate(type, out var content))
            return NotFound(new { ok = false, error = "NOT_FOUND", message = "Modelo não encontrado." });

        var fileName = type == "items" ? "modelo-importacao-brindes.xlsx" : "modelo-importacao-solicitacoes.xlsx";
        return File(content, SpreadsheetContentType, fileName);
    }

    [HttpPost("items/preview")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> PreviewItems([FromForm] IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return UnprocessableEntity(new { ok = false, errors = new Dictionary<string, string[]> { ["file"] = ["Selecione a planilha."] } });
        try
        {
            await using var stream = file.OpenReadStream();
            var result = await previews.PreviewItemsAsync(stream, ct);
            return Ok(new
            {
                rows = result.Rows,
                errors = result.Errors,
                total = result.Total,
                report = new { valid = result.Total - result.Errors, invalid = result.Errors, pending = result.Total }
            });
        }
        catch (InvalidDataException exception)
        {
            return UnprocessableEntity(new { ok = false, errors = new Dictionary<string, string[]> { ["file"] = [exception.Message] } });
        }
    }

    [HttpPost("items/apply")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public Task<IActionResult> ApplyItems([FromForm] IFormFile? file, CancellationToken ct) => ApplyItemsCoreAsync(file, false, ct);

    [HttpPost("~/api/import/items/commit")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public Task<IActionResult> CommitLegacy([FromForm] IFormFile? file, CancellationToken ct) => ApplyItemsCoreAsync(file, true, ct);

    private async Task<IActionResult> ApplyItemsCoreAsync(IFormFile? file, bool legacyEnvelope, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return UnprocessableEntity(new { ok = false, errors = new Dictionary<string, string[]> { ["file"] = ["Selecione a planilha."] } });
        ItemsPreview? parsedPreview = null;
        try
        {
            await using var stream = file.OpenReadStream();
            var preview = await previews.PreviewItemsAsync(stream, ct);
            parsedPreview = preview;
            if (preview.Rows.Any(row => row.Ok && row.Payload is ImportItemPayload payload && payload.InitialQuantity > 0)
                && !User.HasClaim("permission", "stock.entry"))
                return Forbid();
            if (preview.Rows.Any(row => row.Ok && row.Payload is ImportItemPayload payload && string.IsNullOrWhiteSpace(payload.Code)))
                return UnprocessableEntity(new { ok = false, errors = new Dictionary<string, string[]> { ["code"] = ["Informe código único em todas as linhas para permitir reenvio idempotente."] } });

            await using var transaction = database.Database.IsRelational() ? await database.Database.BeginTransactionAsync(ct) : null;
            var created = 0;
            var batch = new Dictionary<string, string>(StringComparer.Ordinal);
            var newItems = new List<(CatalogItem Item, ImportItemPayload Payload)>();
            foreach (var row in preview.Rows.Where(row => row.Ok))
            {
                var payload = (ImportItemPayload)row.Payload;
                var code = payload.Code!.Trim().ToUpperInvariant();
                var rowIdentity = JsonSerializer.Serialize(payload with { Code = code, Name = payload.Name.Trim() });
                if (batch.TryGetValue(code, out var prior))
                {
                    if (prior == rowIdentity) continue;
                    if (transaction is not null) await transaction.RollbackAsync(ct);
                    return Conflict(new { error = "IMPORT_KEY_CONFLICT", code });
                }
                batch[code] = rowIdentity;
                var existing = await database.CatalogItems.AsNoTracking().SingleOrDefaultAsync(item => item.Code == code, ct);
                if (existing is not null)
                {
                    var priorFingerprint = await database.AuditLogs.AsNoTracking()
                        .Where(log => log.EntityType == "item" && log.EntityId == existing.Id && log.Action == "create")
                        .OrderBy(log => log.CreatedAt).Select(log => log.AfterJson).FirstOrDefaultAsync(ct);
                    using var priorDocument = priorFingerprint is null ? null : JsonDocument.Parse(priorFingerprint);
                    if (priorDocument?.RootElement.TryGetProperty("import_fingerprint", out var savedFingerprint) == true
                        && savedFingerprint.GetString() == rowIdentity)
                        continue;
                    if (transaction is not null) await transaction.RollbackAsync(ct);
                    return Conflict(new { error = "IMPORT_KEY_CONFLICT", code });
                }
                var item = new CatalogItem
                {
                    Code = code,
                    Name = payload.Name.Trim(),
                    CategoryId = payload.CategoryId!.Value,
                    IndustryId = payload.IndustryId,
                    LocationId = payload.LocationId,
                    SupplierId = payload.SupplierId,
                    UnitValue = decimal.TryParse(payload.UnitValue, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null,
                    MinStock = Math.Max(0, payload.MinStock),
                    Notes = payload.Notes
                };
                var actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedActorId) ? parsedActorId : (Guid?)null;
                item.CreatedBy = actorId;
                item.UpdatedBy = actorId;
                database.CatalogItems.Add(item);
                database.AuditLogs.Add(new AuditLog
                {
                    UserId = actorId,
                    UserName = User.FindFirstValue(ClaimTypes.Name),
                    Action = "create",
                    EntityType = "item",
                    EntityId = item.Id,
                    EntityLabel = $"{item.Code} - {item.Name}",
                    AfterJson = JsonSerializer.Serialize(new { code = item.Code, name = item.Name, category_id = item.CategoryId, industry_id = item.IndustryId, location_id = item.LocationId, supplier_id = item.SupplierId, unit_value = item.UnitValue, min_stock = item.MinStock, initial_quantity = payload.InitialQuantity, notes = item.Notes, import_fingerprint = rowIdentity }),
                    CreatedAt = DateTimeOffset.UtcNow
                });
                newItems.Add((item, payload));
                created++;
            }
            await database.SaveChangesAsync(ct);
            foreach (var (item, payload) in newItems.Where(row => row.Payload.InitialQuantity > 0))
            {
                var actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedActorId) ? parsedActorId : (Guid?)null;
                await inventory.MoveAsync(item.Id, "entrada", payload.InitialQuantity, actorId, "Saldo inicial",
                    "Saldo inicial informado no cadastro do brinde.", null, null, null, null, payload.SupplierId,
                    null, ct);
            }
            if (transaction is not null) await transaction.CommitAsync(ct);
            var result = new { created, skipped = preview.Errors + preview.Rows.Count(row => row.Ok) - created };
            return legacyEnvelope ? Ok(new { ok = true, data = result }) : Ok(new { result.created, result.skipped, total = preview.Total });
        }
        catch (InvalidDataException exception)
        {
            return UnprocessableEntity(new { ok = false, errors = new Dictionary<string, string[]> { ["file"] = [exception.Message] } });
        }
        catch (DbUpdateException) when (parsedPreview is not null)
        {
            database.ChangeTracker.Clear();
            if (!await IsExactImportReplayAsync(parsedPreview, ct))
                return Conflict(new { error = "IMPORT_KEY_CONFLICT" });
            var skipped = parsedPreview.Errors + parsedPreview.Rows.Count(row => row.Ok);
            return legacyEnvelope
                ? Ok(new { ok = true, data = new { created = 0, skipped } })
                : Ok(new { created = 0, skipped, total = parsedPreview.Total });
        }
    }

    private async Task<bool> IsExactImportReplayAsync(ItemsPreview preview, CancellationToken ct)
    {
        var validRows = preview.Rows.Where(row => row.Ok).ToArray();
        if (validRows.Length == 0) return false;
        foreach (var row in validRows)
        {
            if (row.Payload is not ImportItemPayload payload || string.IsNullOrWhiteSpace(payload.Code)) return false;
            var code = payload.Code.Trim().ToUpperInvariant();
            var fingerprint = JsonSerializer.Serialize(payload with { Code = code, Name = payload.Name.Trim() });
            var itemId = await database.CatalogItems.AsNoTracking()
                .Where(item => item.Code == code).Select(item => (Guid?)item.Id).SingleOrDefaultAsync(ct);
            if (itemId is null) return false;
            var auditJson = await database.AuditLogs.AsNoTracking()
                .Where(log => log.EntityType == "item" && log.EntityId == itemId && log.Action == "create")
                .OrderBy(log => log.CreatedAt).Select(log => log.AfterJson).FirstOrDefaultAsync(ct);
            if (auditJson is null) return false;
            using var document = JsonDocument.Parse(auditJson);
            if (!document.RootElement.TryGetProperty("import_fingerprint", out var saved)
                || saved.GetString() != fingerprint) return false;
        }
        return true;
    }

    [HttpPost("requests/preview")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> PreviewRequests([FromForm] IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return UnprocessableEntity(new { ok = false, errors = new Dictionary<string, string[]> { ["file"] = ["Selecione a planilha."] } });
        try
        {
            await using var stream = file.OpenReadStream();
            var result = await previews.PreviewRequestsAsync(stream, ct);
            return Ok(new
            {
                rows = result.Rows,
                errors = result.Errors,
                total = result.Total,
                report = new { valid = result.Total - result.Errors, invalid = result.Errors, pending = result.Total }
            });
        }
        catch (InvalidDataException exception)
        {
            return UnprocessableEntity(new { ok = false, errors = new Dictionary<string, string[]> { ["file"] = [exception.Message] } });
        }
    }
}
