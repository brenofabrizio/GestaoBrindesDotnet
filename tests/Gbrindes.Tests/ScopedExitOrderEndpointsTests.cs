using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Gbrindes.Web.Data;
using Gbrindes.Web.Identity;
using Gbrindes.Web.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gbrindes.Tests;

public sealed class ScopedExitOrderEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;
    public ScopedExitOrderEndpointsTests(TestWebApplicationFactory factory) => this.factory = factory;

    [Fact]
    public async Task Industry_scoped_user_cannot_download_another_industry_exit_order_attachment()
    {
        var client = factory.CreateClient();
        var userIndustryId = Guid.NewGuid();
        var otherIndustryId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"exit-confirm-viewer-{Guid.NewGuid():N}";
        Guid userId = Guid.Empty;
        string? attachmentPath = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                Assert.True((await roles.CreateAsync(new ApplicationRole(roleName))).Succeeded);
                var role = await roles.FindByNameAsync(roleName);
                Assert.NotNull(role);
                Assert.True((await roles.AddClaimAsync(role!, new Claim("permission", "stock.exit_confirm"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(role!, new Claim("permission", "stock.exit"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Scoped exit viewer", IndustryId = userIndustryId };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;

                database.Industries.AddRange(
                    new Industry { Id = userIndustryId, Name = "User exit industry", NameKey = $"exit-user-{userIndustryId:N}" },
                    new Industry { Id = otherIndustryId, Name = "Other exit industry", NameKey = $"exit-other-{otherIndustryId:N}" });
                database.Categories.Add(new Category { Id = categoryId, Name = "Exit category", NameKey = $"exit-cat-{categoryId:N}" });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"EX-{itemId:N}"[..12], Name = "Exit item", CategoryId = categoryId });
                database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 1, Reserved = 1 });
                var storage = services.GetRequiredService<StockAttachmentStorage>();
                attachmentPath = storage.Store(itemId, new PreparedStockAttachment("%PDF-1.4 exit"u8.ToArray(), "pdf", "application/pdf", "exit-hash")).Path;
                database.StockExitOrders.Add(new StockExitOrder
                {
                    Id = orderId,
                    Code = $"SAIDA-{orderId:N}"[..20],
                    ItemId = itemId,
                    Quantity = 1,
                    Purpose = "Teste de escopo",
                    IndustryId = otherIndustryId,
                    AttachmentPath = attachmentPath,
                    AuthorizedBy = user.Id,
                    IdempotencyKey = $"seed-{orderId:N}",
                    IdempotencyRequestHash = "seed-hash"
                });
                await database.SaveChangesAsync();
            }

            using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
            login.EnsureSuccessStatusCode();
            using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            var token = loginJson.RootElement.GetProperty("accessToken").GetString()!;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/stock/exit-orders/{orderId}/attachment");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            using var cancelRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/stock/exit-orders/{orderId}/cancel")
            {
                Content = JsonContent.Create(new { reason = "escopo" })
            };
            cancelRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var cancelResponse = await client.SendAsync(cancelRequest);
            Assert.Equal(HttpStatusCode.NotFound, cancelResponse.StatusCode);
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            scope.ServiceProvider.GetRequiredService<StockAttachmentStorage>().Delete(attachmentPath);
            database.StockExitOrders.RemoveRange(database.StockExitOrders.Where(value => value.Id == orderId));
            database.Stocks.RemoveRange(database.Stocks.Where(value => value.ItemId == itemId));
            database.CatalogItems.RemoveRange(database.CatalogItems.Where(value => value.Id == itemId));
            database.Categories.RemoveRange(database.Categories.Where(value => value.Id == categoryId));
            database.Industries.RemoveRange(database.Industries.Where(value => value.Id == userIndustryId || value.Id == otherIndustryId));
            var user = await database.Users.FindAsync(userId);
            if (user is not null) database.Users.Remove(user);
            var role = await scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>().FindByNameAsync(roleName);
            if (role is not null) database.Roles.Remove(role);
            await database.SaveChangesAsync();
        }
    }
}
