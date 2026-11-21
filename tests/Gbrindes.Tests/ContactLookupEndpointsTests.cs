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

public sealed class ContactLookupEndpointsTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    [Theory]
    [InlineData("industries")]
    [InlineData("suppliers")]
    public async Task Contact_lookup_http_lifecycle_validation_and_permissions(string type)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/v1/{type}")).StatusCode);
        var viewOnly = await Token(["lookups.view"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"/api/v1/{type}", viewOnly, new { name = "Denied" })).StatusCode);
        var token = await Token(["lookups.view", "lookups.manage", "lookups.delete"]);
        var path = $"/api/v1/{type}";
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(HttpMethod.Post, path, token, new { name = "Invalid", cnpj = "abc" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(HttpMethod.Post, path, token, new { name = "Invalid", contact_email = "not-email" })).StatusCode);
        var name = $"Empresa-{Guid.NewGuid():N}";
        using var created = await Send(HttpMethod.Post, path, token, new { name, cnpj = "12.345/678-90", contact_name = "Ana", contact_email = "ana@example.com", contact_phone = "123", notes = "nota" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("12.345/678-90", body.RootElement.GetProperty("cnpj").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Send(HttpMethod.Post, path, token, new { name = name.ToUpperInvariant() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"{path}/{id}", token, new { contact_phone = "456" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{path}/{id}/deactivate", token)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await Send(HttpMethod.Post, path, token, new { name = name.ToUpperInvariant() })).StatusCode);
        using var inactive = await Send(HttpMethod.Get, $"{path}?q={Uri.EscapeDataString(name)}&active=0", token);
        Assert.Single(JsonDocument.Parse(await inactive.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"{path}/{id}", token)).StatusCode);
        using var trash = await Send(HttpMethod.Get, $"{path}?trashed=1&q={Uri.EscapeDataString(name)}", token);
        Assert.Single(JsonDocument.Parse(await trash.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{path}/{id}/restore", token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{path}/{id}/activate", token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"{path}/{id}/activate", token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"{path}/{id}", token)).StatusCode);

        await using var auditScope = factory.Services.CreateAsyncScope();
        var auditDatabase = auditScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var entityType = type == "industries" ? "industry" : "supplier";
        var events = await auditDatabase.AuditLogs.Where(log => log.EntityType == entityType && log.EntityId == id).ToListAsync();
        Assert.Equal(["activate", "create", "deactivate", "delete", "restore", "update"], events.Select(log => log.Action).OrderBy(action => action));
        Assert.All(events, log => Assert.NotNull(log.UserId));
        var createdBy = events.Single(log => log.Action == "create").UserId;
        var updatedBy = events.Single(log => log.Action == "activate").UserId;
        ContactLookup persisted = type == "industries"
            ? await auditDatabase.Industries.SingleAsync(value => value.Id == id)
            : await auditDatabase.Suppliers.SingleAsync(value => value.Id == id);
        Assert.Equal(createdBy, typeof(ContactLookup).GetProperty("CreatedBy")?.GetValue(persisted));
        Assert.Equal(updatedBy, typeof(ContactLookup).GetProperty("UpdatedBy")?.GetValue(persisted));
        Assert.False(body.RootElement.TryGetProperty("createdBy", out _));
        var updateEvent = events.Single(log => log.Action == "update");
        using var updateBefore = JsonDocument.Parse(updateEvent.BeforeJson!);
        using var updateAfter = JsonDocument.Parse(updateEvent.AfterJson!);
        Assert.Equal("123", updateBefore.RootElement.GetProperty("contact_phone").GetString());
        Assert.Equal("456", updateAfter.RootElement.GetProperty("contact_phone").GetString());
    }

    [Theory]
    [InlineData("industries")]
    [InlineData("suppliers")]
    public async Task Nullable_contact_fields_can_be_cleared(string type)
    {
        var token = await Token(["lookups.view", "lookups.manage"]);
        var path = $"/api/v1/{type}";
        using var created = await Send(HttpMethod.Post, path, token, new { name = $"Empresa-{Guid.NewGuid():N}", contact_phone = "123" });
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();
        using var updated = await Send(HttpMethod.Put, $"{path}/{id}", token, new { contact_phone = (string?)null });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(await updated.Content.ReadAsStringAsync()).RootElement.GetProperty("contact_phone").ValueKind);
    }

    [Theory]
    [InlineData("industries", "industry")]
    [InlineData("suppliers", "supplier")]
    public async Task Contact_lookup_purge_requires_trash_and_confirmation_then_audits(string type, string entityType)
    {
        var token = await Token(["lookups.view", "lookups.manage", "lookups.delete", "lookups.purge"]);
        var name = $"Purge {type} {Guid.NewGuid():N}";
        var path = $"/api/v1/{type}";
        using var created = await Send(HttpMethod.Post, path, token, new { name });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var active = await Send(HttpMethod.Post, $"{path}/{id}/purge", token, new { confirm = name });
        Assert.Equal(HttpStatusCode.Conflict, active.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"{path}/{id}", token)).StatusCode);
        using var wrong = await Send(HttpMethod.Post, $"{path}/{id}/purge", token, new { confirm = "wrong" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);
        using var purged = await Send(HttpMethod.Post, $"{path}/{id}/purge", token, new { confirm = name });
        Assert.Equal(HttpStatusCode.OK, purged.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var audit = await database.AuditLogs.SingleAsync(log => log.Action == "purge" && log.EntityType == entityType && log.EntityId == id);
        Assert.Equal(name, JsonDocument.Parse(audit.BeforeJson!).RootElement.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("industries")]
    [InlineData("suppliers")]
    public async Task Contact_lookup_purge_is_blocked_by_legacy_domain_references(string type)
    {
        var token = await Token(["lookups.view", "lookups.manage", "lookups.delete", "lookups.purge"]);
        var name = $"Referenced {type} {Guid.NewGuid():N}";
        var path = $"/api/v1/{type}";
        using var created = await Send(HttpMethod.Post, path, token, new { name });
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Categories.Add(new Category { Id = categoryId, Name = $"Reference {categoryId:N}", NameKey = categoryId.ToString("N") });
            database.CatalogItems.Add(new CatalogItem
            {
                Id = itemId,
                Code = $"REF-{itemId:N}"[..12],
                Name = "Brinde de referência",
                CategoryId = categoryId,
                SupplierId = type == "suppliers" ? id : null
            });
            if (type == "industries")
                database.StockMovements.Add(new StockMovement { ItemId = itemId, IndustryId = id, Type = "entrada", Quantity = 1, BalanceAfter = 1 });
            await database.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"{path}/{id}", token)).StatusCode);

        using var response = await Send(HttpMethod.Post, $"{path}/{id}/purge", token, new { confirm = name });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("IN_USE", await response.Content.ReadAsStringAsync());

        await using var cleanupScope = factory.Services.CreateAsyncScope();
        var cleanup = cleanupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        cleanup.StockMovements.RemoveRange(cleanup.StockMovements.Where(movement => movement.ItemId == itemId));
        cleanup.CatalogItems.RemoveRange(cleanup.CatalogItems.Where(item => item.Id == itemId));
        cleanup.Categories.RemoveRange(cleanup.Categories.Where(category => category.Id == categoryId));
        if (type == "industries") cleanup.Industries.RemoveRange(cleanup.Industries.Where(industry => industry.Id == id));
        else cleanup.Suppliers.RemoveRange(cleanup.Suppliers.Where(supplier => supplier.Id == id));
        await cleanup.SaveChangesAsync();
    }

    [Fact]
    public async Task Industry_purge_is_blocked_by_event_allocations()
    {
        var token = await Token(["lookups.view", "lookups.manage", "lookups.delete", "lookups.purge"]);
        var name = $"Allocated industry {Guid.NewGuid():N}";
        using var created = await Send(HttpMethod.Post, "/api/v1/industries", token, new { name });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var industryId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                database.Categories.Add(new Category { Id = categoryId, Name = $"Event category {categoryId:N}", NameKey = categoryId.ToString("N") });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"EVT-{itemId:N}"[..12], Name = "Allocated event item", CategoryId = categoryId });
                database.Events.Add(new Event { Id = eventId, Name = $"Event {eventId:N}" });
                await database.SaveChangesAsync();
                database.EventAllocations.Add(new EventAllocation
                {
                    EventId = eventId,
                    IndustryId = industryId,
                    ItemId = itemId,
                    QuantityAllocated = 4
                });
                await database.SaveChangesAsync();
            }

            Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"/api/v1/industries/{industryId}", token)).StatusCode);
            using var purge = await Send(HttpMethod.Post, $"/api/v1/industries/{industryId}/purge", token, new { confirm = name });
            Assert.Equal(HttpStatusCode.Conflict, purge.StatusCode);
            Assert.Contains("IN_USE", await purge.Content.ReadAsStringAsync());
        }
        finally
        {
            await using var cleanupScope = factory.Services.CreateAsyncScope();
            var cleanup = cleanupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            cleanup.EventAllocations.RemoveRange(cleanup.EventAllocations.Where(value => value.EventId == eventId));
            cleanup.Events.RemoveRange(cleanup.Events.Where(value => value.Id == eventId));
            cleanup.CatalogItems.RemoveRange(cleanup.CatalogItems.Where(value => value.Id == itemId));
            cleanup.Categories.RemoveRange(cleanup.Categories.Where(value => value.Id == categoryId));
            cleanup.AuditLogs.RemoveRange(cleanup.AuditLogs.Where(value => value.EntityType == "industry" && value.EntityId == industryId));
            cleanup.Industries.RemoveRange(cleanup.Industries.Where(value => value.Id == industryId));
            await cleanup.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Industry_purge_is_blocked_by_delivery_protocols()
    {
        var token = await Token(["lookups.view", "lookups.manage", "lookups.delete", "lookups.purge"]);
        var name = $"Delivered industry {Guid.NewGuid():N}";
        using var created = await Send(HttpMethod.Post, "/api/v1/industries", token, new { name });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var industryId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var requestId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var requester = new ApplicationUser
        {
            UserName = $"delivery-{Guid.NewGuid():N}@example.invalid",
            Email = $"delivery-{Guid.NewGuid():N}@example.invalid",
            EmailConfirmed = true,
            FullName = "Delivery requester"
        };

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                Assert.True((await users.CreateAsync(requester, "CorrectPassword1")).Succeeded);
                var database = services.GetRequiredService<ApplicationDbContext>();
                database.Requests.Add(new InternalRequest
                {
                    Id = requestId,
                    Code = $"REQ-{requestId:N}"[..24],
                    RequesterId = requester.Id,
                    Purpose = "Protocolo de evento"
                });
                database.Deliveries.Add(new Delivery
                {
                    Id = deliveryId,
                    Code = $"DL-{deliveryId:N}"[..20],
                    Type = "trade",
                    RequestId = requestId,
                    IndustryId = industryId,
                    ReceivedByName = "Recebedor",
                    SignaturePath = "private/test-signature",
                    IdempotencyKeyHash = new string('a', 64),
                    RequestHash = new string('b', 64)
                });
                await database.SaveChangesAsync();
            }

            Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"/api/v1/industries/{industryId}", token)).StatusCode);
            using var purge = await Send(HttpMethod.Post, $"/api/v1/industries/{industryId}/purge", token, new { confirm = name });
            Assert.Equal(HttpStatusCode.Conflict, purge.StatusCode);
            Assert.Contains("IN_USE", await purge.Content.ReadAsStringAsync());
        }
        finally
        {
            await using var cleanupScope = factory.Services.CreateAsyncScope();
            var services = cleanupScope.ServiceProvider;
            var cleanup = services.GetRequiredService<ApplicationDbContext>();
            cleanup.AuditLogs.RemoveRange(cleanup.AuditLogs.Where(value => value.EntityType == "industry" && value.EntityId == industryId));
            cleanup.Deliveries.RemoveRange(cleanup.Deliveries.Where(value => value.Id == deliveryId));
            cleanup.Requests.RemoveRange(cleanup.Requests.Where(value => value.Id == requestId));
            cleanup.Industries.RemoveRange(cleanup.Industries.Where(value => value.Id == industryId));
            await cleanup.SaveChangesAsync();
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var persistedRequester = await users.FindByIdAsync(requester.Id.ToString());
            if (persistedRequester is not null) Assert.True((await users.DeleteAsync(persistedRequester)).Succeeded);
        }
    }

    [Fact]
    public async Task Supplier_purge_is_blocked_by_stock_movement_after_catalog_supplier_is_cleared()
    {
        var token = await Token(["lookups.view", "lookups.manage", "lookups.delete", "lookups.purge", "items.view", "items.manage", "stock.entry"]);
        var supplierName = $"Movement supplier {Guid.NewGuid():N}";
        var alternateSupplierName = $"Alternate supplier {Guid.NewGuid():N}";
        using var createdSupplier = await Send(HttpMethod.Post, "/api/v1/suppliers", token, new { name = supplierName });
        Assert.Equal(HttpStatusCode.Created, createdSupplier.StatusCode);
        var supplierId = JsonDocument.Parse(await createdSupplier.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var createdAlternate = await Send(HttpMethod.Post, "/api/v1/suppliers", token, new { name = alternateSupplierName });
        Assert.Equal(HttpStatusCode.Created, createdAlternate.StatusCode);
        var alternateSupplierId = JsonDocument.Parse(await createdAlternate.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var itemCode = $"MOV-{itemId:N}"[..12];
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Categories.Add(new Category { Id = categoryId, Name = $"Movement category {categoryId:N}", NameKey = categoryId.ToString("N") });
            database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = itemCode, Name = "Recebimento com fornecedor", CategoryId = categoryId, SupplierId = supplierId });
            await database.SaveChangesAsync();
        }

        try
        {
            var idempotencyKey = $"supplier-{Guid.NewGuid():N}";
            using var entry = await Send(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", token,
                new { quantity = 1, supplier_id = supplierId, idempotency_key = idempotencyKey });
            Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
            Assert.Equal(supplierId, JsonDocument.Parse(await entry.Content.ReadAsStringAsync()).RootElement.GetProperty("supplier_id").GetGuid());
            using var changedReplay = await Send(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", token,
                new { quantity = 1, supplier_id = alternateSupplierId, idempotency_key = idempotencyKey });
            Assert.Equal(HttpStatusCode.Conflict, changedReplay.StatusCode);
            using var itemUpdated = await Send(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { supplierId = (Guid?)null });
            Assert.Equal(HttpStatusCode.OK, itemUpdated.StatusCode);
            await using (var verifyScope = factory.Services.CreateAsyncScope())
            {
                var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Null(await verifyDb.CatalogItems.Where(item => item.Id == itemId).Select(item => item.SupplierId).SingleAsync());
            }
            Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, $"/api/v1/suppliers/{supplierId}", token)).StatusCode);

            using var purge = await Send(HttpMethod.Post, $"/api/v1/suppliers/{supplierId}/purge", token, new { confirm = supplierName });
            Assert.Equal(HttpStatusCode.Conflict, purge.StatusCode);
            Assert.Contains("IN_USE", await purge.Content.ReadAsStringAsync());
        }
        finally
        {
            await using var cleanupScope = factory.Services.CreateAsyncScope();
            var cleanup = cleanupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            cleanup.StockPositions.RemoveRange(cleanup.StockPositions.Where(position => position.ItemId == itemId));
            cleanup.StockMovements.RemoveRange(cleanup.StockMovements.Where(movement => movement.ItemId == itemId));
            cleanup.Stocks.RemoveRange(cleanup.Stocks.Where(stock => stock.ItemId == itemId));
            cleanup.CatalogItems.RemoveRange(cleanup.CatalogItems.Where(item => item.Id == itemId));
            cleanup.Categories.RemoveRange(cleanup.Categories.Where(category => category.Id == categoryId));
            var supplier = await cleanup.Suppliers.SingleOrDefaultAsync(value => value.Id == supplierId);
            if (supplier is not null) cleanup.Suppliers.Remove(supplier);
            var alternate = await cleanup.Suppliers.SingleOrDefaultAsync(value => value.Id == alternateSupplierId);
            if (alternate is not null) cleanup.Suppliers.Remove(alternate);
            await cleanup.SaveChangesAsync();
        }
    }

    private async Task<string> Token(string[] permissions)
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = new ApplicationRole($"lookup-{Guid.NewGuid():N}");
        Assert.True((await roles.CreateAsync(role)).Succeeded);
        foreach (var permission in permissions) Assert.True((await roles.AddClaimAsync(role, new("permission", permission))).Succeeded);
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Test" };
        Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role.Name!)).Succeeded);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }
}
