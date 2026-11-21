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

public sealed class ItemTrashTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    [Fact]
    public async Task Trash_requires_items_delete_and_restores_lifecycle()
    {
        var (token, id) = await CreateItem(["items.view", "items.manage", "lookups.manage", "lookups.view"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Delete, $"/api/v1/items/{id}", token)).StatusCode);
        var (authorized, _) = await CreateItem(["items.view", "items.manage", "items.delete", "lookups.manage", "lookups.view"]);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"/api/v1/items/{id}", authorized)).StatusCode);
        using var normal = JsonDocument.Parse(await (await Send(HttpMethod.Get, "/api/v1/items", authorized)).Content.ReadAsStringAsync());
        Assert.DoesNotContain(normal.RootElement.GetProperty("data").EnumerateArray(), x => x.GetProperty("id").GetGuid() == id);
        using var trash = JsonDocument.Parse(await (await Send(HttpMethod.Get, "/api/v1/items?trashed=1", authorized)).Content.ReadAsStringAsync());
        Assert.Contains(trash.RootElement.GetProperty("data").EnumerateArray(), x => x.GetProperty("id").GetGuid() == id);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/v1/items/{id}/restore", authorized)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"/api/v1/items/{id}/restore", authorized)).StatusCode);
    }

    [Fact]
    public async Task Trash_records_actor_and_item_snapshot()
    {
        var (token, id) = await CreateItem(["items.view", "items.manage", "items.delete", "lookups.manage", "lookups.view"]);

        using var trashed = await Send(HttpMethod.Delete, $"/api/v1/items/{id}", token);
        Assert.Equal(HttpStatusCode.OK, trashed.StatusCode);
        using var trashList = JsonDocument.Parse(await (await Send(HttpMethod.Get, "/api/v1/items?trashed=1", token)).Content.ReadAsStringAsync());
        var trashedItem = Assert.Single(trashList.RootElement.GetProperty("data").EnumerateArray(), item => item.GetProperty("id").GetGuid() == id);
        Assert.False(trashedItem.TryGetProperty("deletedBy", out _));

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var audit = await database.AuditLogs.SingleAsync(log => log.EntityType == "item" && log.EntityId == id && log.Action == "delete");
        Assert.NotNull(audit.UserId);
        Assert.Equal(audit.UserId, (await database.CatalogItems.SingleAsync(item => item.Id == id)).DeletedBy);
        Assert.Equal("Trash test", audit.UserName);
        Assert.Contains(" - Test item", audit.EntityLabel);
        Assert.Null(audit.AfterJson);
        using var before = JsonDocument.Parse(audit.BeforeJson!);
        Assert.Equal("Test item", before.RootElement.GetProperty("name").GetString());
        Assert.Equal("ativo", before.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Restore_records_actor_without_adding_snapshot_fields()
    {
        var (token, id) = await CreateItem(["items.view", "items.manage", "items.delete", "lookups.manage", "lookups.view"]);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"/api/v1/items/{id}", token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/v1/items/{id}/restore", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"/api/v1/items/{id}/restore", token)).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var audit = await database.AuditLogs.SingleAsync(log => log.EntityType == "item" && log.EntityId == id && log.Action == "restore");
        Assert.NotNull(audit.UserId);
        Assert.Equal("Trash test", audit.UserName);
        Assert.Null(audit.BeforeJson);
        Assert.Null(audit.AfterJson);
        Assert.Null((await database.CatalogItems.SingleAsync(item => item.Id == id)).DeletedBy);
    }

    [Fact]
    public async Task Listing_uses_case_insensitive_search_and_treats_missing_stock_as_attention()
    {
        var (token, id) = await CreateItem(["items.view", "items.manage", "lookups.manage", "lookups.view"]);

        foreach (var level in new[] { "zero", "attention" })
        {
            using var response = await Send(HttpMethod.Get, $"/api/v1/items?q=TEST%20ITEM&stock_level={level}", token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains(json.RootElement.GetProperty("data").EnumerateArray(), item => item.GetProperty("id").GetGuid() == id);
        }
    }

    [Theory]
    [InlineData(4, 1, "ITEM_HAS_RESERVATIONS")]
    [InlineData(4, 0, "ITEM_HAS_STOCK")]
    public async Task Trash_rejects_items_with_reserved_or_on_hand_stock(int onHand, int reserved, string error)
    {
        var (token, id) = await CreateItem(["items.view", "items.manage", "items.delete", "lookups.manage", "lookups.view"]);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var item = await database.CatalogItems.SingleAsync(x => x.Id == id);
            database.Stocks.Add(new InventoryStock { ItemId = id, Item = item, OnHand = onHand, Reserved = reserved });
            await database.SaveChangesAsync();
        }

        using var response = await Send(HttpMethod.Delete, $"/api/v1/items/{id}", token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(error, body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Listing_honors_legacy_per_page_and_sort_parameters()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var permissions = new[] { "items.view", "items.manage", "lookups.manage", "lookups.view" };
        await CreateItem(permissions, $"Sort-{marker}-A");
        var (token, _) = await CreateItem(permissions, $"Sort-{marker}-B");

        using var firstResponse = await Send(HttpMethod.Get, $"/api/v1/items?q={marker}&sort=-name&page=1&per_page=1", token);
        using var first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        Assert.Single(first.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal($"Sort-{marker}-B", first.RootElement.GetProperty("data")[0].GetProperty("name").GetString());

        using var secondResponse = await Send(HttpMethod.Get, $"/api/v1/items?q={marker}&sort=-name&page=2&per_page=1", token);
        using var second = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
        Assert.Single(second.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal($"Sort-{marker}-A", second.RootElement.GetProperty("data")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Listing_sorts_by_available_stock_when_requested()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var permissions = new[] { "items.view", "items.manage", "lookups.manage", "lookups.view" };
        var (_, higherStockId) = await CreateItem(permissions, $"Stock-{marker}-A");
        var (token, lowerStockId) = await CreateItem(permissions, $"Stock-{marker}-B");
        await SetStock(lowerStockId, 2, 0);
        await SetStock(higherStockId, 8, 0);

        using var response = await Send(HttpMethod.Get, $"/api/v1/items?q={marker}&sort=-available&per_page=1", token);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(higherStockId, json.RootElement.GetProperty("data")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Listing_classifies_zero_low_and_ok_by_available_stock()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var permissions = new[] { "items.view", "items.manage", "lookups.manage", "lookups.view" };
        var (_, lowId) = await CreateItem(permissions, $"Level-{marker}-low");
        var (_, okId) = await CreateItem(permissions, $"Level-{marker}-ok");
        var (token, zeroId) = await CreateItem(permissions, $"Level-{marker}-zero");

        foreach (var id in new[] { lowId, okId, zeroId })
        {
            using var update = await Send(HttpMethod.Put, $"/api/v1/items/{id}", token, new { minStock = 3 });
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        }
        await SetStock(lowId, 4, 2);
        await SetStock(okId, 6, 1);
        await SetStock(zeroId, 2, 2);

        await AssertListContainsOnly(token, marker, "low", lowId);
        await AssertListContainsOnly(token, marker, "ok", okId);
        await AssertListContainsOnly(token, marker, "zero", zeroId);
    }

    [Fact]
    public async Task Trashed_item_cannot_receive_inventory_movements()
    {
        var (token, id) = await CreateItem(["items.view", "items.manage", "items.delete", "lookups.manage", "lookups.view", "stock.entry"]);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"/api/v1/items/{id}", token)).StatusCode);

        using var response = await Send(HttpMethod.Post, $"/api/v1/items/{id}/entry", token, new { quantity = 1 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Trashed_item_cannot_be_updated_or_activated()
    {
        var (token, id) = await CreateItem(["items.view", "items.manage", "items.delete", "lookups.manage", "lookups.view"]);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"/api/v1/items/{id}", token)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Put, $"/api/v1/items/{id}", token, new { name = "Changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, $"/api/v1/items/{id}/activate", token)).StatusCode);
    }

    private async Task<(string, Guid)> CreateItem(string[] permissions, string itemName = "Test item")
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = new ApplicationRole($"r-{Guid.NewGuid():N}");
        Assert.True((await roles.CreateAsync(role)).Succeeded);
        foreach (var p in permissions) Assert.True((await roles.AddClaimAsync(role, new("permission", p))).Succeeded);
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Trash test" };
        Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role.Name!)).Succeeded);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        using var lj = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = lj.RootElement.GetProperty("accessToken").GetString()!;
        var cat = await Send(HttpMethod.Post, "/api/v1/categories", token, new { name = $"C-{Guid.NewGuid():N}" });
        using var cj = JsonDocument.Parse(await cat.Content.ReadAsStringAsync());
        var created = await Send(HttpMethod.Post, "/api/v1/items", token, new { name = itemName, categoryId = cj.RootElement.GetProperty("id").GetGuid() });
        using var ij = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return (token, ij.RootElement.GetProperty("id").GetGuid());
    }

    private async Task SetStock(Guid itemId, int onHand, int reserved)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var item = await database.CatalogItems.SingleAsync(x => x.Id == itemId);
        database.Stocks.Add(new InventoryStock { ItemId = itemId, Item = item, OnHand = onHand, Reserved = reserved });
        await database.SaveChangesAsync();
    }

    private async Task AssertListContainsOnly(string token, string marker, string level, Guid expectedId)
    {
        using var response = await Send(HttpMethod.Get, $"/api/v1/items?q={marker}&stock_level={level}", token);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedId, Assert.Single(json.RootElement.GetProperty("data").EnumerateArray()).GetProperty("id").GetGuid());
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string token, object? body = null)
    {
        var r = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(r);
    }
}
