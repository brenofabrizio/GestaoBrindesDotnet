using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Gbrindes.Web.Data;
using Gbrindes.Web.Identity;
using Gbrindes.Web.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Gbrindes.Tests;

public sealed class DeliveryScopeEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;

    public DeliveryScopeEndpointsTests(TestWebApplicationFactory factory) => this.factory = factory;

    [Fact]
    public async Task Industry_scoped_user_cannot_read_other_industry_delivery_artifacts()
    {
        var client = factory.CreateClient();
        var userIndustryId = Guid.NewGuid();
        var otherIndustryId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"delivery-viewer-{Guid.NewGuid():N}";
        Guid userId = Guid.Empty;
        string? signaturePath = null;

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
                Assert.True((await roles.AddClaimAsync(role!, new Claim("permission", "deliveries.view"))).Succeeded);
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = "Scoped delivery viewer",
                    IndustryId = userIndustryId
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;

                var signatures = services.GetRequiredService<DigitalSignatureStorage>();
                var signature = signatures.Store(deliveryId,
                    "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
                signaturePath = signature.Path;

                database.Industries.AddRange(
                    new Industry { Id = userIndustryId, Name = $"User industry {userIndustryId:N}", NameKey = userIndustryId.ToString("N") },
                    new Industry { Id = otherIndustryId, Name = $"Other industry {otherIndustryId:N}", NameKey = otherIndustryId.ToString("N") });
                database.Deliveries.Add(new Delivery
                {
                    Id = deliveryId,
                    Code = $"SCOPE-{deliveryId:N}"[..20],
                    IndustryId = otherIndustryId,
                    SignaturePath = signaturePath,
                    IdempotencyKeyHash = Guid.NewGuid().ToString("N"),
                    RequestHash = Guid.NewGuid().ToString("N")
                });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            foreach (var path in new[]
            {
                $"/api/v1/deliveries/{deliveryId}",
                $"/api/v1/deliveries/{deliveryId}/pdf",
                $"/api/v1/deliveries/{deliveryId}/qr",
                $"/api/deliveries/{deliveryId}/signature"
            })
            {
                using var response = await SendAsync(client, path, token);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            scope.ServiceProvider.GetRequiredService<DigitalSignatureStorage>().Delete(signaturePath);
            var user = await database.Users.FindAsync(userId);
            if (user is not null) database.Users.Remove(user);
            var delivery = await database.Deliveries.FindAsync(deliveryId);
            if (delivery is not null) database.Deliveries.Remove(delivery);
            var industries = database.Industries.Where(value => value.Id == userIndustryId || value.Id == otherIndustryId);
            database.Industries.RemoveRange(industries);
            var role = await scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>().FindByNameAsync(roleName);
            if (role is not null) database.Roles.Remove(role);
            await database.SaveChangesAsync();
        }
    }

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
