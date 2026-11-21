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

[Collection("PostgreSQL integration")]
public sealed class PostgreSqlImportTests(PostgreSqlWebApplicationFactory factory)
    : IClassFixture<PostgreSqlWebApplicationFactory>
{
    [PostgreSqlFact]
    public async Task Imported_request_submission_persists_reservation_approval_history_and_audit_in_postgres()
    {
        using var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-importer-{Guid.NewGuid():N}";
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var categoryName = $"PostgreSQL import {Guid.NewGuid():N}";
        var itemName = $"PostgreSQL imported item {Guid.NewGuid():N}";
        var purpose = $"PostgreSQL imported request {Guid.NewGuid():N}";
        Guid? userId = null;
        Guid? requestId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                Assert.False(string.IsNullOrWhiteSpace(database.Database.GetConnectionString()));
                database.Categories.Add(new Category { Id = categoryId, Name = categoryName, NameKey = categoryName.ToUpperInvariant() });
                database.CatalogItems.Add(new CatalogItem
                {
                    Id = itemId,
                    Code = $"PGI-{Guid.NewGuid():N}"[..20],
                    Name = itemName,
                    CategoryId = categoryId,
                    UnitValue = 2m
                });
                database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 5, Reserved = 0, UpdatedAt = DateTimeOffset.UtcNow });
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "import.run"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "PostgreSQL importer" };
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                userId = user.Id;
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                await database.SaveChangesAsync();
            }

            using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            var token = loginJson.RootElement.GetProperty("accessToken").GetString()!;
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(ImportEndpointsTests.CreateWorkbook(
                ["brinde", "quantidade", "finalidade"], [itemName, "3", purpose])), "file", "requests.xlsx");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/import/requests/apply") { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, body);
            using var result = JsonDocument.Parse(body);
            Assert.Equal(1, result.RootElement.GetProperty("created").GetInt32());
            Assert.Equal(0, result.RootElement.GetProperty("skipped").GetInt32());

            await using (var verifyScope = factory.Services.CreateAsyncScope())
            {
                var database = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var requestRow = await database.Requests.Include(entity => entity.Items).SingleAsync(entity => entity.Purpose == purpose);
                requestId = requestRow.Id;
                Assert.Equal("aprovada", requestRow.Status);
                Assert.Equal("interna", requestRow.Flow);
                Assert.Equal(userId, requestRow.RequesterId);
                Assert.Equal(6m, requestRow.TotalValue);
                Assert.Equal(3, requestRow.Items.Single().QuantityReserved);
                Assert.Equal(3, await database.Stocks.Where(stock => stock.ItemId == itemId).Select(stock => stock.Reserved).SingleAsync());
                Assert.Contains(await database.RequestStatusHistory.Where(history => history.RequestId == requestRow.Id)
                    .Select(history => history.ToStatus).ToArrayAsync(), status => status == "aprovada");
                Assert.Equal(2, await database.AuditLogs.CountAsync(log => log.EntityType == "request" && log.EntityId == requestRow.Id));
            }
        }
        finally
        {
            await using var cleanupScope = factory.Services.CreateAsyncScope();
            var services = cleanupScope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            if (requestId is Guid createdRequestId)
            {
                database.Notifications.RemoveRange(await database.Notifications.Where(notification => notification.RelatedId == createdRequestId).ToListAsync());
                database.AuditLogs.RemoveRange(await database.AuditLogs.Where(log => log.EntityType == "request" && log.EntityId == createdRequestId).ToListAsync());
                if (await database.Requests.SingleOrDefaultAsync(entity => entity.Id == createdRequestId) is { } requestRow)
                    database.Requests.Remove(requestRow);
                await database.SaveChangesAsync();
            }
            database.Stocks.RemoveRange(await database.Stocks.Where(stock => stock.ItemId == itemId).ToListAsync());
            if (await database.CatalogItems.SingleOrDefaultAsync(item => item.Id == itemId) is { } catalogItem)
                database.CatalogItems.Remove(catalogItem);
            if (await database.Categories.SingleOrDefaultAsync(category => category.Id == categoryId) is { } category)
                database.Categories.Remove(category);
            await database.SaveChangesAsync();
            if (userId is Guid createdUserId)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                if (await users.FindByIdAsync(createdUserId.ToString()) is { } user)
                    Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            if (await roles.FindByNameAsync(roleName) is { } role)
                Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }
}
