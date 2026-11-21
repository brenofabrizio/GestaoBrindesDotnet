using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Gbrindes.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Route("api/v1/locations")]
public sealed class LocationsController(ApplicationDbContext database) : ControllerBase
{
    [HttpGet, Authorize(Policy = "lookups.view")]
    public async Task<IActionResult> List([FromQuery] string? q, [FromQuery] string? active, [FromQuery] string? trashed,
        [FromQuery] string? all, [FromQuery] int page = 1, [FromQuery(Name = "per_page")] int perPage = 25, CancellationToken ct = default)
    {
        var includeTrash = IsTrue(trashed);
        var query = database.Locations.AsNoTracking().Where(x => includeTrash ? x.DeletedAt != null : x.DeletedAt == null);
        if (active is "0" or "1")
        {
            var isActive = active == "1";
            query = query.Where(x => x.Active == isActive);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var search = q.Trim().ToLowerInvariant();
            query = query.Where(x => x.Name.ToLower().Contains(search) || (x.Description != null && x.Description.ToLower().Contains(search)));
        }

        var ordered = query.OrderBy(x => x.Name).ThenBy(x => x.Id);
        if (IsTrue(all))
        {
            var optionLocations = await ordered.Where(x => x.DeletedAt == null && x.Active).ToListAsync(ct);
            return Ok(optionLocations.Select(ToView));
        }

        page = Math.Clamp(page, 1, 1_000_000);
        perPage = Math.Clamp(perPage, 1, 100);
        var total = await query.CountAsync(ct);
        var pageLocations = await ordered.Skip((page - 1) * perPage).Take(perPage).ToListAsync(ct);
        var items = pageLocations.Select(ToView);
        return Ok(new { items, page, per_page = perPage, total });
    }

    [HttpGet("{id:guid}"), Authorize(Policy = "lookups.view")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var location = await database.Locations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return location is null ? NotFound() : Ok(ToView(location));
    }

