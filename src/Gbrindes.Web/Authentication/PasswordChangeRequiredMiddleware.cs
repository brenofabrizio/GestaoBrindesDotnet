using System.Security.Claims;
using System.Text.Json;

namespace Gbrindes.Web.Authentication;

public sealed class PasswordChangeRequiredMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> AllowedWhilePasswordChangeIsRequired = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/v1/auth/me",
        "/api/v1/auth/logout",
        "/api/v1/auth/change-password"
    };

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var changeRequired = string.Equals(
            context.User.FindFirstValue("must_change_password"), "true", StringComparison.OrdinalIgnoreCase);

        if (path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase)
            && changeRequired
            && !AllowedWhilePasswordChangeIsRequired.Contains(path))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.Body,
                new { code = "PASSWORD_CHANGE_REQUIRED", message = "Altere sua senha para continuar." },
                cancellationToken: context.RequestAborted);
            return;
        }

        await next(context);
    }
}
