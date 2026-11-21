using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Gbrindes.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Gbrindes.Tests;

public sealed class PermissionMatrixTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    [Fact]
    public async Task Five_profile_matrix_allows_only_the_declared_representative_domains()
    {
        var all = new[]
        {
            "dashboard.view", "items.view", "stock.view", "requests.view_all", "requests.view_own",
            "requests.view_department", "requests.approve", "events.view", "reports.view"
        };
        var profiles = new Dictionary<string, (string[] Permissions, string[] AllowedRoutes)>
        {
            ["admin"] = (all, ["dashboard", "items", "stock", "requests", "events", "reports"]),
            ["trade-gestor"] = (["dashboard.view", "items.view", "stock.view", "requests.view_all", "requests.approve", "events.view", "reports.view"], ["dashboard", "items", "stock", "requests", "events", "reports"]),
            ["cd-estoque"] = (["dashboard.view", "items.view", "stock.view"], ["dashboard", "items", "stock"]),
            ["solicitante"] = (["dashboard.view", "items.view", "requests.view_own"], ["dashboard", "items", "requests"]),
            ["industria"] = (["dashboard.view", "items.view", "requests.view_own", "reports.view"], ["dashboard", "items", "requests", "reports"])
        };
        var routes = new Dictionary<string, string>
        {
            ["dashboard"] = "/api/v1/dashboard",
            ["items"] = "/api/v1/items",
            ["stock"] = "/api/v1/stock/positions",
            ["requests"] = "/api/v1/requests",
            ["events"] = "/api/v1/events",
            ["reports"] = "/api/v1/reports"
        };

        foreach (var (profile, matrix) in profiles)
        {
            var token = await CreateUserAndLoginAsync($"{profile}-{Guid.NewGuid():N}@example.invalid", profile, matrix.Permissions);
            foreach (var (domain, path) in routes)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request);
                var shouldAllow = matrix.AllowedRoutes.Contains(domain, StringComparer.Ordinal);
                Assert.Equal(shouldAllow, response.StatusCode != HttpStatusCode.Forbidden);
            }
        }
    }

    [Fact]
    public async Task Profile_matrix_rejects_anonymous_and_unknown_permission_access()
    {
        using var anonymous = await client.GetAsync("/api/v1/reports");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var token = await CreateUserAndLoginAsync($"limited-{Guid.NewGuid():N}@example.invalid", "limited", ["dashboard.view"]);
        using var deniedItems = new HttpRequestMessage(HttpMethod.Get, "/api/v1/items");
        deniedItems.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(deniedItems);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<string> CreateUserAndLoginAsync(string email, string profile, string[] permissions)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = new ApplicationRole($"matrix-{profile}-{Guid.NewGuid():N}");
        Assert.True((await roles.CreateAsync(role)).Succeeded);
        foreach (var permission in permissions)
            Assert.True((await roles.AddClaimAsync(role, new System.Security.Claims.Claim("permission", permission))).Succeeded);
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = $"Matrix {profile}"
        };
        Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role.Name!)).Succeeded);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }
}