    [HttpPost, Authorize(Policy = "lookups.manage")]
    public async Task<IActionResult> Create(LocationCreateRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return Invalid("name", "O nome é obrigatório.");
        if (name.Length > 100) return Invalid("name", "O nome deve ter no máximo 100 caracteres.");
        var description = request.Description?.Trim();
        if (request.Kind is not ("cd" or "evento" or "outro")) return Invalid("kind", "O tipo deve ser cd, evento ou outro.");
        if (description?.Length > 255) return Invalid("description", "A descrição deve ter no máximo 255 caracteres.");
        var key = Normalize(name);
        if (await database.Locations.AnyAsync(x => x.DeletedAt == null && x.NameKey == key, ct))
            return Invalid("name", "Já existe um registro com este nome.");

        var location = new Location
        {
            Name = name,
            NameKey = key,
            Description = description,
            Kind = request.Kind!,
            Active = request.Active ?? true
        };
        var now = DateTimeOffset.UtcNow;
        var agent = Request.Headers.UserAgent.ToString();
        var creatorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var createdBy) ? createdBy : (Guid?)null;
        location.CreatedBy = creatorId;
        location.UpdatedBy = creatorId;
        database.Locations.Add(location);
        database.AuditLogs.Add(new AuditLog
        {
            UserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ? actorId : null,
            UserName = User.FindFirstValue(ClaimTypes.Name),
            Action = "create",
            EntityType = "location",
            EntityId = location.Id,
            EntityLabel = location.Name,
            AfterJson = JsonSerializer.Serialize(new { name = location.Name, description = location.Description, kind = location.Kind, active = location.Active }),
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = agent.Length > 255 ? agent[..255] : agent,
            CreatedAt = now
        });
        await database.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = location.Id }, ToView(location));
    }

    [HttpPut("{id:guid}"), Authorize(Policy = "lookups.manage")]
    public async Task<IActionResult> Update(Guid id, LocationUpdateRequest request, CancellationToken ct)
    {
        var location = await database.Locations.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (location is null) return NotFound();
        var before = AuditSnapshot(location);

        if (request.Name.ValueKind != JsonValueKind.Undefined)
        {
            if (request.Name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(request.Name.GetString()))
                return Invalid("name", "O nome é obrigatório.");
            var name = request.Name.GetString()!.Trim();
            if (name.Length > 100) return Invalid("name", "O nome deve ter no máximo 100 caracteres.");
            var key = Normalize(name);
            if (await database.Locations.AnyAsync(x => x.Id != id && x.DeletedAt == null && x.NameKey == key, ct))
                return Invalid("name", "Já existe um registro com este nome.");
            location.Name = name;
            location.NameKey = key;
        }

        if (request.Description.ValueKind != JsonValueKind.Undefined)
        {
            if (request.Description.ValueKind == JsonValueKind.Null)
                location.Description = null;
            else if (request.Description.ValueKind == JsonValueKind.String)
            {
                var description = request.Description.GetString()?.Trim();
                if (description?.Length > 255) return Invalid("description", "A descrição deve ter no máximo 255 caracteres.");
                location.Description = description;
            }
            else return Invalid("description", "A descrição deve ser texto ou nula.");
        }
        if (request.Kind is not null)
        {
            if (request.Kind is not ("cd" or "evento" or "outro")) return Invalid("kind", "O tipo deve ser cd, evento ou outro.");
            location.Kind = request.Kind;
        }
        if (request.Active is not null) location.Active = request.Active.Value;
        location.UpdatedAt = DateTimeOffset.UtcNow;
        AddUpdateAudit(location, before);
        await database.SaveChangesAsync(ct);
        return Ok(ToView(location));
    }

    [HttpPost("{id:guid}/activate"), Authorize(Policy = "lookups.manage")]
    public Task<IActionResult> Activate(Guid id, CancellationToken ct) => SetActive(id, true, ct);

    [HttpPost("{id:guid}/deactivate"), Authorize(Policy = "lookups.manage")]
    public Task<IActionResult> Deactivate(Guid id, CancellationToken ct) => SetActive(id, false, ct);

    [HttpDelete("{id:guid}"), Authorize(Policy = "lookups.delete")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var location = await database.Locations.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (location is null) return NotFound();
        var before = new { id = location.Id, name = location.Name, description = location.Description, kind = location.Kind, active = location.Active, deleted_at = location.DeletedAt };
        location.DeletedAt = DateTimeOffset.UtcNow;
        location.UpdatedAt = location.DeletedAt.Value;
        var agent = Request.Headers.UserAgent.ToString();
        database.AuditLogs.Add(new AuditLog
        {
            UserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ? actorId : null,
            UserName = User.FindFirstValue(ClaimTypes.Name),
            Action = "delete",
            EntityType = "location",
            EntityId = location.Id,
            EntityLabel = location.Name,
            BeforeJson = JsonSerializer.Serialize(before),
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = agent.Length > 255 ? agent[..255] : agent,
            CreatedAt = location.DeletedAt.Value
        });
        await database.SaveChangesAsync(ct);
        return Ok(new { id, deleted = true });
    }

    [HttpPost("{id:guid}/restore"), Authorize(Policy = "lookups.delete")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    {
        var location = await database.Locations.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt != null, ct);
        if (location is null) return NotFound();
        if (await database.Locations.AnyAsync(x => x.Id != id && x.DeletedAt == null && x.NameKey == location.NameKey, ct))
            return Invalid("name", "Já existe um registro com este nome.");
        location.DeletedAt = null;
        location.UpdatedAt = DateTimeOffset.UtcNow;
        var agent = Request.Headers.UserAgent.ToString();
        database.AuditLogs.Add(new AuditLog
        {
            UserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ? actorId : null,
            UserName = User.FindFirstValue(ClaimTypes.Name),
            Action = "restore",
            EntityType = "location",
            EntityId = location.Id,
            EntityLabel = location.Name,
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = agent.Length > 255 ? agent[..255] : agent,
            CreatedAt = location.UpdatedAt
        });
        await database.SaveChangesAsync(ct);
        return Ok(ToView(location));
    }

    [HttpPost("{id:guid}/purge"), Authorize(Policy = "lookups.purge")]
    public async Task<IActionResult> Purge(Guid id, LocationPurgeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Confirm)) return Invalid("confirm", "Confirmação obrigatória.");
        IDbContextTransaction? transaction = null;
        try
        {
            if (database.Database.IsRelational()) transaction = await database.Database.BeginTransactionAsync(ct);
            Location? location;
            if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
                location = await database.Locations.FromSqlInterpolated(
                    $"SELECT * FROM \"Locations\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
            else location = await database.Locations.SingleOrDefaultAsync(value => value.Id == id, ct);
            if (location is null) return NotFound();
            if (location.DeletedAt is null) return Conflict(new { error = "NOT_IN_TRASH" });
            if (!string.Equals(request.Confirm.Trim(), location.Name, StringComparison.OrdinalIgnoreCase))
                return Invalid("confirm", $"Digite \"{location.Name}\" para confirmar a exclusão definitiva.");
            var uses = await ReferencesAsync(id, ct);
            if (uses.Count > 0) return Conflict(new { error = "IN_USE", uses });

            var agent = Request.Headers.UserAgent.ToString();
            database.AuditLogs.Add(new AuditLog
            {
                UserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ? actorId : null,
                UserName = User.FindFirstValue(ClaimTypes.Name),
                Action = "purge",
                EntityType = "location",
                EntityId = location.Id,
                EntityLabel = location.Name,
                BeforeJson = JsonSerializer.Serialize(new { id = location.Id, name = location.Name, description = location.Description, kind = location.Kind, active = location.Active, deleted_at = location.DeletedAt }),
                Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = agent.Length > 255 ? agent[..255] : agent,
                CreatedAt = DateTimeOffset.UtcNow
            });
            database.Locations.Remove(location);
            await database.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return Ok(new { id, purged = true });
        }
        catch (DbUpdateException) when (transaction is not null)
        {
            await transaction.RollbackAsync(ct);
            database.ChangeTracker.Clear();
            var uses = await ReferencesAsync(id, ct);
            if (uses.Count > 0) return Conflict(new { error = "IN_USE", uses });
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    private async Task<IActionResult> SetActive(Guid id, bool active, CancellationToken ct)
    {
        var location = await database.Locations.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (location is null) return NotFound();
        if (location.Active == active) return Ok(ToView(location));
        var before = location.Active;
        location.Active = active;
        location.UpdatedAt = DateTimeOffset.UtcNow;
        location.UpdatedBy = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var updatedBy) ? updatedBy : null;
        AddStateAudit(location, active ? "activate" : "deactivate", before, active);
        await database.SaveChangesAsync(ct);
        return Ok(ToView(location));
    }

    private IActionResult Invalid(string field, string message) =>
        UnprocessableEntity(new ValidationProblemDetails(new Dictionary<string, string[]> { [field] = [message] }));

    private void AddUpdateAudit(Location location, Dictionary<string, object?> before)
    {
        var current = AuditSnapshot(location);
        var oldValues = new Dictionary<string, object?>();
        var newValues = new Dictionary<string, object?>();
        foreach (var (field, value) in current)
        {
            if (Equals(before[field], value)) continue;
            oldValues[field] = before[field];
            newValues[field] = value;
        }
        if (newValues.Count == 0) return;

        location.UpdatedBy = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var updatedBy) ? updatedBy : null;
        var agent = Request.Headers.UserAgent.ToString();
        database.AuditLogs.Add(new AuditLog
        {
            UserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ? actorId : null,
            UserName = User.FindFirstValue(ClaimTypes.Name),
            Action = "update",
            EntityType = "location",
            EntityId = location.Id,
            EntityLabel = location.Name,
            BeforeJson = JsonSerializer.Serialize(oldValues),
            AfterJson = JsonSerializer.Serialize(newValues),
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = agent.Length > 255 ? agent[..255] : agent,
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private void AddStateAudit(Location location, string action, bool before, bool after)
    {
        var agent = Request.Headers.UserAgent.ToString();
        database.AuditLogs.Add(new AuditLog
        {
            UserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ? actorId : null,
            UserName = User.FindFirstValue(ClaimTypes.Name),
            Action = action,
            EntityType = "location",
            EntityId = location.Id,
            EntityLabel = location.Name,
            BeforeJson = JsonSerializer.Serialize(new { active = before }),
            AfterJson = JsonSerializer.Serialize(new { active = after }),
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = agent.Length > 255 ? agent[..255] : agent,
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private static Dictionary<string, object?> AuditSnapshot(Location location) => new()
    {
        ["name"] = location.Name,
        ["description"] = location.Description,
        ["kind"] = location.Kind,
        ["active"] = location.Active
    };

    private async Task<List<string>> ReferencesAsync(Guid id, CancellationToken ct)
    {
        var uses = new List<string>();
        if (await database.CatalogItems.AnyAsync(item => item.LocationId == id, ct)) uses.Add("brinde(s)");
        if (await database.StockPositions.AnyAsync(position => position.LocationId == id, ct)) uses.Add("posição(ões) de estoque");
        if (await database.StockMovements.AnyAsync(movement => movement.LocationId == id || movement.FromLocationId == id || movement.ToLocationId == id, ct))
            uses.Add("movimentação(ões)");
        return uses;
    }

    private static LocationView ToView(Location x) => new(x.Id, x.Name, x.Description, x.Kind, x.Active, x.CreatedAt, x.UpdatedAt, x.DeletedAt);
    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static bool IsTrue(string? value) => value is "1" or "true";
}

public sealed class LocationCreateRequest
{
    [StringLength(100)] public string? Name { get; set; }
    [StringLength(255)] public string? Description { get; set; }
    public string? Kind { get; set; }
    public bool? Active { get; set; }
}

public sealed class LocationUpdateRequest
{
    public JsonElement Name { get; set; }
    public JsonElement Description { get; set; }
    public string? Kind { get; set; }
    public bool? Active { get; set; }
}

public sealed class LocationPurgeRequest
{
    [StringLength(100)] public string? Confirm { get; set; }
}

public sealed record LocationView(Guid Id, string Name, string? Description, string Kind, bool Active, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt);
