using System.Data.Common;
using System.Text.RegularExpressions;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gbrindes.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Route("api/v1/industries")]
[Authorize]
public sealed class IndustriesController(ApplicationDbContext db) : ContactLookupsController<Industry>(db, db.Industries);

[ApiController]
[Route("api/v1/suppliers")]
[Authorize]
public sealed class SuppliersController(ApplicationDbContext db) : ContactLookupsController<Supplier>(db, db.Suppliers);

public abstract class ContactLookupsController<T>(ApplicationDbContext db, DbSet<T> set) : ControllerBase where T : ContactLookup, new()
{
    [HttpGet, Authorize(Policy = "lookups.view")]
    public async Task<IActionResult> List(string? q, string? active, string? trashed, int page = 1, [FromQuery(Name = "per_page")] int perPage = 25, CancellationToken ct = default)
    {
        var includeTrash = trashed is "1" or "true";
        var query = set.AsNoTracking().Where(x => includeTrash ? x.DeletedAt != null : x.DeletedAt == null);
        if (active is "0" or "1") { var flag = active == "1"; query = query.Where(x => x.Active == flag); }
        if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim().ToLowerInvariant(); query = query.Where(x => x.Name.ToLower().Contains(s) || (x.Cnpj != null && x.Cnpj.ToLower().Contains(s)) || (x.ContactName != null && x.ContactName.ToLower().Contains(s)) || (x.ContactEmail != null && x.ContactEmail.ToLower().Contains(s))); }
        page = Math.Clamp(page, 1, 1_000_000); perPage = Math.Clamp(perPage, 1, 100);
        var total = await query.CountAsync(ct); var items = await query.OrderBy(x => x.Name).ThenBy(x => x.Id).Skip((page - 1) * perPage).Take(perPage).ToListAsync(ct);
        return Ok(new { items = items.Select(View), page, per_page = perPage, total });
    }
    [HttpGet("{id:guid}"), Authorize(Policy = "lookups.view")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) { var x = await set.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct); return x is null ? NotFound() : Ok(View(x)); }
    [HttpPost, Authorize(Policy = "lookups.manage")]
    public async Task<IActionResult> Create(ContactRequest r, CancellationToken ct)
    {
        var values = Parse(r, true, out var errors); if (errors.Count > 0) return Bad(errors);
        var name = values.Name!.Trim(); var key = Key(name); if (await set.AnyAsync(x => x.DeletedAt == null && x.NameKey == key, ct)) return Bad("name", "Já existe um registro com este nome.");
        var x = new T { Name = name, NameKey = key, Cnpj = Clean(values.Cnpj), ContactName = Clean(values.ContactName), ContactEmail = Clean(values.ContactEmail), ContactPhone = Clean(values.ContactPhone), Notes = Clean(values.Notes), Active = r.Active ?? true };
        var actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var createdBy) ? createdBy : (Guid?)null;
        x.CreatedBy = actorId;
        x.UpdatedBy = actorId;
        set.Add(x);
        AddAudit("create", x, null, AuditData(x));
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = x.Id }, View(x));
    }
    [HttpPut("{id:guid}"), Authorize(Policy = "lookups.manage")]
    public async Task<IActionResult> Update(Guid id, ContactRequest r, CancellationToken ct)
    {
        var x = await set.FirstOrDefaultAsync(v => v.Id == id && v.DeletedAt == null, ct); if (x is null) return NotFound();
        var values = Parse(r, false, out var errors); if (errors.Count > 0) return Bad(errors);
        var before = AuditData(x);
        if (r.Name.ValueKind != JsonValueKind.Undefined) { var n = values.Name!.Trim(); var k = Key(n); if (await set.AnyAsync(v => v.Id != id && v.DeletedAt == null && v.NameKey == k, ct)) return Bad("name", "Já existe um registro com este nome."); x.Name = n; x.NameKey = k; }
        if (r.Cnpj.ValueKind != JsonValueKind.Undefined) x.Cnpj = Clean(values.Cnpj);
        if (r.ContactName.ValueKind != JsonValueKind.Undefined) x.ContactName = Clean(values.ContactName);
        if (r.ContactEmail.ValueKind != JsonValueKind.Undefined) x.ContactEmail = Clean(values.ContactEmail);
        if (r.ContactPhone.ValueKind != JsonValueKind.Undefined) x.ContactPhone = Clean(values.ContactPhone);
        if (r.Notes.ValueKind != JsonValueKind.Undefined) x.Notes = Clean(values.Notes);
        if (r.Active is not null) x.Active = r.Active.Value;
        x.UpdatedAt = DateTimeOffset.UtcNow;
        var current = AuditData(x);
        var beforeChanges = new Dictionary<string, object?>();
        var afterChanges = new Dictionary<string, object?>();
        foreach (var (field, value) in current)
        {
            if (!Equals(before[field], value))
            {
                beforeChanges[field] = before[field];
                afterChanges[field] = value;
            }
        }
        if (afterChanges.Count > 0)
        {
            x.UpdatedBy = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var updatedBy) ? updatedBy : null;
            AddAudit("update", x, beforeChanges, afterChanges, x.UpdatedAt);
        }
        await db.SaveChangesAsync(ct);
        return Ok(View(x));
    }
    [HttpPost("{id:guid}/activate"), Authorize(Policy = "lookups.manage")] public Task<IActionResult> Activate(Guid id, CancellationToken ct) => SetActive(id, true, ct);
    [HttpPost("{id:guid}/deactivate"), Authorize(Policy = "lookups.manage")] public Task<IActionResult> Deactivate(Guid id, CancellationToken ct) => SetActive(id, false, ct);
    private async Task<IActionResult> SetActive(Guid id, bool active, CancellationToken ct)
    {
        var x = await set.FirstOrDefaultAsync(v => v.Id == id && v.DeletedAt == null, ct);
        if (x is null) return NotFound();
        if (x.Active == active) return Ok(View(x));
        var previous = x.Active;
        x.Active = active;
        x.UpdatedAt = DateTimeOffset.UtcNow;
        x.UpdatedBy = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var updatedBy) ? updatedBy : null;
        AddAudit(active ? "activate" : "deactivate", x, new { active = previous }, new { active }, x.UpdatedAt);
        await db.SaveChangesAsync(ct);
        return Ok(View(x));
    }
    [HttpDelete("{id:guid}"), Authorize(Policy = "lookups.delete")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var x = await set.FirstOrDefaultAsync(v => v.Id == id && v.DeletedAt == null, ct);
        if (x is null) return NotFound();
        var before = AuditData(x);
        x.DeletedAt = x.UpdatedAt = DateTimeOffset.UtcNow;
        AddAudit("delete", x, before, null, x.UpdatedAt);
        await db.SaveChangesAsync(ct);
        return Ok(new { id, deleted = true });
    }
    [HttpPost("{id:guid}/restore"), Authorize(Policy = "lookups.delete")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    {
        var x = await set.FirstOrDefaultAsync(v => v.Id == id && v.DeletedAt != null, ct);
        if (x is null) return NotFound();
        if (await set.AnyAsync(v => v.Id != id && v.DeletedAt == null && v.NameKey == x.NameKey, ct)) return Bad("name", "Já existe um registro com este nome.");
        x.DeletedAt = null;
        x.UpdatedAt = DateTimeOffset.UtcNow;
        AddAudit("restore", x, null, null, x.UpdatedAt);
        await db.SaveChangesAsync(ct);
        return Ok(View(x));
    }
    [HttpPost("{id:guid}/purge"), Authorize(Policy = "lookups.purge")]
    public async Task<IActionResult> Purge(Guid id, ContactPurgeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Confirm)) return Bad("confirm", "Confirmação obrigatória.");
        IDbContextTransaction? transaction = null;
        try
        {
            if (db.Database.IsRelational()) transaction = await db.Database.BeginTransactionAsync(ct);
            T? value;
            if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.Transaction = transaction!.GetDbTransaction();
                var table = typeof(T) == typeof(Industry) ? "Industries" : "Suppliers";
                command.CommandText = $"SELECT \"Id\" FROM \"{table}\" WHERE \"Id\" = @id FOR UPDATE";
                DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = "id";
                parameter.Value = id;
                command.Parameters.Add(parameter);
                if (await command.ExecuteScalarAsync(ct) is null) return NotFound();
                value = await set.SingleOrDefaultAsync(x => x.Id == id, ct);
            }
            else value = await set.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (value is null) return NotFound();
            if (value.DeletedAt is null) return Conflict(new { error = "NOT_IN_TRASH" });
            if (!string.Equals(request.Confirm.Trim(), value.Name, StringComparison.OrdinalIgnoreCase))
                return Bad("confirm", $"Digite \"{value.Name}\" para confirmar a exclusão definitiva.");
            var uses = await ReferencesAsync(id, ct);
            if (uses.Count > 0) return Conflict(new { error = "IN_USE", uses });

            var entityType = typeof(T) == typeof(Industry) ? "industry" : "supplier";
            var agent = Request.Headers.UserAgent.ToString();
            db.AuditLogs.Add(new AuditLog
            {
                UserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ? actorId : null,
                UserName = User.FindFirstValue(ClaimTypes.Name),
                Action = "purge",
                EntityType = entityType,
                EntityId = value.Id,
                EntityLabel = value.Name,
                BeforeJson = JsonSerializer.Serialize(new { id = value.Id, name = value.Name, cnpj = value.Cnpj, contact_name = value.ContactName, contact_email = value.ContactEmail, contact_phone = value.ContactPhone, notes = value.Notes, active = value.Active, deleted_at = value.DeletedAt }),
                Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = agent.Length > 255 ? agent[..255] : agent,
                CreatedAt = DateTimeOffset.UtcNow
            });
            set.Remove(value);
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return Ok(new { id, purged = true });
        }
        catch (DbUpdateException) when (transaction is not null)
        {
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var uses = await ReferencesAsync(id, ct);
            if (uses.Count > 0) return Conflict(new { error = "IN_USE", uses });
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }
    private async Task<List<string>> ReferencesAsync(Guid id, CancellationToken ct)
    {
        var uses = new List<string>();
        if (typeof(T) == typeof(Supplier) && await db.CatalogItems.AnyAsync(item => item.SupplierId == id, ct))
            uses.Add("brinde(s)");
        if (typeof(T) == typeof(Supplier) && await db.StockMovements.AnyAsync(movement => movement.SupplierId == id, ct))
            uses.Add("movimentação(ões)");
        if (typeof(T) == typeof(Industry))
        {
            if (await db.CatalogItems.AnyAsync(item => item.IndustryId == id, ct)) uses.Add("brinde(s)");
            if (await db.Requests.AnyAsync(request => request.IndustryId == id, ct)) uses.Add("solicitação(ões)");
            if (await db.StockMovements.AnyAsync(movement => movement.IndustryId == id, ct)) uses.Add("movimentação(ões)");
            if (await db.StockExitOrders.AnyAsync(order => order.IndustryId == id, ct)) uses.Add("saída(s) autorizada(s)");
            if (await db.EventAllocations.AnyAsync(allocation => allocation.IndustryId == id, ct)) uses.Add("cota(s) de evento");
            if (await db.Deliveries.AnyAsync(delivery => delivery.IndustryId == id, ct)) uses.Add("protocolo(s)");
        }
        return uses;
    }
    private void AddAudit(string action, ContactLookup value, object? before, object? after, DateTimeOffset? createdAt = null)
    {
        var agent = Request.Headers.UserAgent.ToString();
        db.AuditLogs.Add(new AuditLog
        {
            UserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ? actorId : null,
            UserName = User.FindFirstValue(ClaimTypes.Name) ?? "Sistema",
            Action = action,
            EntityType = typeof(T) == typeof(Industry) ? "industry" : "supplier",
            EntityId = value.Id,
            EntityLabel = value.Name.Length > 190 ? value.Name[..190] : value.Name,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before),
            AfterJson = after is null ? null : JsonSerializer.Serialize(after),
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = agent.Length > 255 ? agent[..255] : agent,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow
        });
    }
    private static Dictionary<string, object?> AuditData(ContactLookup x) => new()
    {
        ["id"] = x.Id,
        ["name"] = x.Name,
        ["cnpj"] = x.Cnpj,
        ["contact_name"] = x.ContactName,
        ["contact_email"] = x.ContactEmail,
        ["contact_phone"] = x.ContactPhone,
        ["notes"] = x.Notes,
        ["active"] = x.Active,
        ["deleted_at"] = x.DeletedAt
    };
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim(); private static string Key(string s) => s.Trim().ToUpperInvariant();
    private static ContactValues Parse(ContactRequest r, bool creating, out Dictionary<string, string[]> errors)
    {
        var validationErrors = new Dictionary<string, string[]>();
        errors = validationErrors;
        string? Read(JsonElement value, string field) { if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null; if (value.ValueKind == JsonValueKind.String) return value.GetString(); validationErrors[field] = ["O valor deve ser texto ou nulo."]; return null; }
        var name = Read(r.Name, "name"); var cnpj = Read(r.Cnpj, "cnpj"); var contact = Read(r.ContactName, "contact_name"); var email = Read(r.ContactEmail, "contact_email"); var phone = Read(r.ContactPhone, "contact_phone"); var notes = Read(r.Notes, "notes");
        void Add(string field, string message) => validationErrors[field] = [message];
        if ((creating || r.Name.ValueKind != JsonValueKind.Undefined) && string.IsNullOrWhiteSpace(name)) Add("name", "O nome é obrigatório.");
        if (name?.Length > 150) Add("name", "Máximo 150 caracteres.");
        if (Clean(cnpj) is { } c && (c.Length > 20 || !Regex.IsMatch(c, @"^[0-9./\- ]+$"))) Add("cnpj", "CNPJ inválido.");
        if (contact?.Length > 120) Add("contact_name", "Máximo 120 caracteres.");
        if (Clean(email) is { } e && (e.Length > 190 || !System.Net.Mail.MailAddress.TryCreate(e, out _))) Add("contact_email", "E-mail inválido.");
        if (phone?.Length > 30) Add("contact_phone", "Máximo 30 caracteres.");
        if (notes?.Length > 2000) Add("notes", "Máximo 2000 caracteres.");
        return new(name, cnpj, contact, email, phone, notes);
    }
    private sealed record ContactValues(string? Name, string? Cnpj, string? ContactName, string? ContactEmail, string? ContactPhone, string? Notes);
    private IActionResult Bad(string f, string m) => Bad(new Dictionary<string, string[]> { { f, [m] } }); private IActionResult Bad(Dictionary<string, string[]> e) => UnprocessableEntity(new ValidationProblemDetails(e));
    private static object View(ContactLookup x) => new { id = x.Id, name = x.Name, cnpj = x.Cnpj, contact_name = x.ContactName, contact_email = x.ContactEmail, contact_phone = x.ContactPhone, notes = x.Notes, active = x.Active, created_at = x.CreatedAt, updated_at = x.UpdatedAt, deleted_at = x.DeletedAt };
}
public sealed class ContactPurgeRequest { [JsonPropertyName("confirm")] public string? Confirm { get; set; } }
public sealed class ContactRequest { public JsonElement Name { get; set; } [JsonPropertyName("cnpj")] public JsonElement Cnpj { get; set; } [JsonPropertyName("contact_name")] public JsonElement ContactName { get; set; } [JsonPropertyName("contact_email")] public JsonElement ContactEmail { get; set; } [JsonPropertyName("contact_phone")] public JsonElement ContactPhone { get; set; } public JsonElement Notes { get; set; } public bool? Active { get; set; } }
