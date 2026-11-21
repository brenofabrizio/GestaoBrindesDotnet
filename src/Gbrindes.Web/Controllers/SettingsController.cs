using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Gbrindes.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Gbrindes.Web.Storage;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Route("api/v1/settings")]
public sealed class SettingsController(ApplicationDbContext database, BrandingLogoStorage logoStorage) : ControllerBase
{
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.Ordinal)
    {
        ["company_name"] = "Controle de Brindes",
        ["primary_color"] = "#2563EB"
    };

    private static readonly HashSet<string> EditableKeys = ["company_name", "primary_color"];

    [HttpGet("public")]
    [HttpGet("~/api/settings/public")]
    public async Task<IActionResult> Public(CancellationToken ct) => Ok(new
    {
        ok = true,
        data = await ViewAsync(ct)
    });

    [HttpGet("logo")]
    [HttpGet("~/api/settings/logo")]
    public async Task<IActionResult> Logo(CancellationToken ct)
    {
        var relativePath = await database.ApplicationSettings.AsNoTracking()
            .Where(setting => setting.Key == "logo_path")
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(relativePath) || logoStorage.OpenRead(relativePath) is not { } stream)
            return NotFound(new { ok = false, error = "Logo não configurado." });

        Response.Headers.CacheControl = "public, max-age=86400";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(stream, "image/png");
    }

    [HttpPost("logo")]
    [HttpPost("~/api/settings/logo")]
    [Authorize(Policy = "settings.manage")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(BrandingLogoStorage.MaximumUploadBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = BrandingLogoStorage.MaximumUploadBytes + 64 * 1024)]
    public async Task<IActionResult> UploadLogo([FromForm(Name = "logo")] IFormFile? logo, CancellationToken ct)
    {
        if (logo is null)
            return ValidationError("logo", "Selecione uma imagem.");

        string newPath;
        try
        {
            newPath = await logoStorage.StoreAsync(logo, ct);
        }
        catch (BrandingLogoValidationException exception)
        {
            return ValidationError("logo", exception.Message);
        }

        var oldPath = await database.ApplicationSettings.AsNoTracking()
            .Where(setting => setting.Key == "logo_path")
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(ct);
        try
        {
            await SaveLogoPathAsync(newPath, ct);
        }
        catch
        {
            logoStorage.Delete(newPath);
            throw;
        }
        if (!string.Equals(oldPath, newPath, StringComparison.Ordinal)) logoStorage.Delete(oldPath);
        return Ok(new { ok = true, data = await ViewAsync(ct) });
    }

    [HttpDelete("logo")]
    [HttpDelete("~/api/settings/logo")]
    [Authorize(Policy = "settings.manage")]
    public async Task<IActionResult> DeleteLogo(CancellationToken ct)
    {
        var oldPath = await database.ApplicationSettings.AsNoTracking()
            .Where(setting => setting.Key == "logo_path")
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(oldPath))
        {
            await SaveLogoPathAsync(string.Empty, ct);
            logoStorage.Delete(oldPath);
        }
        return Ok(new { ok = true, data = await ViewAsync(ct) });
    }

    [HttpGet]
    [HttpGet("~/api/settings")]
    [Authorize(Policy = "settings.manage")]
    public async Task<IActionResult> Index(CancellationToken ct) => Ok(new { ok = true, data = await ViewAsync(ct) });

    [HttpPut]
    [HttpPut("~/api/settings")]
    [Authorize(Policy = "settings.manage")]
    public async Task<IActionResult> Update(JsonElement input, CancellationToken ct)
    {
        if (input.ValueKind != JsonValueKind.Object)
            return ValidationError("body", "O corpo deve ser um objeto JSON.");
        var errors = new Dictionary<string, string[]>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in input.EnumerateObject())
        {
            if (!EditableKeys.Contains(property.Name))
            {
                errors[property.Name] = ["Configuração não editável."];
                continue;
            }
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                errors[property.Name] = ["Informe um valor textual."];
                continue;
            }
            var value = property.Value.GetString()!.Trim();
            if (property.Name == "company_name")
            {
                if (value.Length is < 1 or > 100)
                    errors[property.Name] = ["O nome da empresa deve conter entre 1 e 100 caracteres."];
                else
                    values[property.Name] = value;
            }
            else if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            {
                errors[property.Name] = ["Informe uma cor hexadecimal no formato #RRGGBB."];
            }
            else
            {
                values[property.Name] = value.ToUpperInvariant();
            }
        }
        if (errors.Count > 0) return UnprocessableEntity(new { ok = false, errors });
        if (values.Count == 0) return ValidationError("body", "Informe ao menos uma configuração editável.");

        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational()) transaction = await database.Database.BeginTransactionAsync(ct);
        try
        {
            var currentRows = await database.ApplicationSettings.Where(setting => EditableKeys.Contains(setting.Key)).ToListAsync(ct);
            var before = Defaults.ToDictionary(entry => entry.Key, entry => currentRows.FirstOrDefault(row => row.Key == entry.Key)?.Value ?? entry.Value, StringComparer.Ordinal);
            var beforeChanges = new Dictionary<string, string>(StringComparer.Ordinal);
            var afterChanges = new Dictionary<string, string>(StringComparer.Ordinal);
            var actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedActorId) ? parsedActorId : (Guid?)null;
            var now = DateTimeOffset.UtcNow;
            foreach (var (key, value) in values)
            {
                if (before[key] == value) continue;
                beforeChanges[key] = before[key];
                afterChanges[key] = value;
                var row = currentRows.FirstOrDefault(setting => setting.Key == key);
                if (row is null)
                    database.ApplicationSettings.Add(new ApplicationSetting { Key = key, Value = value, UpdatedBy = actorId, UpdatedAt = now });
                else
                {
                    row.Value = value;
                    row.UpdatedBy = actorId;
                    row.UpdatedAt = now;
                }
            }
            if (afterChanges.Count > 0)
            {
                var agent = Request.Headers.UserAgent.ToString();
                database.AuditLogs.Add(new AuditLog
                {
                    UserId = actorId,
                    UserName = User.FindFirstValue(ClaimTypes.Name),
                    Action = "settings_update",
                    EntityType = "settings",
                    EntityLabel = "Configurações",
                    BeforeJson = JsonSerializer.Serialize(beforeChanges),
                    AfterJson = JsonSerializer.Serialize(afterChanges),
                    Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                    UserAgent = agent.Length > 255 ? agent[..255] : agent,
                    CreatedAt = now
                });
                await database.SaveChangesAsync(ct);
            }
            if (transaction is not null) await transaction.CommitAsync(ct);
            return Ok(new { ok = true, data = await ViewAsync(ct) });
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(ct);
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    private async Task<object> ViewAsync(CancellationToken ct)
    {
        Dictionary<string, string> values;
        try
        {
            values = await database.ApplicationSettings.AsNoTracking()
                .Where(setting => EditableKeys.Contains(setting.Key) || setting.Key == "logo_path")
                .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, ct);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested && exception is DbException or InvalidOperationException)
        {
            values = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        return new
        {
            company_name = values.GetValueOrDefault("company_name", Defaults["company_name"]),
            primary_color = values.GetValueOrDefault("primary_color", Defaults["primary_color"]),
            logo_url = values.GetValueOrDefault("logo_path") is { Length: > 0 } logoPath
                ? BrandingLogoStorage.PublicUrl(logoPath)
                : null
        };
    }

    private async Task SaveLogoPathAsync(string newPath, CancellationToken ct)
    {
        IDbContextTransaction? transaction = null;
        if (database.Database.IsRelational()) transaction = await database.Database.BeginTransactionAsync(ct);
        try
        {
            var setting = await database.ApplicationSettings.SingleOrDefaultAsync(row => row.Key == "logo_path", ct);
            var before = setting?.Value ?? string.Empty;
            var actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedActorId) ? parsedActorId : (Guid?)null;
            var now = DateTimeOffset.UtcNow;
            if (setting is null)
                database.ApplicationSettings.Add(new ApplicationSetting { Key = "logo_path", Value = newPath, UpdatedBy = actorId, UpdatedAt = now });
            else
            {
                setting.Value = newPath;
                setting.UpdatedBy = actorId;
                setting.UpdatedAt = now;
            }

            if (!string.Equals(before, newPath, StringComparison.Ordinal))
            {
                var agent = Request.Headers.UserAgent.ToString();
                database.AuditLogs.Add(new AuditLog
                {
                    UserId = actorId,
                    UserName = User.FindFirstValue(ClaimTypes.Name),
                    Action = "settings_update",
                    EntityType = "settings",
                    EntityLabel = "Configurações",
                    BeforeJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["logo_path"] = before }),
                    AfterJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["logo_path"] = newPath }),
                    Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                    UserAgent = agent.Length > 255 ? agent[..255] : agent,
                    CreatedAt = now
                });
                await database.SaveChangesAsync(ct);
            }

            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(ct);
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    private IActionResult ValidationError(string field, string message) =>
        UnprocessableEntity(new { ok = false, errors = new Dictionary<string, string[]> { [field] = [message] } });
}
