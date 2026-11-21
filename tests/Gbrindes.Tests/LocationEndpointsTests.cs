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

public sealed class LocationEndpointsTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Locations_require_lookup_permissions()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/locations")).StatusCode);
        var token = await CreateTokenAsync(["items.view"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/v1/locations", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/api/v1/locations", token, new { name = "Local", kind = "cd" })).StatusCode);
    }

    [Fact]
    public async Task Location_lifecycle_filters_and_duplicate_validation_work()
    {
        var token = await CreateTokenAsync(["lookups.view", "lookups.manage", "lookups.delete"]);
        var name = $"Local-{Guid.NewGuid():N}";
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SendAsync(HttpMethod.Post, "/api/v1/locations", token, new { kind = "cd" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SendAsync(HttpMethod.Post, "/api/v1/locations", token, new { name, kind = "invalid" })).StatusCode);
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/locations", token, new { name, description = "Depósito", kind = "cd" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var data = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = data.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("cd", data.RootElement.GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SendAsync(HttpMethod.Post, "/api/v1/locations", token, new { name = name.ToUpperInvariant(), kind = "evento" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/api/v1/locations/{id}", token, new { description = "Atualizado", kind = "evento" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/api/v1/locations/{id}/deactivate", token)).StatusCode);
        using var active = await SendAsync(HttpMethod.Get, $"/api/v1/locations?q={Uri.EscapeDataString(name)}&active=0", token);
        Assert.Single(JsonDocument.Parse(await active.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/api/v1/locations/{id}/activate", token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/locations/{id}", token)).StatusCode);
        using var trashed = await SendAsync(HttpMethod.Get, $"/api/v1/locations?trashed=1&q={Uri.EscapeDataString(name)}", token);
        Assert.Single(JsonDocument.Parse(await trashed.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/api/v1/locations/{id}/restore", token)).StatusCode);
        using var restored = await SendAsync(HttpMethod.Get, $"/api/v1/locations/{id}", token);
        Assert.Equal("evento", JsonDocument.Parse(await restored.Content.ReadAsStringAsync()).RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Location_creation_persists_actor_and_after_snapshot_in_audit()
    {
        var token = await CreateTokenAsync(["lookups.manage"]);
        var name = $"Audited location {Guid.NewGuid():N}";
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/locations", token, new { name, description = "Audit test", kind = "outro" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var audit = await database.AuditLogs.SingleAsync(log => log.Action == "create" && log.EntityType == "location" && log.EntityId == id);
        Assert.NotNull(audit.UserId);
        Assert.Null(audit.BeforeJson);
        using var after = JsonDocument.Parse(audit.AfterJson!);
        Assert.Equal(name, after.RootElement.GetProperty("name").GetString());
        Assert.Equal("outro", after.RootElement.GetProperty("kind").GetString());
        Assert.Equal("Audit test", after.RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Location_update_audits_only_changed_fields_with_actor()
    {
        var token = await CreateTokenAsync(["lookups.view", "lookups.manage"]);
        var name = $"Updated location {Guid.NewGuid():N}";
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/locations", token, new { name, description = "Before", kind = "cd" });
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var updated = await SendAsync(HttpMethod.Put, $"/api/v1/locations/{id}", token, new { description = "After", kind = "evento" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var audit = await database.AuditLogs.SingleAsync(log => log.Action == "update" && log.EntityType == "location" && log.EntityId == id);
        Assert.NotNull(audit.UserId);
        var createAudit = await database.AuditLogs.SingleAsync(log => log.Action == "create" && log.EntityType == "location" && log.EntityId == id);
        var persistedLocation = await database.Locations.SingleAsync(value => value.Id == id);
        Assert.Equal(createAudit.UserId, typeof(Location).GetProperty("CreatedBy")?.GetValue(persistedLocation));
        Assert.Equal(audit.UserId, typeof(Location).GetProperty("UpdatedBy")?.GetValue(persistedLocation));
        using var before = JsonDocument.Parse(audit.BeforeJson!);
        using var after = JsonDocument.Parse(audit.AfterJson!);
        Assert.Equal("Before", before.RootElement.GetProperty("description").GetString());
        Assert.Equal("cd", before.RootElement.GetProperty("kind").GetString());
        Assert.Equal("After", after.RootElement.GetProperty("description").GetString());
        Assert.Equal("evento", after.RootElement.GetProperty("kind").GetString());
        Assert.False(after.RootElement.TryGetProperty("name", out _));
    }

    [Fact]
    public async Task Location_deactivation_audits_transition_once()
    {
        var token = await CreateTokenAsync(["lookups.view", "lookups.manage"]);
        var name = $"Deactivated location {Guid.NewGuid():N}";
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/locations", token, new { name, kind = "cd" });
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var first = await SendAsync(HttpMethod.Post, $"/api/v1/locations/{id}/deactivate", token);
        using var repeated = await SendAsync(HttpMethod.Post, $"/api/v1/locations/{id}/deactivate", token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var audits = await database.AuditLogs.Where(log => log.Action == "deactivate" && log.EntityType == "location" && log.EntityId == id).ToListAsync();
        var audit = Assert.Single(audits);
        Assert.NotNull(audit.UserId);
        using var before = JsonDocument.Parse(audit.BeforeJson!);
        using var after = JsonDocument.Parse(audit.AfterJson!);
        Assert.True(before.RootElement.GetProperty("active").GetBoolean());
        Assert.False(after.RootElement.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Location_activation_audits_transition_once()
    {
        var token = await CreateTokenAsync(["lookups.view", "lookups.manage"]);
        var name = $"Reactivated location {Guid.NewGuid():N}";
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/locations", token, new { name, kind = "cd" });
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var deactivated = await SendAsync(HttpMethod.Post, $"/api/v1/locations/{id}/deactivate", token);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);

        using var first = await SendAsync(HttpMethod.Post, $"/api/v1/locations/{id}/activate", token);
        using var repeated = await SendAsync(HttpMethod.Post, $"/api/v1/locations/{id}/activate", token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var audits = await database.AuditLogs.Where(log => log.Action == "activate" && log.EntityType == "location" && log.EntityId == id).ToListAsync();
        var audit = Assert.Single(audits);
        Assert.NotNull(audit.UserId);
        using var before = JsonDocument.Parse(audit.BeforeJson!);
        using var after = JsonDocument.Parse(audit.AfterJson!);
        Assert.False(before.RootElement.GetProperty("active").GetBoolean());
        Assert.True(after.RootElement.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Location_trash_and_restore_persist_legacy_audit_events()
    {
        var token = await CreateTokenAsync(["lookups.view", "lookups.manage", "lookups.delete"]);
        var name = $"Trashed location {Guid.NewGuid():N}";
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/locations", token, new { name, kind = "cd" });
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var trashed = await SendAsync(HttpMethod.Delete, $"/api/v1/locations/{id}", token);
        Assert.Equal(HttpStatusCode.OK, trashed.StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var audit = await database.AuditLogs.SingleAsync(log => log.Action == "delete" && log.EntityType == "location" && log.EntityId == id);
            Assert.NotNull(audit.UserId);
            Assert.Null(audit.AfterJson);
            using var before = JsonDocument.Parse(audit.BeforeJson!);
            Assert.Equal(name, before.RootElement.GetProperty("name").GetString());
        }

        using var restored = await SendAsync(HttpMethod.Post, $"/api/v1/locations/{id}/restore", token);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        await using var verify = factory.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var restoreAudit = await verifyDb.AuditLogs.SingleAsync(log => log.Action == "restore" && log.EntityType == "location" && log.EntityId == id);
        Assert.NotNull(restoreAudit.UserId);
        Assert.Null(restoreAudit.BeforeJson);
        Assert.Null(restoreAudit.AfterJson);
    }

    [Fact]
    public async Task Location_purge_requires_trash_and_confirmation_then_audits_removal()
    {
        var token = await CreateTokenAsync(["lookups.view", "lookups.manage", "lookups.delete", "lookups.purge"]);
        var name = $"Purge location {Guid.NewGuid():N}";
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/locations", token, new { name, kind = "outro" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var active = await SendAsync(HttpMethod.Post, $"/api/v1/locations/{id}/purge", token, new { confirm = name });
        Assert.Equal(HttpStatusCode.Conflict, active.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/locations/{id}", token)).StatusCode);
        using var wrong = await SendAsync(HttpMethod.Post, $"/api/v1/locations/{id}/purge", token, new { confirm = "wrong" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);
        using var purged = await SendAsync(HttpMethod.Post, $"/api/v1/locations/{id}/purge", token, new { confirm = name });
        Assert.Equal(HttpStatusCode.OK, purged.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await database.Locations.AnyAsync(location => location.Id == id));
        var audit = await database.AuditLogs.SingleAsync(log => log.Action == "purge" && log.EntityType == "location" && log.EntityId == id);
        Assert.Equal(name, JsonDocument.Parse(audit.BeforeJson!).RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Location_purge_is_blocked_by_catalog_reference()
    {
        var token = await CreateTokenAsync(["lookups.view", "lookups.manage", "lookups.delete", "lookups.purge"]);
        var name = $"Referenced location {Guid.NewGuid():N}";
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/locations", token, new { name, kind = "cd" });
        var locationId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Categories.Add(new Category { Id = categoryId, Name = $"Location reference {categoryId:N}", NameKey = categoryId.ToString("N") });
            database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"LOC-{itemId:N}"[..12], Name = "Brinde em local", CategoryId = categoryId, LocationId = locationId });
            await database.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/locations/{locationId}", token)).StatusCode);

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/locations/{locationId}/purge", token, new { confirm = name });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("IN_USE", await response.Content.ReadAsStringAsync());
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await verify.Locations.AnyAsync(location => location.Id == locationId));
        Assert.Empty(await verify.AuditLogs.Where(log => log.Action == "purge" && log.EntityType == "location" && log.EntityId == locationId).ToListAsync());
        verify.CatalogItems.RemoveRange(verify.CatalogItems.Where(item => item.Id == itemId));
        verify.Categories.RemoveRange(verify.Categories.Where(category => category.Id == categoryId));
        await verify.SaveChangesAsync();
    }

    [Theory]
    [InlineData("position")]
    [InlineData("movement")]
    public async Task Location_purge_is_blocked_by_each_stock_history_reference(string referenceType)
    {
        var token = await CreateTokenAsync(["lookups.view", "lookups.manage", "lookups.delete", "lookups.purge"]);
        var name = $"{referenceType} location {Guid.NewGuid():N}";
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/locations", token, new { name, kind = "cd" });
        var locationId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Categories.Add(new Category { Id = categoryId, Name = $"Stock reference {categoryId:N}", NameKey = categoryId.ToString("N") });
            database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"STK-{itemId:N}"[..12], Name = "Brinde histórico", CategoryId = categoryId });
            if (referenceType == "position")
                database.StockPositions.Add(new StockPosition { ItemId = itemId, LocationId = locationId, Quantity = 1 });
            else
                database.StockMovements.Add(new StockMovement { ItemId = itemId, LocationId = locationId, Type = "entrada", Quantity = 1, BalanceAfter = 1 });
            await database.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/locations/{locationId}", token)).StatusCode);

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/locations/{locationId}/purge", token, new { confirm = name });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("IN_USE", await response.Content.ReadAsStringAsync());

        await using var cleanupScope = factory.Services.CreateAsyncScope();
        var cleanup = cleanupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        cleanup.StockPositions.RemoveRange(cleanup.StockPositions.Where(position => position.ItemId == itemId));
        cleanup.StockMovements.RemoveRange(cleanup.StockMovements.Where(movement => movement.ItemId == itemId));
        cleanup.CatalogItems.RemoveRange(cleanup.CatalogItems.Where(item => item.Id == itemId));
        cleanup.Categories.RemoveRange(cleanup.Categories.Where(category => category.Id == categoryId));
        await cleanup.SaveChangesAsync();
    }

    private async Task<string> CreateTokenAsync(string[] permissions)
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = new ApplicationRole($"location-{Guid.NewGuid():N}");
        Assert.True((await roles.CreateAsync(role)).Succeeded);
        foreach (var permission in permissions) Assert.True((await roles.AddClaimAsync(role, new("permission", permission))).Succeeded);
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Location test" };
        Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role.Name!)).Succeeded);
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return _client.SendAsync(request);
    }
}
