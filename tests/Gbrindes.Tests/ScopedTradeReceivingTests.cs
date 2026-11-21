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

public sealed class ScopedTradeReceivingTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;
    public ScopedTradeReceivingTests(TestWebApplicationFactory factory) => this.factory = factory;

    [Fact]
    public async Task Department_scoped_user_cannot_receive_trade_request_from_another_department()
    {
        var client = factory.CreateClient();
        var industryId = Guid.NewGuid();
        var userDepartmentId = Guid.NewGuid();
        var otherDepartmentId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"trade-receiver-{Guid.NewGuid():N}";
        Guid userId = Guid.Empty;

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
                Assert.True((await roles.AddClaimAsync(role!, new Claim("permission", "stock.receive"))).Succeeded);
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = "Scoped TRADE receiver",
                    IndustryId = industryId,
                    DepartmentId = userDepartmentId
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;

                database.Industries.Add(new Industry { Id = industryId, Name = "TRADE industry", NameKey = $"trade-{industryId:N}" });
                database.Departments.AddRange(
                    new Department { Id = userDepartmentId, Name = "Receiver department", NameKey = $"receiver-{userDepartmentId:N}" },
                    new Department { Id = otherDepartmentId, Name = "Request department", NameKey = $"request-{otherDepartmentId:N}" });
                database.Categories.Add(new Category { Id = categoryId, Name = "TRADE category", NameKey = $"trade-cat-{categoryId:N}" });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"TR-{itemId:N}"[..12], Name = "TRADE item", CategoryId = categoryId });
                var tradeRequest = new InternalRequest
                {
                    Id = requestId,
                    Code = $"TR-{requestId:N}"[..18],
                    PublicCode = $"PUB-{requestId:N}"[..18],
                    RequesterId = userId,
                    IndustryId = industryId,
                    DepartmentId = otherDepartmentId,
                    Flow = "trade",
                    Status = "aguardando_recebimento",
                    Purpose = "Recebimento isolado",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Items = [new InternalRequestItem { RequestId = requestId, ItemId = itemId, Position = 0, QuantityRequested = 1 }]
                };
                database.Requests.Add(tradeRequest);
                await database.SaveChangesAsync();
            }

            using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
            login.EnsureSuccessStatusCode();
            using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            var token = loginJson.RootElement.GetProperty("accessToken").GetString()!;
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/receive")
            {
                Content = JsonContent.Create(new { invoice_no = "NF-ESCOPO-01", items = new[] { new { item_id = itemId, qty = 1 } } })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Idempotency-Key", "trade-scope-receive-01");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            await using var verify = factory.Services.CreateAsyncScope();
            var verifyDatabase = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var saved = await verifyDatabase.Requests.SingleAsync(value => value.Id == requestId);
            Assert.Equal("aguardando_recebimento", saved.Status);
            Assert.Null(saved.InvoiceNo);
            Assert.Empty(await verifyDatabase.StockMovements.Where(value => value.ItemId == itemId).ToListAsync());
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.RequestItems.RemoveRange(database.RequestItems.Where(value => value.RequestId == requestId));
            database.Requests.RemoveRange(database.Requests.Where(value => value.Id == requestId));
            database.CatalogItems.RemoveRange(database.CatalogItems.Where(value => value.Id == itemId));
            database.Categories.RemoveRange(database.Categories.Where(value => value.Id == categoryId));
            database.Departments.RemoveRange(database.Departments.Where(value => value.Id == userDepartmentId || value.Id == otherDepartmentId));
            database.Industries.RemoveRange(database.Industries.Where(value => value.Id == industryId));
            var user = await database.Users.FindAsync(userId);
            if (user is not null) database.Users.Remove(user);
            var role = await scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>().FindByNameAsync(roleName);
            if (role is not null) database.Roles.Remove(role);
            await database.SaveChangesAsync();
        }
    }
}
