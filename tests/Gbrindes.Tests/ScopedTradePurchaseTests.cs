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

public sealed class ScopedTradePurchaseTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;
    public ScopedTradePurchaseTests(TestWebApplicationFactory factory) => this.factory = factory;

    [Fact]
    public async Task Department_scoped_user_cannot_register_purchase_for_another_department_trade_request()
    {
        var client = factory.CreateClient();
        var industryId = Guid.NewGuid();
        var userDepartmentId = Guid.NewGuid();
        var requestDepartmentId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"trade-purchaser-{Guid.NewGuid():N}";
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
                Assert.True((await roles.AddClaimAsync(role!, new Claim("permission", "requests.approve"))).Succeeded);
                var user = new ApplicationUser
                {
                    UserName = email, Email = email, EmailConfirmed = true,
                    FullName = "Scoped TRADE purchaser", IndustryId = industryId, DepartmentId = userDepartmentId
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Industries.Add(new Industry { Id = industryId, Name = "Purchase industry", NameKey = $"purchase-{industryId:N}" });
                database.Departments.AddRange(
                    new Department { Id = userDepartmentId, Name = "Purchaser department", NameKey = $"purchaser-{userDepartmentId:N}" },
                    new Department { Id = requestDepartmentId, Name = "Request department", NameKey = $"request-{requestDepartmentId:N}" });
                database.Requests.Add(new InternalRequest
                {
                    Id = requestId, Code = $"TP-{requestId:N}"[..18], PublicCode = $"PUB-{requestId:N}"[..18],
                    RequesterId = userId, IndustryId = industryId, DepartmentId = requestDepartmentId,
                    Flow = "trade", Status = "aprovada", Purpose = "Compra isolada",
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
                });
                await database.SaveChangesAsync();
            }

            using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
            login.EnsureSuccessStatusCode();
            using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            var token = loginJson.RootElement.GetProperty("accessToken").GetString()!;
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/purchase")
            {
                Content = JsonContent.Create(new { purchase_ticket_no = "COMPRA-NAO-AUTORIZADA" })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            await using var verify = factory.Services.CreateAsyncScope();
            var verifyDatabase = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var saved = await verifyDatabase.Requests.SingleAsync(value => value.Id == requestId);
            Assert.Equal("aprovada", saved.Status);
            Assert.Null(saved.PurchaseTicketNo);
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.RequestStatusHistory.RemoveRange(database.RequestStatusHistory.Where(value => value.RequestId == requestId));
            database.AuditLogs.RemoveRange(database.AuditLogs.Where(value => value.EntityId == requestId));
            database.Requests.RemoveRange(database.Requests.Where(value => value.Id == requestId));
            database.Departments.RemoveRange(database.Departments.Where(value => value.Id == userDepartmentId || value.Id == requestDepartmentId));
            database.Industries.RemoveRange(database.Industries.Where(value => value.Id == industryId));
            var user = await database.Users.FindAsync(userId);
            if (user is not null) database.Users.Remove(user);
            var role = await scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>().FindByNameAsync(roleName);
            if (role is not null) database.Roles.Remove(role);
            await database.SaveChangesAsync();
        }
    }
}
