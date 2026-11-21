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
[Route("api/v1/departments")]
public sealed class DepartmentsController(ApplicationDbContext database) : ControllerBase
{
    [HttpGet, Authorize(Policy = "lookups.view")]
    public async Task<IActionResult> List([FromQuery] string? q, [FromQuery] string? active, [FromQuery] string? trashed,
        [FromQuery] string? all, [FromQuery] int page = 1, [FromQuery(Name = "per_page")] int perPage = 25, CancellationToken ct = default)
    {
        var includeTrash = IsTrue(trashed);
        var query = database.Departments.AsNoTracking().Where(x => includeTrash ? x.DeletedAt != null : x.DeletedAt == null);
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
            var optionDepartments = await ordered.Where(x => x.DeletedAt == null && x.Active).ToListAsync(ct);
            return Ok(optionDepartments.Select(ToView));
        }

        page = Math.Clamp(page, 1, 1_000_000);
        perPage = Math.Clamp(perPage, 1, 100);
        var total = await query.CountAsync(ct);
        var pageDepartments = await ordered.Skip((page - 1) * perPage).Take(perPage).ToListAsync(ct);
        var items = pageDepartments.Select(ToView);
        return Ok(new { items, page, per_page = perPage, total });
    }

    [HttpGet("{id:guid}"), Authorize(Policy = "lookups.view")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var department = await database.Departments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return department is null ? NotFound() : Ok(ToView(department));
    }

    [HttpPost, Authorize(Policy = "lookups.manage")]
    public async Task<IActionResult> Create(DepartmentCreateRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return Invalid("name", "O nome é obrigatório.");
        if (name.Length > 100) return Invalid("name", "O nome deve ter no máximo 100 caracteres.");
        var description = request.Description?.Trim();
        if (description?.Length > 255) return Invalid("description", "A descrição deve ter no máximo 255 caracteres.");
        var key = Normalize(name);
        if (await database.Departments.AnyAsync(x => x.DeletedAt == null && x.NameKey == key, ct))
            return Invalid("name", "Já existe um registro com este nome.");

        var department = new Department
        {
            Name = name,
            NameKey = key,
            Description = description,
            Active = request.Active ?? true
        };
        var actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var createdBy) ? createdBy : (Guid?)null;
        department.CreatedBy = actorId;
        department.UpdatedBy = actorId;
        database.Departments.Add(department);
        AddAudit("create", department, null, new { name = department.Name, description = department.Description, active = department.Active });
        await database.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = department.Id }, ToView(department));
    }

    [HttpPut("{id:guid}"), Authorize(Policy = "lookups.manage")]
    public async Task<IActionResult> Update(Guid id, DepartmentUpdateRequest request, CancellationToken ct)
    {
        var department = await database.Departments.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (department is null) return NotFound();
        var oldName = department.Name;
        var oldDescription = department.Description;
        var oldActive = department.Active;

        if (request.Name.ValueKind != JsonValueKind.Undefined)
        {
            if (request.Name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(request.Name.GetString()))
                return Invalid("name", "O nome é obrigatório.");
            var name = request.Name.GetString()!.Trim();
            if (name.Length > 100) return Invalid("name", "O nome deve ter no máximo 100 caracteres.");
            var key = Normalize(name);
            if (await database.Departments.AnyAsync(x => x.Id != id && x.DeletedAt == null && x.NameKey == key, ct))
                return Invalid("name", "Já existe um registro com este nome.");
            department.Name = name;
            department.NameKey = key;
        }

        if (request.Description.ValueKind != JsonValueKind.Undefined)
        {
            if (request.Description.ValueKind == JsonValueKind.Null)
                department.Description = null;
            else if (request.Description.ValueKind == JsonValueKind.String)
            {
                var description = request.Description.GetString()?.Trim();
                if (description?.Length > 255) return Invalid("description", "A descrição deve ter no máximo 255 caracteres.");
                department.Description = description;
            }
            else return Invalid("description", "A descrição deve ser texto ou nula.");
        }
        if (request.Active is not null) department.Active = request.Active.Value;
        var before = new Dictionary<string, object?>();
        var after = new Dictionary<string, object?>();
        if (oldName != department.Name) { before["name"] = oldName; after["name"] = department.Name; }
        if (oldDescription != department.Description) { before["description"] = oldDescription; after["description"] = department.Description; }
        if (oldActive != department.Active) { before["active"] = oldActive; after["active"] = department.Active; }
        var now = DateTimeOffset.UtcNow;
        department.UpdatedAt = now;
        if (after.Count > 0)
        {
            department.UpdatedBy = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var updatedBy) ? updatedBy : null;
            AddAudit("update", department, before, after, now);
        }
        await database.SaveChangesAsync(ct);
        return Ok(ToView(department));
    }

    [HttpPost("{id:guid}/activate"), Authorize(Policy = "lookups.manage")]
    public Task<IActionResult> Activate(Guid id, CancellationToken ct) => SetActive(id, true, ct);

    [HttpPost("{id:guid}/deactivate"), Authorize(Policy = "lookups.manage")]
    public Task<IActionResult> Deactivate(Guid id, CancellationToken ct) => SetActive(id, false, ct);

    [HttpDelete("{id:guid}"), Authorize(Policy = "lookups.delete")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var department = await database.Departments.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (department is null) return NotFound();
        var before = Snapshot(department);
        department.DeletedAt = DateTimeOffset.UtcNow;
        department.UpdatedAt = department.DeletedAt.Value;
        AddAudit("delete", department, before, null, department.UpdatedAt);
        await database.SaveChangesAsync(ct);
        return Ok(new { id, deleted = true });
    }

    [HttpPost("{id:guid}/restore"), Authorize(Policy = "lookups.delete")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    {
        var department = await database.Departments.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt != null, ct);
        if (department is null) return NotFound();
        if (await database.Departments.AnyAsync(x => x.Id != id && x.DeletedAt == null && x.NameKey == department.NameKey, ct))
            return Invalid("name", "Já existe um registro com este nome.");
        department.DeletedAt = null;
        department.UpdatedAt = DateTimeOffset.UtcNow;
        AddAudit("restore", department, null, null, department.UpdatedAt);
        await database.SaveChangesAsync(ct);
        return Ok(ToView(department));
    }

    [HttpPost("{id:guid}/purge"), Authorize(Policy = "lookups.purge")]
    public async Task<IActionResult> Purge(Guid id, DepartmentPurgeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Confirm))
            return Invalid("confirm", "Confirmação obrigatória.");

        IDbContextTransaction? transaction = null;
        try
        {
            if (database.Database.IsRelational())
                transaction = await database.Database.BeginTransactionAsync(ct);

            Department? department;
            if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
                department = await database.Departments.FromSqlInterpolated(
                    $"SELECT * FROM \"Departments\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
            else
                department = await database.Departments.SingleOrDefaultAsync(value => value.Id == id, ct);

            if (department is null) return NotFound();
            if (department.DeletedAt is null) return Conflict(new { error = "NOT_IN_TRASH" });
            if (!string.Equals(request.Confirm.Trim(), department.Name, StringComparison.OrdinalIgnoreCase))
                return Invalid("confirm", $"Digite \"{department.Name}\" para confirmar a exclusão definitiva.");

            var uses = await ReferencesAsync(id, ct);
            if (uses.Count > 0)
                return Conflict(new { error = "IN_USE", uses });

            var now = DateTimeOffset.UtcNow;
            var agent = Request.Headers.UserAgent.ToString();
            database.AuditLogs.Add(new AuditLog
            {
                UserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ? actorId : null,
                UserName = User.FindFirstValue(ClaimTypes.Name),
                Action = "purge",
                EntityType = "department",
                EntityId = department.Id,
                EntityLabel = department.Name,
                BeforeJson = JsonSerializer.Serialize(new { id = department.Id, name = department.Name, description = department.Description, active = department.Active, deleted_at = department.DeletedAt }),
                Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = agent.Length > 255 ? agent[..255] : agent,
                CreatedAt = now
            });
            database.Departments.Remove(department);
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return Ok(new { id, purged = true });
        }
        catch (DbUpdateException) when (transaction is not null)
        {
            await transaction.RollbackAsync(ct);
            database.ChangeTracker.Clear();
            var uses = await ReferencesAsync(id, ct);
            if (uses.Count > 0)
                return Conflict(new { error = "IN_USE", uses });
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private async Task<IActionResult> SetActive(Guid id, bool active, CancellationToken ct)
    {
        var department = await database.Departments.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (department is null) return NotFound();
        if (department.Active == active) return Ok(ToView(department));
        var previous = department.Active;
        department.Active = active;
        var now = DateTimeOffset.UtcNow;
        department.UpdatedAt = now;
        department.UpdatedBy = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var updatedBy) ? updatedBy : null;
        AddAudit(active ? "activate" : "deactivate", department, new { active = previous }, new { active }, now);
        await database.SaveChangesAsync(ct);
        return Ok(ToView(department));
    }

    private void AddAudit(string action, Department department, object? before, object? after, DateTimeOffset? createdAt = null)
    {
        var agent = Request.Headers.UserAgent.ToString();
        database.AuditLogs.Add(new AuditLog
        {
            UserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ? actorId : null,
            UserName = User.FindFirstValue(ClaimTypes.Name) ?? "Sistema",
            Action = action,
            EntityType = "department",
            EntityId = department.Id,
            EntityLabel = department.Name.Length > 190 ? department.Name[..190] : department.Name,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before),
            AfterJson = after is null ? null : JsonSerializer.Serialize(after),
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = agent.Length > 255 ? agent[..255] : agent,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow
        });
    }

    private static object Snapshot(Department department) => new
    {
        id = department.Id,
        name = department.Name,
        description = department.Description,
        active = department.Active,
        deleted_at = department.DeletedAt
    };

    private IActionResult Invalid(string field, string message) =>
        UnprocessableEntity(new ValidationProblemDetails(new Dictionary<string, string[]> { [field] = [message] }));

    private async Task<List<string>> ReferencesAsync(Guid id, CancellationToken ct)
    {
        var uses = new List<string>();
        if (await database.Users.AnyAsync(user => user.DepartmentId == id, ct)) uses.Add("usuário(s)");
        if (await database.Requests.AnyAsync(request => request.DepartmentId == id, ct)) uses.Add("solicitação(ões)");
        if (await database.StockExitOrders.AnyAsync(order => order.DepartmentId == id, ct)) uses.Add("saída(s) autorizada(s)");
        if (await database.StockMovements.AnyAsync(movement => movement.DepartmentId == id, ct)) uses.Add("movimentação(ões) de estoque");
        return uses;
    }

    private static DepartmentView ToView(Department x) => new(x.Id, x.Name, x.Description, x.Active, x.CreatedAt, x.UpdatedAt, x.DeletedAt);
    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static bool IsTrue(string? value) => value is "1" or "true";
}

public sealed class DepartmentCreateRequest
{
    [StringLength(100)] public string? Name { get; set; }
    [StringLength(255)] public string? Description { get; set; }
    public bool? Active { get; set; }
}

public sealed class DepartmentUpdateRequest
{
    public JsonElement Name { get; set; }
    public JsonElement Description { get; set; }
    public bool? Active { get; set; }
}

public sealed class DepartmentPurgeRequest
{
    [StringLength(100)] public string? Confirm { get; set; }
}

public sealed record DepartmentView(Guid Id, string Name, string? Description, bool Active, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt);
