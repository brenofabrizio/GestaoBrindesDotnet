using System.Globalization;
using System.Text.Json;
using Gbrindes.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Authorize(Policy = "audit.view")]
[Route("api/v1/audit")]
public sealed class AuditController(ApplicationDbContext database) : ControllerBase
{
    private static readonly IReadOnlyDictionary<string, string> ActionLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["create"] = "Cadastro",
        ["update"] = "Alteração",
        ["delete"] = "Exclusão (lixeira)",
        ["restore"] = "Restauração",
        ["purge"] = "Exclusão definitiva",
        ["activate"] = "Ativação",
        ["deactivate"] = "Inativação",
        ["login"] = "Login",
        ["logout"] = "Logout",
        ["password_change"] = "Troca de senha",
        ["password_reset"] = "Redefinição de senha",
        ["photo_update"] = "Foto alterada",
        ["photo_delete"] = "Foto removida",
        ["permissions_update"] = "Permissões alteradas",
        ["settings_update"] = "Configurações alteradas",
        ["stock_entrada"] = "Entrada de estoque",
        ["stock_saida"] = "Saída de estoque",
        ["stock_ajuste"] = "Ajuste de estoque",
        ["stock_reserve"] = "Reserva de estoque",
        ["stock_release"] = "Liberação de reserva",
        ["stock_exit_authorize"] = "Saída autorizada",
        ["stock_exit_confirm"] = "Saída confirmada no CD",
        ["submit"] = "Envio",
        ["approve"] = "Aprovação",
        ["reject"] = "Reprovação",
        ["cancel"] = "Cancelamento",
        ["start_picking"] = "Início da separação",
        ["ready"] = "Pronta para retirada",
        ["deliver"] = "Entrega",
        ["open"] = "Abertura",
        ["close"] = "Encerramento",
        ["withdraw"] = "Retirada"
    };

    private static readonly IReadOnlyDictionary<string, string> EntityLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["user"] = "Usuário",
        ["role"] = "Perfil",
        ["item"] = "Brinde",
        ["category"] = "Categoria",
        ["department"] = "Departamento",
        ["industry"] = "Indústria",
        ["location"] = "Local",
        ["supplier"] = "Fornecedor",
        ["settings"] = "Configurações",
        ["request"] = "Solicitação",
        ["event"] = "Evento",
        ["delivery"] = "Protocolo",
        ["approval_rule"] = "Regra de aprovação",
        ["stock_exit_order"] = "Saída autorizada"
    };

    [HttpGet]
    [HttpGet("~/api/audit")]
    public async Task<IActionResult> List(
        [FromQuery(Name = "entity_type")] string? entityType,
        [FromQuery(Name = "entity_id")] string? entityId,
        [FromQuery(Name = "user_id")] string? userId,
        [FromQuery] string? action,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? q,
        [FromQuery] int page = 1,
        [FromQuery(Name = "per_page")] int perPage = 50,
        CancellationToken ct = default)
    {
        var query = database.AuditLogs.AsNoTracking();
        if (!string.IsNullOrEmpty(entityType)) query = query.Where(log => log.EntityType == entityType);
        if (Guid.TryParse(entityId, out var parsedEntityId)) query = query.Where(log => log.EntityId == parsedEntityId);
        if (Guid.TryParse(userId, out var parsedUserId)) query = query.Where(log => log.UserId == parsedUserId);
        if (!string.IsNullOrEmpty(action)) query = query.Where(log => log.Action == action);
        if (DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromDate))
        {
            var start = new DateTimeOffset(fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            query = query.Where(log => log.CreatedAt >= start);
        }
        if (DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var toDate))
        {
            if (toDate != DateOnly.MaxValue)
            {
                var end = new DateTimeOffset(toDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
                query = query.Where(log => log.CreatedAt < end);
            }
        }
        if (!string.IsNullOrEmpty(q))
        {
            var term = q.ToLowerInvariant();
            query = query.Where(log => (log.EntityLabel != null && log.EntityLabel.ToLower().Contains(term))
                || (log.UserName != null && log.UserName.ToLower().Contains(term)));
        }

        perPage = perPage == 0 ? 50 : Math.Clamp(perPage, 1, 100);
        page = Math.Clamp(page, 1, int.MaxValue / perPage);
        var total = await query.CountAsync(ct);
        var entries = await query.OrderByDescending(log => log.CreatedAt).ThenByDescending(log => log.Id)
            .Skip((page - 1) * perPage).Take(perPage).ToListAsync(ct);
        return Ok(new
        {
            ok = true,
            data = entries.Select(Present),
            meta = new
            {
                page,
                per_page = perPage,
                total,
                last_page = Math.Max(1, (int)Math.Ceiling((double)total / perPage)),
                filters = new { actions = ActionLabels, entities = EntityLabels }
            }
        });
    }

    [HttpGet("{id:guid}")]
    [HttpGet("~/api/audit/{id:guid}")]
    public async Task<IActionResult> Show(Guid id, CancellationToken ct)
    {
        var entry = await database.AuditLogs.AsNoTracking().SingleOrDefaultAsync(log => log.Id == id, ct);
        if (entry is null)
            return NotFound(new { ok = false, error = "NOT_FOUND", message = "Registro de auditoria não encontrado." });
        return Ok(new { ok = true, data = Present(entry) });
    }

    private static object Present(AuditLog entry) => new
    {
        id = entry.Id,
        action = entry.Action,
        action_label = ActionLabels.GetValueOrDefault(entry.Action, entry.Action),
        entity_type = entry.EntityType,
        entity_label = EntityLabels.GetValueOrDefault(entry.EntityType, entry.EntityType),
        entity_id = entry.EntityId,
        entity_name = entry.EntityLabel,
        user = new { id = entry.UserId, name = entry.UserName },
        before = ParseJson(entry.BeforeJson),
        after = ParseJson(entry.AfterJson),
        ip = entry.Ip,
        user_agent = entry.UserAgent,
        created_at = entry.CreatedAt
    };

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
