using System.Security.Claims;
using System.Text.Json;
using Gbrindes.Web.Data;
using Gbrindes.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Authorize]
[Route("api/v1")]
public sealed class RolesController(
    ApplicationDbContext database,
    RoleManager<ApplicationRole> roles) : ControllerBase
{
    private sealed record PermissionDefinition(string Slug, string Name, string Module);

    // This is the permission vocabulary currently enforced by the .NET API. It is not
    // a merged or seeded role matrix; source matrices remain documented independently.
    private static readonly PermissionDefinition[] PermissionCatalog =
    [
        new("approval_rules.manage", "Gerenciar regras de aprovação", "Solicitações"),
        new("events.view", "Visualizar eventos", "Eventos"),
        new("events.manage", "Gerenciar eventos", "Eventos"),
        new("events.withdraw", "Registrar retirada de evento", "Eventos"),
        new("audit.view", "Consultar trilha de auditoria", "Auditoria"),
        new("dashboard.view", "Visualizar dashboard", "Dashboard"),
        new("deliveries.view", "Visualizar protocolos de entrega", "Entregas"),
        new("items.delete", "Excluir e restaurar brindes", "Brindes"),
        new("items.manage", "Cadastrar, editar e inativar brindes", "Brindes"),
        new("items.purge", "Excluir brindes definitivamente", "Brindes"),
        new("items.view", "Ver brindes", "Brindes"),
        new("import.run", "Importar planilhas", "Importação"),
        new("lookups.delete", "Excluir e restaurar cadastros", "Cadastros"),
        new("lookups.manage", "Gerenciar cadastros", "Cadastros"),
        new("lookups.purge", "Excluir cadastros definitivamente", "Cadastros"),
        new("lookups.view", "Ver cadastros", "Cadastros"),
        new("requests.approve", "Aprovar e reprovar solicitações", "Solicitações"),
        new("requests.cancel_any", "Cancelar qualquer solicitação", "Solicitações"),
        new("requests.create", "Criar solicitações", "Solicitações"),
        new("requests.process", "Separar e entregar solicitações", "Solicitações"),
        new("requests.view_all", "Ver todas as solicitações", "Solicitações"),
        new("requests.view_department", "Ver solicitações do departamento", "Solicitações"),
        new("requests.view_own", "Ver as próprias solicitações", "Solicitações"),
        new("reports.export", "Exportar relatórios", "Relatórios"),
        new("reports.view", "Visualizar relatórios", "Relatórios"),
        new("settings.manage", "Gerenciar configurações e marca", "Configurações"),
        new("roles.manage", "Gerenciar perfis e permissões", "Usuários"),
        new("stock.adjust", "Ajustar estoque manualmente", "Estoque"),
        new("stock.transfer", "Transferir estoque entre locais", "Estoque"),
        new("stock.entry", "Registrar entradas", "Estoque"),
        new("stock.exit", "Autorizar saídas", "Estoque"),
        new("stock.view", "Ver estoque e movimentações", "Estoque"),
        new("users.manage", "Gerenciar usuários", "Usuários"),
        new("users.view", "Ver usuários", "Usuários")
    ];

    [HttpGet("roles")]
    [Authorize(Policy = "roles.manage")]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(new { ok = true, data = await RoleViewsAsync(ct) });

    [HttpPut("roles/{id:guid}/metadata")]
    [Authorize(Policy = "roles.manage")]
    public async Task<IActionResult> UpdateMetadata(Guid id, RoleMetadataUpdateRequest request, CancellationToken ct)
    {
        var role = await roles.FindByIdAsync(id.ToString());
        if (role is null)
            return NotFound(new { ok = false, error = "NOT_FOUND", message = "Perfil não encontrado." });
        if (string.Equals(role.Name, "admin", StringComparison.OrdinalIgnoreCase))
            return Conflict(new { ok = false, error = "ROLE_NOT_EDITABLE", message = "O perfil Administrador não pode ser editado." });
        if (await roles.Roles.AnyAsync(candidate => candidate.Id != id && candidate.Slug == request.Slug, ct))
            return InvalidMetadata("Já existe um perfil com este identificador.");

        var previousSlug = role.Slug;
        var previousDescription = role.Description;
        var changed = previousSlug != request.Slug || previousDescription != request.Description;
        role.Slug = request.Slug;
        role.Description = request.Description;

        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational())
            transaction = await database.Database.BeginTransactionAsync(ct);
        try
        {
            var result = await roles.UpdateAsync(role);
            if (!result.Succeeded)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(ct);
                return IdentityFailure(result.Errors);
            }

            if (changed)
            {
                var actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedActorId)
                    ? parsedActorId
                    : (Guid?)null;
                var agent = Request.Headers.UserAgent.ToString();
                database.AuditLogs.Add(new AuditLog
                {
                    UserId = actorId,
                    UserName = User.FindFirstValue(ClaimTypes.Name),
                    Action = "metadata_update",
                    EntityType = "role",
                    EntityId = role.Id,
                    EntityLabel = role.Name,
                    BeforeJson = JsonSerializer.Serialize(new { slug = previousSlug, description = previousDescription }),
                    AfterJson = JsonSerializer.Serialize(new { slug = role.Slug, description = role.Description }),
                    Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                    UserAgent = agent.Length > 255 ? agent[..255] : agent,
                    CreatedAt = DateTimeOffset.UtcNow
                });
                await database.SaveChangesAsync(ct);
            }
            if (transaction is not null)
                await transaction.CommitAsync(ct);

            var permissions = (await roles.GetClaimsAsync(role))
                .Where(claim => claim.Type == "permission")
                .Select(claim => claim.Value).Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var usersCount = await database.UserRoles.CountAsync(link => link.RoleId == role.Id, ct);
            return Ok(new
            {
                ok = true,
                data = new
                {
                    id = role.Id,
                    slug = role.Slug,
                    name = role.Name,
                    description = role.Description,
                    users_count = usersCount,
                    editable = true,
                    permissions
                }
            });
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(ct);
            role.Slug = previousSlug;
            role.Description = previousDescription;
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    [HttpGet("permissions")]
    [Authorize(Policy = "roles.manage")]
    public IActionResult Permissions()
    {
        var groups = PermissionCatalog.GroupBy(permission => permission.Module)
            .OrderBy(group => group.Key)
            .Select(group => new
            {
                module = group.Key,
                permissions = group.OrderBy(permission => permission.Slug)
                    .Select(permission => new { slug = permission.Slug, name = permission.Name }).ToArray()
            }).ToArray();
        return Ok(new { ok = true, data = groups });
    }

    [HttpPut("roles/{id:guid}/permissions")]
    [Authorize(Policy = "roles.manage")]
    public async Task<IActionResult> UpdatePermissions(Guid id, RolePermissionUpdateRequest request, CancellationToken ct)
    {
        var role = await roles.FindByIdAsync(id.ToString());
        if (role is null)
            return NotFound(new { ok = false, error = "NOT_FOUND", message = "Perfil não encontrado." });
        if (string.Equals(role.Name, "admin", StringComparison.OrdinalIgnoreCase))
            return Conflict(new { ok = false, error = "ROLE_NOT_EDITABLE", message = "O perfil Administrador não pode ser editado." });
        if (request.Permissions is null || request.Permissions.Any(permission => permission is null))
            return InvalidPermissions("Informe a lista de permissões.");

        var requested = request.Permissions.Distinct(StringComparer.Ordinal).OrderBy(permission => permission, StringComparer.Ordinal).ToArray();
        var unknown = requested.Except(PermissionCatalog.Select(permission => permission.Slug), StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
            return InvalidPermissions($"Permissão desconhecida: {string.Join(", ", unknown)}.");

        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational())
            transaction = await database.Database.BeginTransactionAsync(ct);
        try
        {
            var before = (await roles.GetClaimsAsync(role))
                .Where(claim => claim.Type == "permission")
                .Select(claim => claim.Value).Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            foreach (var claim in await roles.GetClaimsAsync(role))
            {
                if (claim.Type == "permission")
                {
                    var removed = await roles.RemoveClaimAsync(role, claim);
                    if (!removed.Succeeded)
                        return IdentityFailure(removed.Errors);
                }
            }
            foreach (var permission in requested)
            {
                var added = await roles.AddClaimAsync(role, new Claim("permission", permission));
                if (!added.Succeeded)
                    return IdentityFailure(added.Errors);
            }

            Guid? actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedActorId)
                ? parsedActorId
                : null;
            database.AuditLogs.Add(new AuditLog
            {
                UserId = actorId,
                UserName = User.FindFirstValue(ClaimTypes.Name),
                Action = "permissions_update",
                EntityType = "role",
                EntityId = role.Id,
                EntityLabel = role.Name,
                BeforeJson = JsonSerializer.Serialize(new { permissions = before }),
                AfterJson = JsonSerializer.Serialize(new { permissions = requested }),
                Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = Request.Headers.UserAgent.ToString() is { Length: > 255 } agent ? agent[..255] : Request.Headers.UserAgent.ToString(),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return Ok(new { ok = true, data = await RoleViewsAsync(ct) });
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(ct);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private async Task<object[]> RoleViewsAsync(CancellationToken ct)
    {
        var roleList = await roles.Roles.AsNoTracking().OrderBy(role => role.Name).ToListAsync(ct);
        var counts = await (from link in database.UserRoles.AsNoTracking()
                            join user in database.Users.AsNoTracking() on link.UserId equals user.Id
                            where user.DeletedAt == null
                            group link by link.RoleId into grouped
                            select new { RoleId = grouped.Key, Count = grouped.Count() })
            .ToDictionaryAsync(item => item.RoleId, item => item.Count, ct);
        var result = new List<object>(roleList.Count);
        foreach (var role in roleList)
        {
            var permissions = (await roles.GetClaimsAsync(role))
                .Where(claim => claim.Type == "permission")
                .Select(claim => claim.Value).Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            result.Add(new
            {
                id = role.Id,
                slug = role.Slug,
                name = role.Name,
                description = role.Description,
                users_count = counts.GetValueOrDefault(role.Id),
                editable = !string.Equals(role.Name, "admin", StringComparison.OrdinalIgnoreCase),
                permissions
            });
        }
        return result.ToArray();
    }

    private IActionResult InvalidPermissions(string message) => UnprocessableEntity(new
    {
        ok = false,
        errors = new Dictionary<string, string[]> { ["permissions"] = [message] }
    });

    private IActionResult InvalidMetadata(string message) => UnprocessableEntity(new
    {
        ok = false,
        errors = new Dictionary<string, string[]> { ["slug"] = [message] }
    });

    private IActionResult IdentityFailure(IEnumerable<IdentityError> errors) => Conflict(new
    {
        ok = false,
        error = "IDENTITY_ERROR",
        message = string.Join(" ", errors.Select(error => error.Description))
    });
}
