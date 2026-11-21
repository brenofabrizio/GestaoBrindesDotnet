using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Gbrindes.Web.Data;
using Gbrindes.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Gbrindes.Tests;

public sealed class ScopedInventoryEndpointsTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task Stock_movements_are_scoped_and_entry_cannot_target_another_industry()
    {
        var industryA = new Industry { Name = $"Stock A {Guid.NewGuid():N}", NameKey = Guid.NewGuid().ToString("N") };
        var industryB = new Industry { Name = $"Stock B {Guid.NewGuid():N}", NameKey = Guid.NewGuid().ToString("N") };
        var category = new Category { Name = $"Stock category {Guid.NewGuid():N}", NameKey = Guid.NewGuid().ToString("N") };
        var itemA = new CatalogItem { Code = $"STA-{Guid.NewGuid():N}"[..12].ToUpperInvariant(), Name = "Stock item A", CategoryId = category.Id };
        var itemB = new CatalogItem { Code = $"STB-{Guid.NewGuid():N}"[..12].ToUpperInvariant(), Name = "Stock item B", CategoryId = category.Id };
        var email = $"stock-{Guid.NewGuid():N}@example.invalid";

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Industries.AddRange(industryA, industryB);
            database.Categories.Add(category);
            database.CatalogItems.AddRange(itemA, itemB);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = new ApplicationRole($"stock-scope-{Guid.NewGuid():N}");
            Assert.True((await roles.CreateAsync(role)).Succeeded);
            foreach (var permission in new[] { "stock.view", "stock.entry" })
                Assert.True((await roles.AddClaimAsync(role, new System.Security.Claims.Claim("permission", permission))).Succeeded);
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, IndustryId = industryA.Id, FullName = "Scoped stock user" };
            Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role.Name!)).Succeeded);
            database.StockMovements.AddRange(
                new StockMovement { ItemId = itemA.Id, IndustryId = industryA.Id, UserId = user.Id, Type = "entrada", Quantity = 4, BalanceAfter = 4 },
                new StockMovement { ItemId = itemB.Id, IndustryId = industryB.Id, UserId = user.Id, Type = "entrada", Quantity = 9, BalanceAfter = 9 });
            await database.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = loginJson.RootElement.GetProperty("accessToken").GetString()!;

        using (var movements = new HttpRequestMessage(HttpMethod.Get, "/api/v1/stock/movements?industry_id=" + industryB.Id))
        {
            movements.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(movements);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(itemB.Name, body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"quantity\":9", body, StringComparison.Ordinal);
        }

        using (var entry = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/items/{itemA.Id}/entry"))
        {
            entry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            entry.Headers.Add("Idempotency-Key", $"scope-{Guid.NewGuid():N}");
            entry.Content = JsonContent.Create(new { quantity = 1, industry_id = industryB.Id });
            using var response = await client.SendAsync(entry);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }
}
