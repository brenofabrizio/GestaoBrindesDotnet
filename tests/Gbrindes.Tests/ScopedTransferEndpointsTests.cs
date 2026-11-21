using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Gbrindes.Web.Data;
using Gbrindes.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gbrindes.Tests;

public sealed class ScopedTransferEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;
    public ScopedTransferEndpointsTests(TestWebApplicationFactory factory) => this.factory = factory;

    [Fact]
    public async Task Industry_scoped_user_cannot_transfer_stock_for_another_industry()
    {
        var client = factory.CreateClient();
        var userIndustryId = Guid.NewGuid();
        var requestedIndustryId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var fromLocationId = Guid.NewGuid();
        var toLocationId = Guid.NewGuid();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"transfer-viewer-{Guid.NewGuid():N}";
        Guid userId = Guid.Empty;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                Assert.True((await roles.CreateAsync(new ApplicationRole(roleName))).Succeeded);
                var role = await roles.FindByNameAsync(roleName);
                Assert.NotNull(role);
                Assert.True((await roles.AddClaimAsync(role!, new Claim("permission", "stock.transfer"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Scoped transfer user", IndustryId = userIndustryId };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;

                database.Industries.AddRange(
                    new Industry { Id = userIndustryId, Name = $"User industry {userIndustryId:N}", NameKey = userIndustryId.ToString("N") },
                    new Industry { Id = requestedIndustryId, Name = $"Requested industry {requestedIndustryId:N}", NameKey = requestedIndustryId.ToString("N") });
                database.Categories.Add(new Category { Id = categoryId, Name = "Transfer category", NameKey = $"transfer-{categoryId:N}" });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"TR-{itemId:N}"[..12], Name = "Transfer item", CategoryId = categoryId });
                database.Locations.AddRange(
                    new Location { Id = fromLocationId, Name = "Transfer source", NameKey = $"source-{fromLocationId:N}", Kind = "cd", Active = true },
                    new Location { Id = toLocationId, Name = "Transfer destination", NameKey = $"dest-{toLocationId:N}", Kind = "outro", Active = true });
                database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 10 });
                database.StockPositions.Add(new StockPosition { ItemId = itemId, LocationId = fromLocationId, Quantity = 10 });
                await database.SaveChangesAsync();
            }

            using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
            login.EnsureSuccessStatusCode();
            using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            var token = loginJson.RootElement.GetProperty("accessToken").GetString()!;
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/transfers")
            {
                Content = JsonContent.Create(new { item_id = itemId, quantity = 2, industry_id = requestedIndustryId, from_location_id = fromLocationId, to_location_id = toLocationId })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Idempotency-Key", $"scope-{Guid.NewGuid():N}"[..20]);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("INDUSTRY_FORBIDDEN", body.RootElement.GetProperty("error").GetProperty("code").GetString());

            await using var verify = factory.Services.CreateAsyncScope();
            var verifyDatabase = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Empty(await verifyDatabase.StockMovements.Where(value => value.ItemId == itemId).ToListAsync());
            Assert.Equal(10, await verifyDatabase.StockPositions.Where(value => value.ItemId == itemId && value.LocationId == fromLocationId).Select(value => value.Quantity).SingleAsync());
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await database.Users.FindAsync(userId);
            if (user is not null) database.Users.Remove(user);
            database.StockMovements.RemoveRange(database.StockMovements.Where(value => value.ItemId == itemId));
            database.StockPositions.RemoveRange(database.StockPositions.Where(value => value.ItemId == itemId));
            database.Stocks.RemoveRange(database.Stocks.Where(value => value.ItemId == itemId));
            database.CatalogItems.RemoveRange(database.CatalogItems.Where(value => value.Id == itemId));
            database.Categories.RemoveRange(database.Categories.Where(value => value.Id == categoryId));
            database.Locations.RemoveRange(database.Locations.Where(value => value.Id == fromLocationId || value.Id == toLocationId));
            database.Industries.RemoveRange(database.Industries.Where(value => value.Id == userIndustryId || value.Id == requestedIndustryId));
            var role = await scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>().FindByNameAsync(roleName);
            if (role is not null) database.Roles.Remove(role);
            await database.SaveChangesAsync();
        }
    }
}
