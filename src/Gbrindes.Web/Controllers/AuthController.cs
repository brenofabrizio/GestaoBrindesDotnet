using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using Gbrindes.Web.Authentication;
using Gbrindes.Web.Data;
using Gbrindes.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(
    UserManager<ApplicationUser> users,
    RoleManager<ApplicationRole> roles,
    ApplicationDbContext database,
    IConfiguration configuration) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("login")]
    [EnableRateLimiting("auth-login-ip")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim();
        var user = await users.FindByEmailAsync(email);
        if (user is null || !user.Active || user.DeletedAt is not null)
            return Unauthorized(new { message = "E-mail ou senha inválidos." });

        if (await users.IsLockedOutAsync(user))
            return StatusCode(StatusCodes.Status429TooManyRequests, new { message = "Muitas tentativas de login. Aguarde antes de tentar novamente." });

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            return Unauthorized(new { message = "E-mail ou senha inválidos." });
        }

        await users.ResetAccessFailedCountAsync(user);
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await users.UpdateAsync(user);

        var token = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var lifetimeMinutes = Math.Clamp(configuration.GetValue("Auth:TokenExpirationMinutes", 480), 5, 1440);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(lifetimeMinutes);
        database.AuthSessions.Add(new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = AccessTokenHash.Compute(token),
            SecurityStamp = user.SecurityStamp ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt
        });
        await database.SaveChangesAsync(cancellationToken);

        return Ok(new AuthResponse(
            token,
            "Bearer",
            expiresAt,
            ToSummary(user),
            await GetPermissionsAsync(user),
            user.MustChangePassword,
            await users.IsInRoleAsync(user, "admin")));
    }

    [AllowAnonymous]
    [HttpPost("forgot-password")]
    [EnableRateLimiting("auth-login-ip")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        const string genericMessage = "Se o e-mail estiver cadastrado, você receberá instruções para redefinir sua senha.";
        var email = request.Email.Trim();
        var user = await users.FindByEmailAsync(email);
        if (user is null || !user.Active || user.DeletedAt is not null)
            return Ok(new { message = genericMessage });

        var now = DateTimeOffset.UtcNow;
        var recent = await database.PasswordResetTokens.AnyAsync(token => token.UserId == user.Id && token.CreatedAt > now.AddMinutes(-5), cancellationToken);
        if (!recent)
        {
            var rawToken = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var reset = new Data.PasswordResetToken
            {
                UserId = user.Id,
                TokenHash = AccessTokenHash.Compute(rawToken),
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(Math.Clamp(configuration.GetValue("Auth:PasswordResetExpirationMinutes", 60), 15, 1440))
            };
            database.PasswordResetTokens.Add(reset);
            database.Notifications.Add(new Data.Notification
            {
                UserId = user.Id,
                Channel = "email",
                ToEmail = user.Email,
                Subject = "Redefinição de senha — Emefarma",
                Body = $"Acesse {BuildPublicUrl("/redefinir-senha", $"token={Uri.EscapeDataString(rawToken)}")} para criar uma nova senha. Este link expira em {reset.ExpiresAt:O}.",
                Status = "fila",
                DedupeKey = $"password-reset:{reset.Id}"
            });
            database.AuditLogs.Add(new Data.AuditLog
            {
                Action = "password_reset_requested",
                EntityType = "user",
                EntityId = user.Id,
                EntityLabel = user.FullName,
                AfterJson = "{\"delivery\":\"email\"}",
                Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = Request.Headers.UserAgent.ToString()
            });
            await database.SaveChangesAsync(cancellationToken);

            if (HttpContext.RequestServices.GetRequiredService<IHostEnvironment>().IsEnvironment("Testing"))
                return Ok(new { message = genericMessage, test_token = rawToken });
        }

        return Ok(new { message = genericMessage });
    }

    [AllowAnonymous]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Password, request.PasswordConfirmation, StringComparison.Ordinal))
            return UnprocessableEntity(new ValidationProblemDetails(new Dictionary<string, string[]> { ["passwordConfirmation"] = ["A confirmação não confere com a senha."] }));

        var now = DateTimeOffset.UtcNow;
        var reset = await database.PasswordResetTokens.Include(token => token.User)
            .SingleOrDefaultAsync(token => token.TokenHash == AccessTokenHash.Compute(request.Token)
                && token.UsedAt == null && token.ExpiresAt > now, cancellationToken);
        if (reset is null || !reset.User.Active || reset.User.DeletedAt is not null)
            return UnprocessableEntity(new ValidationProblemDetails(new Dictionary<string, string[]> { ["token"] = ["Link inválido ou expirado. Solicite uma nova redefinição de senha."] }));

        var result = await users.ResetPasswordAsync(reset.User, await users.GeneratePasswordResetTokenAsync(reset.User), request.Password);
        if (!result.Succeeded)
            return UnprocessableEntity(new ValidationProblemDetails(new Dictionary<string, string[]> { ["password"] = result.Errors.Select(error => error.Description).ToArray() }));

        reset.User.MustChangePassword = false;
        reset.User.PasswordChangedAt = now;
        reset.UsedAt = now;
        var sessions = await database.AuthSessions.Where(session => session.UserId == reset.UserId && session.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var session in sessions) session.RevokedAt = now;
        database.AuditLogs.Add(new Data.AuditLog { Action = "password_reset", EntityType = "user", EntityId = reset.UserId, EntityLabel = reset.User.FullName, Ip = HttpContext.Connection.RemoteIpAddress?.ToString(), UserAgent = Request.Headers.UserAgent.ToString() });
        await database.SaveChangesAsync(cancellationToken);
        return Ok(new { message = "Senha redefinida com sucesso." });
    }

    private string BuildPublicUrl(string path, string query) =>
        $"{Request.Scheme}://{Request.Host}{Request.PathBase}{path}?{query}";
    [Authorize]
    [HttpGet("me")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Me()
    {
        var user = await GetCurrentUserAsync();
        if (user is null || !user.Active || user.DeletedAt is not null)
            return Unauthorized();

        return Ok(new
        {
            user = ToSummary(user),
            permissions = await GetPermissionsAsync(user),
            must_change_password = user.MustChangePassword,
            is_admin = await users.IsInRoleAsync(user, "admin")
        });
    }

    [Authorize]
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(User.FindFirstValue(AccessTokenDefaults.SessionIdClaim), out var sessionId)
            && Guid.TryParse(userId, out var authenticatedUserId))
        {
            var session = await database.AuthSessions.SingleOrDefaultAsync(
                entry => entry.Id == sessionId && entry.UserId == authenticatedUserId,
                cancellationToken);
            if (session is not null && session.RevokedAt is null)
            {
                session.RevokedAt = DateTimeOffset.UtcNow;
                await database.SaveChangesAsync(cancellationToken);
            }
        }

        return Ok(new { message = "Sessão encerrada." });
    }

    [Authorize]
    [HttpPost("change-password")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUserAsync();
        if (user is null || !user.Active || user.DeletedAt is not null)
            return Unauthorized();

        if (!string.Equals(request.NewPassword, request.NewPasswordConfirmation, StringComparison.Ordinal))
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]> { ["new_password_confirmation"] = ["A confirmação não confere com a nova senha."] }));

        if (await users.CheckPasswordAsync(user, request.NewPassword))
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]> { ["new_password"] = ["A nova senha deve ser diferente da atual."] }));

        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            var errors = result.Errors.ToDictionary(
                error => error.Code == "PasswordMismatch" ? "current_password" : "new_password",
                error => new[] { error.Code == "PasswordMismatch" ? "Senha atual incorreta." : error.Description },
                StringComparer.Ordinal);
            return BadRequest(new ValidationProblemDetails(errors));
        }

        user.MustChangePassword = false;
        user.PasswordChangedAt = DateTimeOffset.UtcNow;
        await users.UpdateAsync(user);

        if (Guid.TryParse(User.FindFirstValue(AccessTokenDefaults.SessionIdClaim), out var currentSessionId))
        {
            var sessions = await database.AuthSessions
                .Where(session => session.UserId == user.Id && session.RevokedAt == null)
                .ToListAsync(cancellationToken);
            foreach (var session in sessions)
            {
                if (session.Id == currentSessionId)
                    session.SecurityStamp = user.SecurityStamp ?? string.Empty;
                else
                    session.RevokedAt = DateTimeOffset.UtcNow;
            }
            await database.SaveChangesAsync(cancellationToken);
        }

        return Ok(new
        {
            message = "Senha alterada com sucesso.",
            user = ToSummary(user),
            must_change_password = false
        });
    }

    private async Task<ApplicationUser?> GetCurrentUserAsync()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(id, out var userId) ? await users.FindByIdAsync(userId.ToString()) : null;
    }

    private async Task<IReadOnlyList<string>> GetPermissionsAsync(ApplicationUser user)
    {
        var permissions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in await users.GetClaimsAsync(user))
            if (claim.Type == "permission")
                permissions.Add(claim.Value);

        foreach (var roleName in await users.GetRolesAsync(user))
        {
            var role = await roles.FindByNameAsync(roleName);
            if (role is null)
                continue;
            foreach (var claim in await roles.GetClaimsAsync(role))
                if (claim.Type == "permission")
                    permissions.Add(claim.Value);
        }

        return permissions.Order(StringComparer.Ordinal).ToArray();
    }

    private static UserSummary ToSummary(ApplicationUser user) => new(
        user.Id,
        user.FullName,
        user.Email ?? string.Empty,
        user.DepartmentId,
        user.IndustryId);
}

public sealed record LoginRequest(
    [Required, EmailAddress, StringLength(190)] string Email,
    [Required, StringLength(256)] string Password);

public sealed record ForgotPasswordRequest(
    [Required, EmailAddress, StringLength(190)] string Email);

public sealed record ResetPasswordRequest(
    [Required, StringLength(256)] string Token,
    [Required, StringLength(256, MinimumLength = 8)] string Password,
    [Required, StringLength(256, MinimumLength = 8)] string PasswordConfirmation);

public sealed record ChangePasswordRequest(
    [Required, StringLength(256)] string CurrentPassword,
    [Required, StringLength(256, MinimumLength = 8)] string NewPassword,
    [Required, StringLength(256, MinimumLength = 8)] string NewPasswordConfirmation);

public sealed record UserSummary(Guid Id, string Name, string Email, Guid? DepartmentId, Guid? IndustryId);
public sealed record AuthResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt,
    UserSummary User, IReadOnlyList<string> Permissions, bool MustChangePassword, bool IsAdmin);
