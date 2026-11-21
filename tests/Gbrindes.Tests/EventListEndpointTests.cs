using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Gbrindes.Web.Data;
using Gbrindes.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gbrindes.Tests;

public sealed class EventListEndpointTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task Events_viewer_gets_laravel_pagination_with_allocation_counts()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"event-viewer-{Guid.NewGuid():N}";
        var eventName = $"Listed event {Guid.NewGuid():N}";
        Guid? userId = null;
        Guid? eventId = null;
        Guid? industryId = null;
        Guid? categoryId = null;
        Guid? itemId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.manage"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.view"))).Succeeded);
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = "Event viewer"
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
            }

            var token = await LoginAsync(client, email);
            using (var created = await SendAsync(client, HttpMethod.Post, "/api/v1/events", token, new { name = eventName }))
            {
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
                eventId = createdJson.RootElement.GetProperty("id").GetGuid();
            }

            industryId = Guid.NewGuid();
            categoryId = Guid.NewGuid();
            itemId = Guid.NewGuid();
            await using (var allocationScope = factory.Services.CreateAsyncScope())
            {
                var database = allocationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                database.Industries.Add(new Industry { Id = industryId.Value, Name = $"Event industry {industryId:N}", NameKey = industryId.Value.ToString("N") });
                database.Categories.Add(new Category { Id = categoryId.Value, Name = $"Event category {categoryId:N}", NameKey = categoryId.Value.ToString("N") });
                database.CatalogItems.Add(new CatalogItem { Id = itemId.Value, Code = $"EVT-{itemId.Value:N}"[..12], Name = "Event allocation item", CategoryId = categoryId.Value });
                database.EventAllocations.Add(new EventAllocation
                {
                    EventId = eventId.Value,
                    IndustryId = industryId.Value,
                    ItemId = itemId.Value,
                    QuantityAllocated = 5,
                    QuantityWithdrawn = 2
                });
                await database.SaveChangesAsync();
            }

            using var listed = await SendAsync(client, HttpMethod.Get, "/api/v1/events?page=1", token);
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            using var json = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
            var page = json.RootElement;
            Assert.Equal(1, page.GetProperty("current_page").GetInt32());
            Assert.Equal(25, page.GetProperty("per_page").GetInt32());
            Assert.Equal(1, page.GetProperty("total").GetInt32());
            var events = page.GetProperty("data");
            Assert.Single(events.EnumerateArray());
            Assert.Equal(eventId, events[0].GetProperty("id").GetGuid());
            Assert.Equal(eventName, events[0].GetProperty("name").GetString());
            Assert.Equal(1, events[0].GetProperty("allocations_count").GetInt32());
        }
        finally
        {
            await CleanupAsync(eventId, userId, roleName, industryId, categoryId, itemId);
        }
    }

    [Fact]
    public async Task Legacy_event_list_filters_name_and_venue_and_returns_legacy_pagination()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"legacy-event-viewer-{Guid.NewGuid():N}";
        var marker = $"legacy-{Guid.NewGuid():N}";
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var venueMatchId = Guid.NewGuid();
        var wrongStatusId = Guid.NewGuid();
        Guid? userId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.view"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Legacy event viewer" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;

                database.Events.AddRange(
                    new Event { Id = firstId, Name = $"{marker} spring", Status = "planejado", StartsOn = new DateOnly(2026, 4, 10) },
                    new Event { Id = secondId, Name = $"{marker} autumn", Status = "planejado", StartsOn = new DateOnly(2026, 8, 10) },
                    new Event { Id = venueMatchId, Name = "Venue-only result", Venue = marker, Status = "planejado", StartsOn = new DateOnly(2026, 6, 10) },
                    new Event { Id = wrongStatusId, Name = $"{marker} cancelled", Status = "cancelado", StartsOn = new DateOnly(2026, 9, 10) });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var firstPage = await SendAsync(client, HttpMethod.Get,
                $"/api/events?q={Uri.EscapeDataString(marker.ToUpperInvariant())}&status=planejado&page=1&per_page=1", token);
            Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
            using var firstJson = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
            var firstRoot = firstJson.RootElement;
            Assert.True(firstRoot.GetProperty("ok").GetBoolean());
            Assert.Equal(1, firstRoot.GetProperty("meta").GetProperty("page").GetInt32());
            Assert.Equal(1, firstRoot.GetProperty("meta").GetProperty("per_page").GetInt32());
            Assert.Equal(3, firstRoot.GetProperty("meta").GetProperty("total").GetInt32());
            Assert.Equal(3, firstRoot.GetProperty("meta").GetProperty("last_page").GetInt32());
            var firstRow = Assert.Single(firstRoot.GetProperty("data").EnumerateArray());
            Assert.Equal(secondId, firstRow.GetProperty("id").GetGuid());
            Assert.Equal("Planejado", firstRow.GetProperty("status_label").GetString());
            Assert.True(firstRow.TryGetProperty("location_id", out _));

            using var secondPage = await SendAsync(client, HttpMethod.Get,
                $"/api/events?q={Uri.EscapeDataString(marker)}&status=planejado&page=2&per_page=1", token);
            Assert.Equal(HttpStatusCode.OK, secondPage.StatusCode);
            using var secondJson = JsonDocument.Parse(await secondPage.Content.ReadAsStringAsync());
            Assert.Equal(venueMatchId, Assert.Single(secondJson.RootElement.GetProperty("data").EnumerateArray()).GetProperty("id").GetGuid());
            using var thirdPage = await SendAsync(client, HttpMethod.Get,
                $"/api/events?q={Uri.EscapeDataString(marker)}&status=planejado&page=3&per_page=1", token);
            using var thirdJson = JsonDocument.Parse(await thirdPage.Content.ReadAsStringAsync());
            Assert.Equal(firstId, Assert.Single(thirdJson.RootElement.GetProperty("data").EnumerateArray()).GetProperty("id").GetGuid());

            using var defaultPage = await SendAsync(client, HttpMethod.Get,
                $"/api/events?q={Uri.EscapeDataString(marker)}&status=planejado&page=0&per_page=0", token);
            using var defaultJson = JsonDocument.Parse(await defaultPage.Content.ReadAsStringAsync());
            Assert.Equal(1, defaultJson.RootElement.GetProperty("meta").GetProperty("page").GetInt32());
            Assert.Equal(25, defaultJson.RootElement.GetProperty("meta").GetProperty("per_page").GetInt32());
            Assert.Equal(3, defaultJson.RootElement.GetProperty("data").GetArrayLength());
        }
        finally
        {
            await using (var cleanupScope = factory.Services.CreateAsyncScope())
            {
                var database = cleanupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var ids = new[] { firstId, secondId, venueMatchId, wrongStatusId };
                database.Events.RemoveRange(await database.Events.Where(eventRecord => ids.Contains(eventRecord.Id)).ToListAsync());
                await database.SaveChangesAsync();
            }
            await CleanupAsync(null, userId, roleName, null, null, null);
        }
    }

    [Fact]
    public async Task Event_manager_opens_and_closes_through_php_routes_with_stock_reservations()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"event-open-manager-{Guid.NewGuid():N}";
        var eventId = Guid.NewGuid();
        var industryId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        Guid? userId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.manage"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Event opener" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;

                database.Events.Add(new Event { Id = eventId, Name = $"Open event {eventId:N}" });
                database.Industries.Add(new Industry { Id = industryId, Name = $"Open industry {industryId:N}", NameKey = industryId.ToString("N") });
                database.Categories.Add(new Category { Id = categoryId, Name = $"Open category {categoryId:N}", NameKey = categoryId.ToString("N") });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"OPN-{itemId:N}"[..12], Name = "Event reserve item", CategoryId = categoryId });
                database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 20, Reserved = 2 });
                database.EventAllocations.Add(new EventAllocation { EventId = eventId, IndustryId = industryId, ItemId = itemId, QuantityAllocated = 12 });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var opened = await SendAsync(client, HttpMethod.Post, $"/api/events/{eventId}/open", token, new { });
            Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
            using var openedJson = JsonDocument.Parse(await opened.Content.ReadAsStringAsync());
            Assert.True(openedJson.RootElement.GetProperty("ok").GetBoolean());
            var openedData = openedJson.RootElement.GetProperty("data");
            Assert.Equal("aberto", openedData.GetProperty("status").GetString());
            Assert.True(openedData.GetProperty("opened_at").ValueKind == JsonValueKind.String);

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(userId, (await verify.Events.SingleAsync(eventRecord => eventRecord.Id == eventId)).UpdatedBy);
            Assert.Equal(14, (await verify.Stocks.SingleAsync(stock => stock.ItemId == itemId)).Reserved);
            Assert.Single(await verify.AuditLogs.Where(log => log.EntityType == "event" && log.EntityId == eventId && log.Action == "open").ToListAsync());

            var stockAfterWithdrawal = await verify.Stocks.SingleAsync(stock => stock.ItemId == itemId);
            stockAfterWithdrawal.OnHand -= 3;
            stockAfterWithdrawal.Reserved -= 3;
            var partiallyWithdrawn = await verify.EventAllocations.SingleAsync(allocation => allocation.EventId == eventId);
            partiallyWithdrawn.QuantityWithdrawn = 3;
            await verify.SaveChangesAsync();

            using var closed = await SendAsync(client, HttpMethod.Post, $"/api/events/{eventId}/close", token, new { });
            Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
            using var closedJson = JsonDocument.Parse(await closed.Content.ReadAsStringAsync());
            Assert.True(closedJson.RootElement.GetProperty("ok").GetBoolean());
            var closedData = closedJson.RootElement.GetProperty("data");
            Assert.Equal("encerrado", closedData.GetProperty("status").GetString());
            Assert.True(closedData.GetProperty("closed_at").ValueKind == JsonValueKind.String);

            await using var closedVerifyScope = factory.Services.CreateAsyncScope();
            var closedVerify = closedVerifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(userId, (await closedVerify.Events.SingleAsync(eventRecord => eventRecord.Id == eventId)).UpdatedBy);
            Assert.Equal(2, (await closedVerify.Stocks.SingleAsync(stock => stock.ItemId == itemId)).Reserved);
            Assert.Single(await closedVerify.AuditLogs.Where(log => log.EntityType == "event" && log.EntityId == eventId && log.Action == "close").ToListAsync());
        }
        finally
        {
            await CleanupAsync(eventId, userId, roleName, industryId, categoryId, itemId);
        }
    }

    [Fact]
    public async Task Legacy_event_close_releases_only_the_unwithdrawn_reservation()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"legacy-event-close-manager-{Guid.NewGuid():N}";
        var eventId = Guid.NewGuid();
        var industryId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        Guid? userId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.manage"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Legacy event closer" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;

                database.Events.Add(new Event { Id = eventId, Name = $"Legacy close event {eventId:N}", Status = "aberto" });
                database.Industries.Add(new Industry { Id = industryId, Name = $"Legacy close industry {industryId:N}", NameKey = industryId.ToString("N") });
                database.Categories.Add(new Category { Id = categoryId, Name = $"Legacy close category {categoryId:N}", NameKey = categoryId.ToString("N") });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"LCL-{itemId:N}"[..12], Name = "Legacy close item", CategoryId = categoryId });
                database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 20, Reserved = 11 });
                database.EventAllocations.Add(new EventAllocation
                {
                    EventId = eventId,
                    IndustryId = industryId,
                    ItemId = itemId,
                    QuantityAllocated = 12,
                    QuantityWithdrawn = 3
                });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var response = await SendAsync(client, HttpMethod.Post, $"/api/events/{eventId}/close", token, new { });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
            var data = json.RootElement.GetProperty("data");
            Assert.Equal("encerrado", data.GetProperty("status").GetString());
            Assert.True(data.GetProperty("closed_at").ValueKind == JsonValueKind.String);

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var eventRecord = await verify.Events.SingleAsync(value => value.Id == eventId);
            Assert.Equal(userId, eventRecord.ClosedBy);
            Assert.Equal(2, (await verify.Stocks.SingleAsync(value => value.ItemId == itemId)).Reserved);
            Assert.Single(await verify.AuditLogs.Where(log => log.EntityType == "event" && log.EntityId == eventId && log.Action == "close").ToListAsync());
        }
        finally
        {
            await CleanupAsync(eventId, userId, roleName, industryId, categoryId, itemId);
        }
    }

    [Fact]
    public async Task Legacy_event_open_and_close_errors_use_php_envelopes_without_side_effects()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"legacy-event-workflow-manager-{Guid.NewGuid():N}";
        var eventId = Guid.NewGuid();
        Guid? userId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.manage"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Legacy workflow manager" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Events.Add(new Event { Id = eventId, Name = $"No allocation event {eventId:N}" });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var open = await SendAsync(client, HttpMethod.Post, $"/api/events/{eventId}/open", token, new { });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, open.StatusCode);
            using var openJson = JsonDocument.Parse(await open.Content.ReadAsStringAsync());
            Assert.False(openJson.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("NO_ALLOCATIONS", openJson.RootElement.GetProperty("error").GetProperty("code").GetString());

            using var close = await SendAsync(client, HttpMethod.Post, $"/api/events/{eventId}/close", token, new { });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, close.StatusCode);
            using var closeJson = JsonDocument.Parse(await close.Content.ReadAsStringAsync());
            Assert.False(closeJson.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("EVENT_NOT_OPEN", closeJson.RootElement.GetProperty("error").GetProperty("code").GetString());

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal("planejado", (await verify.Events.SingleAsync(value => value.Id == eventId)).Status);
            Assert.Empty(await verify.AuditLogs.Where(log => log.EntityType == "event" && log.EntityId == eventId).ToListAsync());
        }
        finally
        {
            await CleanupAsync(eventId, userId, roleName, null, null, null);
        }
    }

    [Fact]
    public async Task Event_viewer_gets_event_with_allocations()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"event-detail-viewer-{Guid.NewGuid():N}";
        var eventId = Guid.NewGuid();
        Guid? userId = null;
        var industryId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.view"))).Succeeded);
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = "Event detail viewer"
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;

                database.Events.Add(new Event { Id = eventId, Name = $"Detailed event {eventId:N}" });
                database.Industries.Add(new Industry { Id = industryId, Name = $"Detail industry {industryId:N}", NameKey = industryId.ToString("N") });
                database.Categories.Add(new Category { Id = categoryId, Name = $"Detail category {categoryId:N}", NameKey = categoryId.ToString("N") });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"DET-{itemId:N}"[..12], Name = "Detail allocation item", CategoryId = categoryId });
                database.EventAllocations.Add(new EventAllocation
                {
                    EventId = eventId,
                    IndustryId = industryId,
                    ItemId = itemId,
                    QuantityAllocated = 5,
                    QuantityWithdrawn = 2
                });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var response = await SendAsync(client, HttpMethod.Get, $"/api/v1/events/{eventId}", token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var eventDetails = json.RootElement;
            Assert.Equal(eventId, eventDetails.GetProperty("id").GetGuid());
            Assert.Equal(1, eventDetails.GetProperty("allocations").GetArrayLength());
            var allocation = eventDetails.GetProperty("allocations")[0];
            Assert.Equal(industryId, allocation.GetProperty("industry_id").GetGuid());
            Assert.Equal(itemId, allocation.GetProperty("item_id").GetGuid());
            Assert.Equal(5, allocation.GetProperty("qty_allocated").GetInt32());
            Assert.Equal(2, allocation.GetProperty("qty_withdrawn").GetInt32());
            Assert.False(eventDetails.TryGetProperty("allocations_count", out _));
        }
        finally
        {
            await CleanupAsync(eventId, userId, roleName, industryId, categoryId, itemId);
        }
    }

    [Fact]
    public async Task Legacy_event_detail_uses_php_envelope_and_nested_allocation_shape()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"legacy-event-detail-viewer-{Guid.NewGuid():N}";
        var eventId = Guid.NewGuid();
        var industryId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        Guid? userId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.view"))).Succeeded);
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = "Legacy event detail viewer"
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;

                database.Events.Add(new Event
                {
                    Id = eventId,
                    Name = $"Legacy detail event {eventId:N}",
                    Status = "aberto",
                    StartsOn = new DateOnly(2026, 11, 5)
                });
                database.Industries.Add(new Industry
                {
                    Id = industryId,
                    Name = $"Legacy detail industry {industryId:N}",
                    NameKey = industryId.ToString("N")
                });
                database.Categories.Add(new Category
                {
                    Id = categoryId,
                    Name = $"Legacy detail category {categoryId:N}",
                    NameKey = categoryId.ToString("N")
                });
                database.CatalogItems.Add(new CatalogItem
                {
                    Id = itemId,
                    Code = $"LD-{itemId:N}"[..12],
                    Name = "Legacy nested item",
                    CategoryId = categoryId
                });
                database.EventAllocations.Add(new EventAllocation
                {
                    EventId = eventId,
                    IndustryId = industryId,
                    ItemId = itemId,
                    QuantityAllocated = 5,
                    QuantityWithdrawn = 2
                });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var response = await SendAsync(client, HttpMethod.Get, $"/api/events/{eventId}", token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.False(root.TryGetProperty("meta", out _));
            var data = root.GetProperty("data");
            Assert.Equal(eventId, data.GetProperty("id").GetGuid());
            Assert.Equal("aberto", data.GetProperty("status").GetString());
            Assert.Equal("Aberto", data.GetProperty("status_label").GetString());
            Assert.False(data.TryGetProperty("updated_at", out _));

            var allocation = Assert.Single(data.GetProperty("allocations").EnumerateArray());
            Assert.Equal(industryId, allocation.GetProperty("industry").GetProperty("id").GetGuid());
            Assert.Equal(itemId, allocation.GetProperty("item").GetProperty("id").GetGuid());
            Assert.Equal("Legacy nested item", allocation.GetProperty("item").GetProperty("name").GetString());
            Assert.Equal(5, allocation.GetProperty("qty_allocated").GetInt32());
            Assert.Equal(2, allocation.GetProperty("qty_withdrawn").GetInt32());
            Assert.Equal(3, allocation.GetProperty("saldo").GetInt32());

            var industry = Assert.Single(data.GetProperty("industries").EnumerateArray());
            Assert.Equal(5, industry.GetProperty("allocated").GetInt32());
            Assert.Equal(2, industry.GetProperty("withdrawn").GetInt32());
            Assert.Equal(3, industry.GetProperty("saldo").GetInt32());
            Assert.Single(data.GetProperty("returns").EnumerateArray());

            await using (var trashScope = factory.Services.CreateAsyncScope())
            {
                var database = trashScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var eventRecord = await database.Events.SingleAsync(value => value.Id == eventId);
                eventRecord.DeletedAt = DateTimeOffset.UtcNow;
                await database.SaveChangesAsync();
            }
            using var deleted = await SendAsync(client, HttpMethod.Get, $"/api/events/{eventId}", token);
            Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
            using var deletedJson = JsonDocument.Parse(await deleted.Content.ReadAsStringAsync());
            Assert.False(deletedJson.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("NOT_FOUND", deletedJson.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            await CleanupAsync(eventId, userId, roleName, industryId, categoryId, itemId);
        }
    }

    [Fact]
    public async Task Event_manager_upserts_allocations_and_enforces_quantity_and_status_rules()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"event-allocation-manager-{Guid.NewGuid():N}";
        var eventId = Guid.NewGuid();
        var industryId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        Guid? userId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.manage"))).Succeeded);
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = "Event allocation manager"
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Events.Add(new Event { Id = eventId, Name = $"Allocation event {eventId:N}" });
                database.Industries.Add(new Industry { Id = industryId, Name = $"Allocation industry {industryId:N}", NameKey = industryId.ToString("N") });
                database.Categories.Add(new Category { Id = categoryId, Name = $"Allocation category {categoryId:N}", NameKey = categoryId.ToString("N") });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"ALC-{itemId:N}"[..12], Name = "Allocation item", CategoryId = categoryId });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var created = await SendAsync(client, HttpMethod.Post, $"/api/v1/events/{eventId}/allocations", token,
                new { industry_id = industryId, item_id = itemId, qty_allocated = 5 });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var allocationId = createdJson.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(eventId, createdJson.RootElement.GetProperty("event_id").GetGuid());
            Assert.Equal(industryId, createdJson.RootElement.GetProperty("industry_id").GetGuid());
            Assert.Equal(itemId, createdJson.RootElement.GetProperty("item_id").GetGuid());
            Assert.Equal(5, createdJson.RootElement.GetProperty("qty_allocated").GetInt32());
            Assert.Equal(0, createdJson.RootElement.GetProperty("qty_withdrawn").GetInt32());

            using var updated = await SendAsync(client, HttpMethod.Post, $"/api/v1/events/{eventId}/allocations", token,
                new { industry_id = industryId, item_id = itemId, qty_allocated = 7 });
            Assert.Equal(HttpStatusCode.Created, updated.StatusCode);
            using var updatedJson = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
            Assert.Equal(allocationId, updatedJson.RootElement.GetProperty("id").GetGuid());
            Assert.Equal(7, updatedJson.RootElement.GetProperty("qty_allocated").GetInt32());

            using var zeroQuantity = await SendAsync(client, HttpMethod.Post, $"/api/v1/events/{eventId}/allocations", token,
                new { industry_id = industryId, item_id = itemId, qty_allocated = 0 });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, zeroQuantity.StatusCode);
            using var zeroJson = JsonDocument.Parse(await zeroQuantity.Content.ReadAsStringAsync());
            Assert.True(zeroJson.RootElement.GetProperty("errors").TryGetProperty("qty_allocated", out _));

            await using (var invariantScope = factory.Services.CreateAsyncScope())
            {
                var database = invariantScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var allocation = await database.EventAllocations.SingleAsync(value => value.Id == allocationId);
                allocation.QuantityWithdrawn = 2;
                await database.SaveChangesAsync();
            }
            using var belowWithdrawn = await SendAsync(client, HttpMethod.Post, $"/api/v1/events/{eventId}/allocations", token,
                new { industry_id = industryId, item_id = itemId, qty_allocated = 1 });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, belowWithdrawn.StatusCode);

            await using (var closedEventScope = factory.Services.CreateAsyncScope())
            {
                var database = closedEventScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var eventRecord = await database.Events.FindAsync(eventId);
                Assert.NotNull(eventRecord);
                eventRecord.Status = "encerrado";
                await database.SaveChangesAsync();
            }
            using var missingEvent = await SendAsync(client, HttpMethod.Post,
                $"/api/v1/events/{Guid.NewGuid()}/allocations", token,
                new { industry_id = industryId, item_id = itemId, qty_allocated = 0 });
            Assert.Equal(HttpStatusCode.NotFound, missingEvent.StatusCode);

            using var closedEventWithInvalidQuantity = await SendAsync(client, HttpMethod.Post,
                $"/api/v1/events/{eventId}/allocations", token,
                new { industry_id = industryId, item_id = itemId, qty_allocated = 0 });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, closedEventWithInvalidQuantity.StatusCode);
            using var closedJson = JsonDocument.Parse(await closedEventWithInvalidQuantity.Content.ReadAsStringAsync());
            Assert.Equal("O evento não está disponível para cotas.", closedJson.RootElement.GetProperty("message").GetString());

            using var closedEvent = await SendAsync(client, HttpMethod.Post, $"/api/v1/events/{eventId}/allocations", token,
                new { industry_id = industryId, item_id = itemId, qty_allocated = 8 });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, closedEvent.StatusCode);

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(1, await verifyDatabase.EventAllocations.CountAsync(value => value.EventId == eventId));
            var finalAllocation = await verifyDatabase.EventAllocations.SingleAsync(value => value.Id == allocationId);
            Assert.Equal(7, finalAllocation.QuantityAllocated);
            Assert.Equal(2, finalAllocation.QuantityWithdrawn);
        }
        finally
        {
            await CleanupAsync(eventId, userId, roleName, industryId, categoryId, itemId);
        }
    }

    [Fact]
    public async Task Event_allocation_replacement_preserves_php_and_laravel_contracts()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"event-batch-manager-{Guid.NewGuid():N}";
        var eventId = Guid.NewGuid();
        var industryId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var firstItemId = Guid.NewGuid();
        var secondItemId = Guid.NewGuid();
        Guid? userId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.manage"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Event batch manager" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;

                database.Events.Add(new Event { Id = eventId, Name = $"Batch event {eventId:N}" });
                database.Industries.Add(new Industry { Id = industryId, Name = $"Batch industry {industryId:N}", NameKey = industryId.ToString("N") });
                database.Categories.Add(new Category { Id = categoryId, Name = $"Batch category {categoryId:N}", NameKey = categoryId.ToString("N") });
                database.CatalogItems.AddRange(
                    new CatalogItem { Id = firstItemId, Code = $"B1-{firstItemId:N}"[..12], Name = "First batch item", CategoryId = categoryId },
                    new CatalogItem { Id = secondItemId, Code = $"B2-{secondItemId:N}"[..12], Name = "Second batch item", CategoryId = categoryId });
                database.EventAllocations.AddRange(
                    new EventAllocation { EventId = eventId, IndustryId = industryId, ItemId = firstItemId, QuantityAllocated = 5 },
                    new EventAllocation { EventId = eventId, IndustryId = industryId, ItemId = secondItemId, QuantityAllocated = 4 });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var replaced = await SendAsync(client, HttpMethod.Put, $"/api/v1/events/{eventId}/allocations", token, new
            {
                allocations = new[]
                {
                    new { industry_id = industryId, item_id = firstItemId, qty_allocated = 7 },
                    new { industry_id = industryId, item_id = secondItemId, qty_allocated = 0 }
                }
            });
            Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
            using var replacedJson = JsonDocument.Parse(await replaced.Content.ReadAsStringAsync());
            var allocations = replacedJson.RootElement.GetProperty("allocations");
            Assert.Single(allocations.EnumerateArray());
            Assert.Equal(firstItemId, allocations[0].GetProperty("item_id").GetGuid());
            Assert.Equal(7, allocations[0].GetProperty("qty_allocated").GetInt32());

            using var legacyReplaced = await SendAsync(client, HttpMethod.Put, $"/api/events/{eventId}/allocations", token,
                new[]
                {
                    new { industry_id = industryId, item_id = firstItemId, qty_allocated = 8 },
                    new { industry_id = industryId, item_id = secondItemId, qty_allocated = 0 }
                });
            Assert.Equal(HttpStatusCode.OK, legacyReplaced.StatusCode);
            using var legacyJson = JsonDocument.Parse(await legacyReplaced.Content.ReadAsStringAsync());
            Assert.True(legacyJson.RootElement.GetProperty("ok").GetBoolean());
            var legacyAllocations = legacyJson.RootElement.GetProperty("data").GetProperty("allocations");
            var legacyAllocation = Assert.Single(legacyAllocations.EnumerateArray());
            Assert.Equal(firstItemId, legacyAllocation.GetProperty("item").GetProperty("id").GetGuid());
            Assert.Equal(industryId, legacyAllocation.GetProperty("industry").GetProperty("id").GetGuid());
            Assert.Equal(8, legacyAllocation.GetProperty("qty_allocated").GetInt32());

            using var legacyInvalid = await SendAsync(client, HttpMethod.Put, $"/api/events/{eventId}/allocations", token,
                new[] { new { industry_id = industryId, item_id = firstItemId, qty_allocated = -1 } });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, legacyInvalid.StatusCode);
            using var legacyInvalidJson = JsonDocument.Parse(await legacyInvalid.Content.ReadAsStringAsync());
            Assert.Equal("VALIDATION_ERROR", legacyInvalidJson.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal("Deve ser no mínimo 0.", legacyInvalidJson.RootElement.GetProperty("error").GetProperty("fields").GetProperty("qty_allocated").GetString());

            using var missingEvent = await SendAsync(client, HttpMethod.Put,
                $"/api/events/{Guid.NewGuid()}/allocations", token, Array.Empty<object>());
            Assert.Equal(HttpStatusCode.NotFound, missingEvent.StatusCode);
            using var missingEventJson = JsonDocument.Parse(await missingEvent.Content.ReadAsStringAsync());
            Assert.Equal("NOT_FOUND", missingEventJson.RootElement.GetProperty("error").GetProperty("code").GetString());

            await using (var openScope = factory.Services.CreateAsyncScope())
            {
                var database = openScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var eventRecord = await database.Events.SingleAsync(value => value.Id == eventId);
                eventRecord.Status = "aberto";
                await database.SaveChangesAsync();
            }
            using var rejected = await SendAsync(client, HttpMethod.Put, $"/api/v1/events/{eventId}/allocations", token,
                new { allocations = Array.Empty<object>() });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
            using var legacyRejected = await SendAsync(client, HttpMethod.Put, $"/api/events/{eventId}/allocations", token,
                new { allocations = Array.Empty<object>() });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, legacyRejected.StatusCode);
            using var legacyError = JsonDocument.Parse(await legacyRejected.Content.ReadAsStringAsync());
            Assert.False(legacyError.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("EVENT_NOT_PLANNED", legacyError.RootElement.GetProperty("error").GetProperty("code").GetString());

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var saved = await verify.EventAllocations.Where(value => value.EventId == eventId).ToListAsync();
            var remaining = Assert.Single(saved);
            Assert.Equal(firstItemId, remaining.ItemId);
            Assert.Equal(8, remaining.QuantityAllocated);
            Assert.Equal(2, await verify.AuditLogs.CountAsync(log => log.EntityType == "event" && log.EntityId == eventId && log.Action == "update"));
        }
        finally
        {
            await CleanupAsync(eventId, userId, roleName, industryId, categoryId, firstItemId, secondItemId);
        }
    }

    [Fact]
    public async Task Event_viewer_without_events_manage_cannot_change_php_or_laravel_allocations()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"event-viewer-no-manage-{Guid.NewGuid():N}";
        Guid? userId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.view"))).Succeeded);
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = "Event viewer without manage"
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
            }

            var token = await LoginAsync(client, email);
            using var response = await SendAsync(client, HttpMethod.Post,
                $"/api/v1/events/{Guid.NewGuid()}/allocations", token,
                new { industry_id = Guid.NewGuid(), item_id = Guid.NewGuid(), qty_allocated = 2 });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var legacyResponse = await SendAsync(client, HttpMethod.Put,
                $"/api/events/{Guid.NewGuid()}/allocations", token, Array.Empty<object>());
            Assert.Equal(HttpStatusCode.Forbidden, legacyResponse.StatusCode);
            using var deniedOpen = await SendAsync(client, HttpMethod.Post,
                $"/api/events/{Guid.NewGuid()}/open", token, new { });
            Assert.Equal(HttpStatusCode.Forbidden, deniedOpen.StatusCode);
            using var deniedClose = await SendAsync(client, HttpMethod.Post,
                $"/api/events/{Guid.NewGuid()}/close", token, new { });
            Assert.Equal(HttpStatusCode.Forbidden, deniedClose.StatusCode);
        }
        finally
        {
            await CleanupAsync(null, userId, roleName, null, null, null);
        }
    }

    [Fact]
    public async Task Event_user_without_events_view_cannot_read_details_or_legacy_balance()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"event-detail-no-view-{Guid.NewGuid():N}";
        Guid? userId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = "Event detail denied viewer"
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
            }

            var token = await LoginAsync(client, email);
            using var response = await SendAsync(client, HttpMethod.Get, $"/api/v1/events/{Guid.NewGuid()}", token);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var legacyBalance = await SendAsync(client, HttpMethod.Get,
                $"/api/events/{Guid.NewGuid()}/balance?industry_id={Guid.NewGuid()}", token);
            Assert.Equal(HttpStatusCode.Forbidden, legacyBalance.StatusCode);
        }
        finally
        {
            await CleanupAsync(null, userId, roleName, null, null, null);
        }
    }

    [Fact]
    public async Task Event_balance_routes_preserve_distinct_industry_scope_contracts()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"event-balance-viewer-{Guid.NewGuid():N}";
        var eventId = Guid.NewGuid();
        var userIndustryId = Guid.NewGuid();
        var otherIndustryId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var userItemId = Guid.NewGuid();
        var otherItemId = Guid.NewGuid();
        Guid? userId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.view"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Event balance viewer", IndustryId = userIndustryId };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;

                database.Events.Add(new Event { Id = eventId, Name = $"Balance event {eventId:N}", Status = "aberto" });
                database.Industries.AddRange(
                    new Industry { Id = userIndustryId, Name = $"Scoped industry {userIndustryId:N}", NameKey = userIndustryId.ToString("N") },
                    new Industry { Id = otherIndustryId, Name = $"Other industry {otherIndustryId:N}", NameKey = otherIndustryId.ToString("N") });
                database.Categories.Add(new Category { Id = categoryId, Name = $"Balance category {categoryId:N}", NameKey = categoryId.ToString("N") });
                database.CatalogItems.AddRange(
                    new CatalogItem { Id = userItemId, Code = $"BLA-{userItemId:N}"[..12], Name = "Scoped balance item", CategoryId = categoryId },
                    new CatalogItem { Id = otherItemId, Code = $"BLB-{otherItemId:N}"[..12], Name = "Other balance item", CategoryId = categoryId });
                database.EventAllocations.AddRange(
                    new EventAllocation { EventId = eventId, IndustryId = userIndustryId, ItemId = userItemId, QuantityAllocated = 5, QuantityWithdrawn = 2 },
                    new EventAllocation { EventId = eventId, IndustryId = otherIndustryId, ItemId = otherItemId, QuantityAllocated = 8, QuantityWithdrawn = 1 });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var response = await SendAsync(client, HttpMethod.Get,
                $"/api/v1/events/{eventId}/balance?industry_id={otherIndustryId}", token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var lines = json.RootElement.EnumerateArray().ToArray();
            var line = Assert.Single(lines);
            Assert.Equal(userItemId, line.GetProperty("item").GetProperty("id").GetGuid());
            Assert.Equal(5, line.GetProperty("qty_allocated").GetInt32());
            Assert.Equal(2, line.GetProperty("qty_withdrawn").GetInt32());
            Assert.Equal(3, line.GetProperty("saldo").GetInt32());

            using var legacyResponse = await SendAsync(client, HttpMethod.Get,
                $"/api/events/{eventId}/balance?industry_id={otherIndustryId}", token);
            Assert.Equal(HttpStatusCode.Forbidden, legacyResponse.StatusCode);
        }
        finally
        {
            await CleanupAsync(eventId, userId, roleName, userIndustryId, categoryId, userItemId, otherItemId, otherIndustryId);
        }
    }

    [Fact]
    public async Task Legacy_event_balance_requires_an_industry_and_returns_the_php_envelope()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"legacy-event-balance-viewer-{Guid.NewGuid():N}";
        Guid? userId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.view"))).Succeeded);
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = "Legacy event balance viewer"
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
            }

            var token = await LoginAsync(client, email);
            using var missingIndustry = await SendAsync(client, HttpMethod.Get,
                $"/api/events/{Guid.NewGuid()}/balance", token);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, missingIndustry.StatusCode);
            using var errorJson = JsonDocument.Parse(await missingIndustry.Content.ReadAsStringAsync());
            Assert.False(errorJson.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("VALIDATION_ERROR", errorJson.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal("Informe a indústria.", errorJson.RootElement.GetProperty("error").GetProperty("fields").GetProperty("industry_id").GetString());

            using var invalidIndustry = await SendAsync(client, HttpMethod.Get,
                $"/api/events/{Guid.NewGuid()}/balance?industry_id=not-a-guid", token);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidIndustry.StatusCode);

            using var unknownIndustry = await SendAsync(client, HttpMethod.Get,
                $"/api/events/{Guid.NewGuid()}/balance?industry_id={Guid.NewGuid()}", token);
            Assert.Equal(HttpStatusCode.OK, unknownIndustry.StatusCode);
            using var successJson = JsonDocument.Parse(await unknownIndustry.Content.ReadAsStringAsync());
            Assert.True(successJson.RootElement.GetProperty("ok").GetBoolean());
            Assert.Empty(successJson.RootElement.GetProperty("data").EnumerateArray());
        }
        finally
        {
            await CleanupAsync(null, userId, roleName, null, null, null);
        }
    }

    [Fact]
    public async Task Event_viewer_cannot_read_soft_deleted_event()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"event-deleted-viewer-{Guid.NewGuid():N}";
        var eventId = Guid.NewGuid();
        Guid? userId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.view"))).Succeeded);
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = "Event deleted viewer"
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Events.Add(new Event
                {
                    Id = eventId,
                    Name = $"Deleted event {eventId:N}",
                    DeletedAt = DateTimeOffset.UtcNow
                });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var response = await SendAsync(client, HttpMethod.Get, $"/api/v1/events/{eventId}", token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await CleanupAsync(eventId, userId, roleName, null, null, null);
        }
    }

    [Fact]
    public async Task Admin_without_events_view_claim_can_list_events()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var eventName = $"Admin listed event {Guid.NewGuid():N}";
        Guid? userId = null;
        Guid? eventId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = await roles.FindByNameAsync("admin");
                if (role is null)
                {
                    role = new ApplicationRole("admin");
                    Assert.True((await roles.CreateAsync(role)).Succeeded);
                }
                Assert.DoesNotContain(await roles.GetClaimsAsync(role), claim => claim.Type == "permission" && claim.Value == "events.view");
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = "Event admin viewer"
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, role.Name!)).Succeeded);
                userId = user.Id;
            }

            var token = await LoginAsync(client, email);
            using (var created = await SendAsync(client, HttpMethod.Post, "/api/v1/events", token, new { name = eventName }))
            {
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
                eventId = createdJson.RootElement.GetProperty("id").GetGuid();
            }

            using var listed = await SendAsync(client, HttpMethod.Get, "/api/v1/events", token);
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            using var json = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
            Assert.Contains(json.RootElement.GetProperty("data").EnumerateArray(), item => item.GetProperty("id").GetGuid() == eventId);
        }
        finally
        {
            await CleanupAsync(eventId, userId, null, null, null, null);
        }
    }

    [Fact]
    public async Task Event_manager_without_events_view_is_forbidden_from_listing()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"event-manager-no-view-{Guid.NewGuid():N}";
        Guid? userId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.manage"))).Succeeded);
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = "Event manager without view"
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
            }

            var token = await LoginAsync(client, email);
            using var listed = await SendAsync(client, HttpMethod.Get, "/api/v1/events", token);
            Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);
        }
        finally
        {
            await CleanupAsync(null, userId, roleName, null, null, null);
        }
    }

    private async Task CleanupAsync(Guid? eventId, Guid? userId, string? roleName, Guid? industryId, Guid? categoryId, Guid? itemId, Guid? secondItemId = null, Guid? secondIndustryId = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var database = services.GetRequiredService<ApplicationDbContext>();
        if (eventId is Guid id)
        {
            database.AuditLogs.RemoveRange(await database.AuditLogs
                .Where(log => log.EntityType == "event" && log.EntityId == id).ToListAsync());
            var eventRecord = await database.Events.FindAsync(id);
            if (eventRecord is not null)
                database.Events.Remove(eventRecord);
            await database.SaveChangesAsync();
        }
        if (itemId is Guid catalogItemId)
        {
            var item = await database.CatalogItems.FindAsync(catalogItemId);
            if (item is not null)
                database.CatalogItems.Remove(item);
        }
        if (secondItemId is Guid secondCatalogItemId)
        {
            var item = await database.CatalogItems.FindAsync(secondCatalogItemId);
            if (item is not null)
                database.CatalogItems.Remove(item);
        }
        if (categoryId is Guid catalogCategoryId)
        {
            var category = await database.Categories.FindAsync(catalogCategoryId);
            if (category is not null)
                database.Categories.Remove(category);
        }
        if (industryId is Guid eventIndustryId)
        {
            var industry = await database.Industries.FindAsync(eventIndustryId);
            if (industry is not null)
                database.Industries.Remove(industry);
        }
        if (secondIndustryId is Guid otherIndustry)
        {
            var industry = await database.Industries.FindAsync(otherIndustry);
            if (industry is not null)
                database.Industries.Remove(industry);
        }
        await database.SaveChangesAsync();
        if (userId is Guid idUser)
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(idUser.ToString());
            if (user is not null)
                Assert.True((await users.DeleteAsync(user)).Succeeded);
        }
        if (roleName is not null)
        {
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync(roleName);
            if (role is not null)
                Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
