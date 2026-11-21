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

public sealed class ScopedRequestDeliveryTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;
    public ScopedRequestDeliveryTests(TestWebApplicationFactory factory) => this.factory = factory;

    [Fact]
    public async Task Department_scoped_processor_cannot_deliver_request_from_another_department()
    {
        var client = factory.CreateClient();
        var industryId = Guid.NewGuid();
        var userDepartmentId = Guid.NewGuid();
        var requestDepartmentId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"request-deliverer-{Guid.NewGuid():N}";
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
                Assert.True((await roles.AddClaimAsync(role!, new Claim("permission", "requests.process"))).Succeeded);
                var user = new ApplicationUser
                {
                    UserName = email, Email = email, EmailConfirmed = true,
                    FullName = "Scoped request deliverer", IndustryId = industryId, DepartmentId = userDepartmentId
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Industries.Add(new Industry { Id = industryId, Name = "Delivery industry", NameKey = $"delivery-{industryId:N}" });
                database.Departments.AddRange(
                    new Department { Id = userDepartmentId, Name = "Deliverer department", NameKey = $"deliverer-{userDepartmentId:N}" },
                    new Department { Id = requestDepartmentId, Name = "Request department", NameKey = $"request-{requestDepartmentId:N}" });
                database.Requests.Add(new InternalRequest
                {
                    Id = requestId, Code = $"RD-{requestId:N}"[..18], PublicCode = $"PUB-{requestId:N}"[..18],
                    RequesterId = userId, IndustryId = industryId, DepartmentId = requestDepartmentId,
                    Flow = "interna", Status = "pronta", Purpose = "Entrega isolada",
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
                });
                await database.SaveChangesAsync();
            }

            using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
            login.EnsureSuccessStatusCode();
            using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            var token = loginJson.RootElement.GetProperty("accessToken").GetString()!;
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/requests/{requestId}/deliver")
            {
                Content = JsonContent.Create(new
                {
                    received_by_name = "Recebedor não autorizado",
                    signature = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
                })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Idempotency-Key", $"deliver-scope-{Guid.NewGuid():N}"[..24]);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            await using var verify = factory.Services.CreateAsyncScope();
            var verifyDatabase = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var saved = await verifyDatabase.Requests.SingleAsync(value => value.Id == requestId);
            Assert.Equal("pronta", saved.Status);
            Assert.Empty(await verifyDatabase.Deliveries.Where(value => value.RequestId == requestId).ToListAsync());
            Assert.Empty(await verifyDatabase.AuditLogs.Where(value => value.EntityId == requestId && value.Action == "deliver").ToListAsync());
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Deliveries.RemoveRange(database.Deliveries.Where(value => value.RequestId == requestId));
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
