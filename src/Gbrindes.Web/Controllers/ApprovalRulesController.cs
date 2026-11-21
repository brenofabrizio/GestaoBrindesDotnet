using Gbrindes.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Security.Claims;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Route("api/v1/approval-rules")]
public sealed class ApprovalRulesController(ApplicationDbContext database) : ControllerBase
{
    private static readonly HashSet<string> Criteria = ["quantidade", "valor", "categoria", "departamento", "perfil"];
    private static readonly HashSet<string> Operators = [">", ">=", "=", "in"];

    [HttpGet]
    [Authorize(Policy = "approval_rules.manage")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rules = await database.ApprovalRules.AsNoTracking()
            .Where(rule => rule.DeletedAt == null)
            .OrderBy(rule => rule.Priority).ThenBy(rule => rule.Id)
            .Select(rule => new
            {
                id = rule.Id,
                name = rule.Name,
                criterion = rule.Criterion,
                @operator = rule.Operator,
                value = rule.Value,
                approver_role_id = rule.ApproverRoleId,
                approver_user_id = rule.ApproverUserId,
                priority = rule.Priority,
                active = rule.Active
            })
            .ToListAsync(ct);
        return Ok(new { ok = true, data = rules });
    }

    [HttpPost]
    [Authorize(Policy = "approval_rules.manage")]
    public async Task<IActionResult> Create(CreateApprovalRule input, CancellationToken ct)
    {
        var name = input.Name?.Trim();
        var criterion = input.Criterion?.Trim();
        var operation = input.Operator?.Trim();
        var value = input.Value?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120)
            return ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Informe um nome com até 120 caracteres."] });
        if (criterion is null || !Criteria.Contains(criterion))
            return ValidationProblem(new Dictionary<string, string[]> { ["criterion"] = ["Critério inválido."] });
        if (operation is null || !Operators.Contains(operation))
            return ValidationProblem(new Dictionary<string, string[]> { ["operator"] = ["Operador inválido."] });
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255)
            return ValidationProblem(new Dictionary<string, string[]> { ["value"] = ["Informe um valor com até 255 caracteres."] });
        if (input.Priority is < 1 or > 9999)
            return ValidationProblem(new Dictionary<string, string[]> { ["priority"] = ["A prioridade deve estar entre 1 e 9999."] });
        if (input.ApproverRoleId is null && input.ApproverUserId is null)
            return ValidationProblem(new Dictionary<string, string[]> { ["approver_role_id"] = ["Informe o perfil ou o usuário aprovador."] });
        if (input.ApproverRoleId is Guid roleId && !await database.Roles.AnyAsync(role => role.Id == roleId, ct))
            return ValidationProblem(new Dictionary<string, string[]> { ["approver_role_id"] = ["Perfil aprovador inexistente."] });
        if (input.ApproverUserId is Guid userId && !await database.Users.AnyAsync(user => user.Id == userId && user.Active && user.DeletedAt == null, ct))
            return ValidationProblem(new Dictionary<string, string[]> { ["approver_user_id"] = ["Usuário aprovador inexistente ou inativo."] });

        var now = DateTimeOffset.UtcNow;
        var rule = new ApprovalRule
        {
            Name = name,
            Criterion = criterion,
            Operator = operation,
            Value = value,
            ApproverRoleId = input.ApproverRoleId,
            ApproverUserId = input.ApproverUserId,
            Priority = input.Priority,
            Active = input.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        database.ApprovalRules.Add(rule);
        database.AuditLogs.Add(CreateAudit("create", rule, now, null, RuleSnapshot(rule)));
        await database.SaveChangesAsync(ct);
        return StatusCode(StatusCodes.Status201Created, new { ok = true, data = Present(rule) });
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "approval_rules.manage")]
    public async Task<IActionResult> Update(Guid id, UpdateApprovalRule input, CancellationToken ct)
    {
        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational())
            transaction = await database.Database.BeginTransactionAsync(ct);
        try
        {
            var rule = await LoadForUpdateAsync(id, ct);
            if (rule is null)
                return NotFound();

            var name = rule.Name;
            var criterion = rule.Criterion;
            var operation = rule.Operator;
            var value = rule.Value;
            var approverRoleId = rule.ApproverRoleId;
            var approverUserId = rule.ApproverUserId;
            var priority = rule.Priority;
            var active = rule.Active;

            if (input.Name.ValueKind != JsonValueKind.Undefined)
            {
                if (input.Name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(input.Name.GetString()) || input.Name.GetString()!.Trim().Length > 120)
                    return ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Informe um nome com até 120 caracteres."] });
                name = input.Name.GetString()!.Trim();
            }
            if (input.Criterion.ValueKind != JsonValueKind.Undefined)
            {
                if (input.Criterion.ValueKind != JsonValueKind.String || !Criteria.Contains(input.Criterion.GetString() ?? string.Empty))
                    return ValidationProblem(new Dictionary<string, string[]> { ["criterion"] = ["Critério inválido."] });
                criterion = input.Criterion.GetString()!;
            }
            if (input.Operator.ValueKind != JsonValueKind.Undefined)
            {
                if (input.Operator.ValueKind != JsonValueKind.String || !Operators.Contains(input.Operator.GetString() ?? string.Empty))
                    return ValidationProblem(new Dictionary<string, string[]> { ["operator"] = ["Operador inválido."] });
                operation = input.Operator.GetString()!;
            }
            if (input.Value.ValueKind != JsonValueKind.Undefined)
            {
                if (input.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(input.Value.GetString()) || input.Value.GetString()!.Trim().Length > 255)
                    return ValidationProblem(new Dictionary<string, string[]> { ["value"] = ["Informe um valor com até 255 caracteres."] });
                value = input.Value.GetString()!.Trim();
            }
            if (!TryReadGuid(input.ApproverRoleId, approverRoleId, out approverRoleId) ||
                !TryReadGuid(input.ApproverUserId, approverUserId, out approverUserId))
                return ValidationProblem(new Dictionary<string, string[]> { ["approver_role_id"] = ["O aprovador informado é inválido."] });
            if (input.Priority.ValueKind != JsonValueKind.Undefined)
            {
                if (input.Priority.ValueKind == JsonValueKind.Null)
                    priority = 100;
                else if (!input.Priority.TryGetInt32(out priority) || priority is < 1 or > 9999)
                    return ValidationProblem(new Dictionary<string, string[]> { ["priority"] = ["A prioridade deve estar entre 1 e 9999."] });
            }
            if (input.Active.ValueKind != JsonValueKind.Undefined)
            {
                if (input.Active.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return ValidationProblem(new Dictionary<string, string[]> { ["active"] = ["O estado ativo deve ser verdadeiro ou falso."] });
                active = input.Active.GetBoolean();
            }
            if (approverRoleId is null && approverUserId is null)
                return ValidationProblem(new Dictionary<string, string[]> { ["approver_role_id"] = ["Mantenha pelo menos um aprovador: perfil ou usuário."] });
            if (approverRoleId is Guid roleId && !await database.Roles.AnyAsync(role => role.Id == roleId, ct))
                return ValidationProblem(new Dictionary<string, string[]> { ["approver_role_id"] = ["Perfil aprovador inexistente."] });
            if (approverUserId is Guid userId && !await database.Users.AnyAsync(user => user.Id == userId && user.Active && user.DeletedAt == null, ct))
                return ValidationProblem(new Dictionary<string, string[]> { ["approver_user_id"] = ["Usuário aprovador inexistente ou inativo."] });

            var before = RuleSnapshot(rule);
            rule.Name = name;
            rule.Criterion = criterion;
            rule.Operator = operation;
            rule.Value = value;
            rule.ApproverRoleId = approverRoleId;
            rule.ApproverUserId = approverUserId;
            rule.Priority = priority;
            rule.Active = active;
            rule.UpdatedAt = DateTimeOffset.UtcNow;
            var after = RuleSnapshot(rule);
            var (changedBefore, changedAfter) = Diff(before, after);
            if (changedAfter.Count > 0)
                database.AuditLogs.Add(CreateAudit("update", rule, rule.UpdatedAt, changedBefore, changedAfter));
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return Ok(new { ok = true, data = Present(rule) });
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

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "approval_rules.manage")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational())
            transaction = await database.Database.BeginTransactionAsync(ct);
        try
        {
            var rule = await LoadForUpdateAsync(id, ct);
            if (rule is null)
                return NotFound();
            var now = DateTimeOffset.UtcNow;
            var before = RuleSnapshot(rule);
            rule.Active = false;
            rule.DeletedAt = now;
            rule.UpdatedAt = now;
            database.AuditLogs.Add(CreateAudit("delete", rule, now, before, null));
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return Ok(new { ok = true, data = new { id = rule.Id, deleted = true } });
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

    private async Task<ApprovalRule?> LoadForUpdateAsync(Guid id, CancellationToken ct)
    {
        if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            return await database.ApprovalRules.FromSqlInterpolated($"SELECT * FROM \"ApprovalRules\" WHERE \"Id\" = {id} AND \"DeletedAt\" IS NULL FOR UPDATE").SingleOrDefaultAsync(ct);
        return await database.ApprovalRules.SingleOrDefaultAsync(rule => rule.Id == id && rule.DeletedAt == null, ct);
    }

    private static bool TryReadGuid(JsonElement element, Guid? current, out Guid? value)
    {
        value = current;
        if (element.ValueKind == JsonValueKind.Undefined)
            return true;
        if (element.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }
        if (element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out var parsed))
        {
            value = parsed;
            return true;
        }
        return false;
    }

    private static object Present(ApprovalRule rule) => new
    {
        id = rule.Id,
        rule.Name,
        rule.Criterion,
        @operator = rule.Operator,
        rule.Value,
        approver_role_id = rule.ApproverRoleId,
        approver_user_id = rule.ApproverUserId,
        rule.Priority,
        rule.Active
    };

    private AuditLog CreateAudit(string action, ApprovalRule rule, DateTimeOffset at, object? before, object? after)
    {
        var userAgent = Request.Headers.UserAgent.ToString();
        return new AuditLog
        {
            UserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : null,
            UserName = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "Sistema",
            Action = action,
            EntityType = "approval_rule",
            EntityId = rule.Id,
            EntityLabel = rule.Name.Length > 190 ? rule.Name[..190] : rule.Name,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before),
            AfterJson = after is null ? null : JsonSerializer.Serialize(after),
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = userAgent.Length > 255 ? userAgent[..255] : userAgent,
            CreatedAt = at
        };
    }

    private static Dictionary<string, object?> RuleSnapshot(ApprovalRule rule) => new()
    {
        ["name"] = rule.Name,
        ["criterion"] = rule.Criterion,
        ["operator"] = rule.Operator,
        ["value"] = rule.Value,
        ["approver_role_id"] = rule.ApproverRoleId,
        ["approver_user_id"] = rule.ApproverUserId,
        ["priority"] = rule.Priority,
        ["active"] = rule.Active
    };

    private static (Dictionary<string, object?> Before, Dictionary<string, object?> After) Diff(
        Dictionary<string, object?> before,
        Dictionary<string, object?> after)
    {
        var changedBefore = new Dictionary<string, object?>();
        var changedAfter = new Dictionary<string, object?>();
        foreach (var key in before.Keys)
        {
            if (Equals(before[key], after[key]))
                continue;
            changedBefore[key] = before[key];
            changedAfter[key] = after[key];
        }
        return (changedBefore, changedAfter);
    }

    private IActionResult ValidationProblem(Dictionary<string, string[]> errors) => UnprocessableEntity(new ValidationProblemDetails(errors));
}

public sealed class CreateApprovalRule
{
    public string? Name { get; set; }
    public string? Criterion { get; set; }
    public string? Operator { get; set; }
    public string? Value { get; set; }
    [JsonPropertyName("approver_role_id")] public Guid? ApproverRoleId { get; set; }
    [JsonPropertyName("approver_user_id")] public Guid? ApproverUserId { get; set; }
    public int Priority { get; set; } = 100;
    public bool Active { get; set; } = true;
}

public sealed class UpdateApprovalRule
{
    public JsonElement Name { get; set; }
    public JsonElement Criterion { get; set; }
    [JsonPropertyName("operator")] public JsonElement Operator { get; set; }
    public JsonElement Value { get; set; }
    [JsonPropertyName("approver_role_id")] public JsonElement ApproverRoleId { get; set; }
    [JsonPropertyName("approver_user_id")] public JsonElement ApproverUserId { get; set; }
    public JsonElement Priority { get; set; }
    public JsonElement Active { get; set; }
}
