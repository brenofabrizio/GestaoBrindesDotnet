using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Gbrindes.Web.Data;
using Gbrindes.Web.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;

namespace Gbrindes.Web.Authentication;

public static class AccessTokenDefaults
{
    public const string Scheme = "GbrindesBearer";
    public const string SessionIdClaim = "gbrindes_session_id";
}

public static class AccessTokenHash
{
    public static string Compute(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public sealed class AccessTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorization))
            return AuthenticateResult.NoResult();

        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.Fail("Invalid authorization scheme.");

        var token = authorization["Bearer ".Length..].Trim();
        if (token.Length is < 32 or > 200)
            return AuthenticateResult.Fail("Invalid access token.");

        await using var scope = Context.RequestServices.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var session = await database.AuthSessions
            .Include(authSession => authSession.User)
            .SingleOrDefaultAsync(authSession => authSession.TokenHash == AccessTokenHash.Compute(token), Context.RequestAborted);

        if (session is null
            || session.RevokedAt is not null
            || session.ExpiresAt <= DateTimeOffset.UtcNow
            || !session.User.Active
            || session.User.DeletedAt is not null
            || !string.Equals(session.SecurityStamp, session.User.SecurityStamp, StringComparison.Ordinal))
        {
            return AuthenticateResult.Fail("Invalid or expired access token.");
        }

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, session.UserId.ToString()),
            new(ClaimTypes.Name, session.User.FullName),
            new(ClaimTypes.Email, session.User.Email ?? string.Empty),
            new("must_change_password", session.User.MustChangePassword ? "true" : "false"),
            new(AccessTokenDefaults.SessionIdClaim, session.Id.ToString())
        };

        if (session.User.IndustryId is Guid industryId)
            claims.Add(new Claim("industry_id", industryId.ToString()));
        if (session.User.DepartmentId is Guid departmentId)
            claims.Add(new Claim("department_id", departmentId.ToString()));

        foreach (var roleName in await userManager.GetRolesAsync(session.User))
        {
            claims.Add(new Claim(ClaimTypes.Role, roleName));
            var role = await roleManager.FindByNameAsync(roleName);
            if (role is null)
                continue;

            foreach (var roleClaim in await roleManager.GetClaimsAsync(role))
                if (roleClaim.Type == "permission")
                    claims.Add(roleClaim);
        }

        foreach (var userClaim in await userManager.GetClaimsAsync(session.User))
            if (userClaim.Type == "permission")
                claims.Add(userClaim);

        var identity = new ClaimsIdentity(claims, AccessTokenDefaults.Scheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, AccessTokenDefaults.Scheme);
        return AuthenticateResult.Success(ticket);
    }
}
