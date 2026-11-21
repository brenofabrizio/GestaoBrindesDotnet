using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Gbrindes.Web.Data;
using Gbrindes.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gbrindes.Tests;

public sealed class ScopedReportEndpointsTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task Industry_user_reports_never_include_movements_from_another_industry()
    {
        var industryA = new Industry { Name = $"Report A {Guid.NewGuid():N}", NameKey = Guid.NewGuid().ToString("N") };
        var industryB = new Industry { Name = $"Report B {Guid.NewGuid():N}", NameKey = Guid.NewGuid().ToString("N") };
        var departmentA = new Department { Name = $"Department A {Guid.NewGuid():N}", NameKey = Guid.NewGuid().ToString("N") };
        var category = new Category { Name = $"Report category {Guid.NewGuid():N}", NameKey = Guid.NewGuid().ToString("N") };
        var itemA = new CatalogItem { Code = $"RPA-{Guid.NewGuid():N}"[..12].ToUpperInvariant(), Name = "Item A", CategoryId = category.Id, IndustryId = industryA.Id, UnitValue = 10m };
        var itemB = new CatalogItem { Code = $"RPB-{Guid.NewGuid():N}"[..12].ToUpperInvariant(), Name = "Item B", CategoryId = category.Id, IndustryId = industryB.Id, UnitValue = 20m };
        var email = $"industry-{Guid.NewGuid():N}@example.invalid";
        Guid userId;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Industries.AddRange(industryA, industryB);
            database.Departments.Add(departmentA);
            database.Categories.Add(category);
            database.CatalogItems.AddRange(itemA, itemB);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = new ApplicationRole($"report-scope-{Guid.NewGuid():N}");
            Assert.True((await roles.CreateAsync(role)).Succeeded);
            Assert.True((await roles.AddClaimAsync(role, new System.Security.Claims.Claim("permission", "reports.view"))).Succeeded);
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, IndustryId = industryA.Id, DepartmentId = departmentA.Id, FullName = "Scoped report user" };
            Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role.Name!)).Succeeded);
            userId = user.Id;
            database.StockMovements.AddRange(
                new StockMovement { ItemId = itemA.Id, IndustryId = industryA.Id, DepartmentId = departmentA.Id, UserId = userId, Type = "saida", Quantity = -2, BalanceAfter = 8, CreatedAt = DateTimeOffset.UtcNow },
                new StockMovement { ItemId = itemB.Id, IndustryId = industryB.Id, DepartmentId = departmentA.Id, UserId = userId, Type = "saida", Quantity = -3, BalanceAfter = 7, CreatedAt = DateTimeOffset.UtcNow });
            await database.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = loginJson.RootElement.GetProperty("accessToken").GetString()!;

        foreach (var route in new[] { "/api/v1/reports/by_industry", "/api/v1/reports/by_requester", "/api/v1/reports/by_department", "/api/v1/reports/by_item" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var body = json.RootElement.GetProperty("data").ToString();
            Assert.DoesNotContain(industryB.Name, body, StringComparison.Ordinal);
            Assert.DoesNotContain(itemB.Name, body, StringComparison.Ordinal);
        }
    }
}
