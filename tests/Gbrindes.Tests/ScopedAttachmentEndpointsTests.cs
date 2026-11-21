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

public sealed class ScopedAttachmentEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory factory;
    public ScopedAttachmentEndpointsTests(TestWebApplicationFactory factory) => this.factory = factory;

    [Fact]
    public async Task Department_scoped_user_cannot_download_same_industry_attachment_from_another_department()
    {
        var client = factory.CreateClient();
        var industryId = Guid.NewGuid();
        var userDepartmentId = Guid.NewGuid();
        var otherDepartmentId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var movementId = Guid.NewGuid();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"attachment-viewer-{Guid.NewGuid():N}";
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
                Assert.True((await roles.AddClaimAsync(role!, new Claim("permission", "stock.view"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Department scoped viewer", IndustryId = industryId, DepartmentId = userDepartmentId };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;

                database.Industries.Add(new Industry { Id = industryId, Name = "Attachment industry", NameKey = $"industry-{industryId:N}" });
                database.Departments.AddRange(
                    new Department { Id = userDepartmentId, Name = "User department", NameKey = $"dept-{userDepartmentId:N}" },
                    new Department { Id = otherDepartmentId, Name = "Other department", NameKey = $"dept-{otherDepartmentId:N}" });
                database.Categories.Add(new Category { Id = categoryId, Name = "Attachment category", NameKey = $"cat-{categoryId:N}" });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"AT-{itemId:N}"[..12], Name = "Attachment item", CategoryId = categoryId });
                var storage = services.GetRequiredService<StockAttachmentStorage>();
                var stored = storage.Store(itemId, new PreparedStockAttachment("%PDF-1.4 test"u8.ToArray(), "pdf", "application/pdf", "test-hash"));
                attachmentPath = stored.Path;
                database.StockMovements.Add(new StockMovement { Id = movementId, ItemId = itemId, IndustryId = industryId, DepartmentId = otherDepartmentId, Type = "entrada", Quantity = 1, BalanceAfter = 1, AttachmentPath = attachmentPath });
                await database.SaveChangesAsync();
            }

            using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
            login.EnsureSuccessStatusCode();
            using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            var token = loginJson.RootElement.GetProperty("accessToken").GetString()!;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/stock/movements/{movementId}/attachment");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            scope.ServiceProvider.GetRequiredService<StockAttachmentStorage>().Delete(attachmentPath);
            database.StockMovements.RemoveRange(database.StockMovements.Where(value => value.Id == movementId));
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
