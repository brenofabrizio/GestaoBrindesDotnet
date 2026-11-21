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

public sealed class EventEndpointsTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task Event_manager_can_create_a_planned_event_with_the_laravel_api_contract()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"event-manager-{Guid.NewGuid():N}";
        Guid? userId = null;
        Guid? eventId = null;

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
                    FullName = "Event manager"
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
            }

            var token = await LoginAsync(client, email);
            using var response = await SendAsync(client, token, new
            {
                name = "Feirão regional",
                description = "Evento anual de brindes.",
                venue = "Centro de convenções",
                starts_on = new DateOnly(2026, 12, 5),
                ends_on = new DateOnly(2026, 12, 6),
                notes = "Montagem às 8h."
            });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = json.RootElement;
            eventId = data.GetProperty("id").GetGuid();
            Assert.Equal("Feirão regional", data.GetProperty("name").GetString());
            Assert.Equal("Evento anual de brindes.", data.GetProperty("description").GetString());
            Assert.Equal("Centro de convenções", data.GetProperty("venue").GetString());
            Assert.Equal("2026-12-05", data.GetProperty("starts_on").GetString());
            Assert.Equal("2026-12-06", data.GetProperty("ends_on").GetString());
            Assert.Equal("planejado", data.GetProperty("status").GetString());

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var database = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var persistedEvent = await database.Events.SingleAsync(value => value.Id == eventId);
            Assert.Equal(userId, persistedEvent.CreatedBy);
            Assert.Equal(userId, persistedEvent.UpdatedBy);
            var audit = await database.AuditLogs.SingleAsync(log => log.EntityType == "event" && log.EntityId == eventId);
            Assert.Equal("create", audit.Action);
            Assert.Equal(userId, audit.UserId);
        }
        finally
        {
            await CleanupAsync(eventId, userId, roleName);
        }
    }

    [Fact]
    public async Task Legacy_event_creation_uses_php_route_envelope_and_description_limit()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"legacy-event-manager-{Guid.NewGuid():N}";
        Guid? userId = null;
        Guid? eventId = null;

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
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Legacy event manager" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
            }

            var token = await LoginAsync(client, email);
            using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/events")
            {
                Content = JsonContent.Create(new { name = "Legacy event creation", description = new string('d', 2000) })
            })
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var root = json.RootElement;
                Assert.True(root.GetProperty("ok").GetBoolean());
                var data = root.GetProperty("data");
                eventId = data.GetProperty("id").GetGuid();
                Assert.Equal("Legacy event creation", data.GetProperty("name").GetString());
                Assert.Equal(2000, data.GetProperty("description").GetString()!.Length);
                Assert.Equal("planejado", data.GetProperty("status").GetString());
                Assert.Equal("Planejado", data.GetProperty("status_label").GetString());
                Assert.Empty(data.GetProperty("allocations").EnumerateArray());
                Assert.Empty(data.GetProperty("industries").EnumerateArray());
                Assert.Empty(data.GetProperty("returns").EnumerateArray());
            }

            using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/events")
            {
                Content = JsonContent.Create(new { name = "Oversized legacy event", description = new string('d', 2001) })
            })
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            }

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var database = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(1, await database.Events.CountAsync(record => record.Id == eventId));
            Assert.Equal(1, await database.AuditLogs.CountAsync(log => log.EntityType == "event" && log.EntityId == eventId));
            var persistedEvent = await database.Events.SingleAsync(record => record.Id == eventId);
            Assert.Equal(userId, persistedEvent.CreatedBy);
            Assert.Equal(userId, persistedEvent.UpdatedBy);
        }
        finally
        {
            await CleanupAsync(eventId, userId, roleName);
        }
    }

    [Fact]
    public async Task Event_manager_can_partially_update_a_non_closed_event_using_the_php_contract()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"event-editor-{Guid.NewGuid():N}";
        Guid? userId = null;
        Guid? eventId = null;

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
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Event editor" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
            }

            var token = await LoginAsync(client, email);
            using (var create = await SendAsync(client, token, new { name = "Before update", venue = "Local preservado", description = "Texto antigo" }))
            {
                Assert.Equal(HttpStatusCode.Created, create.StatusCode);
                using var json = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
                eventId = json.RootElement.GetProperty("id").GetGuid();
            }

            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/events/{eventId}")
            {
                Content = JsonContent.Create(new { name = "Depois da edição", description = (string?)null })
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var updated = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            using var updatedJson = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
            Assert.Equal("Depois da edição", updatedJson.RootElement.GetProperty("name").GetString());
            Assert.Equal("Local preservado", updatedJson.RootElement.GetProperty("venue").GetString());
            Assert.Equal(JsonValueKind.Null, updatedJson.RootElement.GetProperty("description").ValueKind);

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var database = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var audit = await database.AuditLogs.SingleAsync(log => log.EntityType == "event" && log.EntityId == eventId && log.Action == "update");
            Assert.Equal(userId, audit.UserId);
            using var before = JsonDocument.Parse(audit.BeforeJson!);
            using var after = JsonDocument.Parse(audit.AfterJson!);
            Assert.Equal("Before update", before.RootElement.GetProperty("name").GetString());
            Assert.Equal("Depois da edição", after.RootElement.GetProperty("name").GetString());
            Assert.False(before.RootElement.TryGetProperty("venue", out _));
            Assert.False(after.RootElement.TryGetProperty("venue", out _));

            var persistedEvent = await database.Events.SingleAsync(value => value.Id == eventId);
            Assert.Equal(userId, persistedEvent.UpdatedBy);
            persistedEvent.Status = "encerrado";
            await database.SaveChangesAsync();
            using var closedRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/events/{eventId}")
            {
                Content = JsonContent.Create(new { name = "Edição rejeitada" })
            };
            closedRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var closedUpdate = await client.SendAsync(closedRequest);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, closedUpdate.StatusCode);
            Assert.Equal("Depois da edição", (await database.Events.SingleAsync(value => value.Id == eventId)).Name);
            Assert.Equal(1, await database.AuditLogs.CountAsync(log => log.EntityType == "event" && log.EntityId == eventId && log.Action == "update"));
        }
        finally
        {
            await CleanupAsync(eventId, userId, roleName);
        }
    }

    [Fact]
    public async Task Non_admin_without_events_manage_cannot_create_an_event()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"event-outsider-{Guid.NewGuid():N}";
        var eventName = $"Forbidden event {Guid.NewGuid():N}";
        Guid? userId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                Assert.True((await roles.CreateAsync(new ApplicationRole(roleName))).Succeeded);
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = "Event outsider"
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
            }

            var token = await LoginAsync(client, email);
            using var response = await SendAsync(client, token, new { name = eventName });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var database = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False(await database.Events.AnyAsync(eventRecord => eventRecord.Name == eventName));
            Assert.False(await database.AuditLogs.AnyAsync(log => log.EntityType == "event" && log.EntityLabel == eventName));
        }
        finally
        {
            await CleanupAsync(null, userId, roleName);
        }
    }

    [Fact]
    public async Task Admin_can_create_an_event_without_an_explicit_events_manage_claim()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        Guid? userId = null;
        Guid? eventId = null;
        var eventName = $"Admin event {Guid.NewGuid():N}";

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
                Assert.DoesNotContain(await roles.GetClaimsAsync(role), claim => claim.Type == "permission" && claim.Value == "events.manage");
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = "Event admin"
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, role.Name!)).Succeeded);
                userId = user.Id;
            }

            var token = await LoginAsync(client, email);
            using var response = await SendAsync(client, token, new
            {
                name = eventName,
                starts_on = new DateOnly(2026, 12, 7)
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            eventId = json.RootElement.GetProperty("id").GetGuid();
        }
        finally
        {
            await CleanupAsync(eventId, userId, null);
        }
    }

    private async Task CleanupAsync(Guid? eventId, Guid? userId, string? roleName)
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
        if (userId is Guid userIdValue)
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(userIdValue.ToString());
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

    [Fact]
    public async Task Authenticated_user_without_either_return_permission_is_forbidden_without_side_effects()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"event-return-denied-{Guid.NewGuid():N}";
        var eventId = Guid.NewGuid();
        Guid? userId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.True((await roles.CreateAsync(new ApplicationRole(roleName))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Denied return user" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Events.Add(new Event { Id = eventId, Name = "Protected return event", Status = "aberto" });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var response = await SendReturnAsync(client, token, eventId,
                new { items = new[] { new { industry_id = Guid.NewGuid(), item_id = Guid.NewGuid(), qty = 1 } } });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal("aberto", (await verify.Events.SingleAsync(value => value.Id == eventId)).Status);
            Assert.Empty(await verify.AuditLogs.Where(log => log.EntityType == "event" && log.EntityId == eventId && log.Action == "return").ToListAsync());
        }
        finally
        {
            await CleanupAsync(eventId, userId, roleName);
        }
    }

    [Fact]
    public async Task Event_manager_can_return_stock_to_cd_without_stock_transfer_claim()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"event-return-{Guid.NewGuid():N}";
        var eventId = Guid.NewGuid();
        var industryId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var eventLocationId = Guid.NewGuid();
        var cdLocationId = Guid.NewGuid();
        Guid? userId = null;
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var database = services.GetRequiredService<ApplicationDbContext>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.manage"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Return operator" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Events.Add(new Event { Id = eventId, Name = "Return event", Status = "aberto", LocationId = eventLocationId });
                database.Industries.Add(new Industry { Id = industryId, Name = "Return industry", NameKey = industryId.ToString("N") });
                database.Categories.Add(new Category { Id = categoryId, Name = "Return category", NameKey = categoryId.ToString("N") });
                database.Locations.AddRange(
                    new Location { Id = eventLocationId, Name = "Event site", NameKey = eventLocationId.ToString("N"), Kind = "evento" },
                    new Location { Id = cdLocationId, Name = "CD", NameKey = cdLocationId.ToString("N"), Kind = "cd" });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"RET-{itemId:N}"[..12], Name = "Return item", CategoryId = categoryId });
                database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 10, Reserved = 4 });
                database.StockPositions.AddRange(
                    new StockPosition { ItemId = itemId, LocationId = eventLocationId, Quantity = 5 },
                    new StockPosition { ItemId = itemId, LocationId = cdLocationId, Quantity = 0 });
                database.EventAllocations.Add(new EventAllocation { EventId = eventId, IndustryId = industryId, ItemId = itemId, QuantityAllocated = 4 });
                await database.SaveChangesAsync();
            }
            var token = await LoginAsync(client, email);
            using var response = await SendReturnAsync(client, token, eventId, new { items = new[] { new { industry_id = industryId, item_id = itemId, qty = 2 } } });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stock = await verify.Stocks.SingleAsync(value => value.ItemId == itemId);
            Assert.Equal(10, stock.OnHand);
            Assert.Equal(2, stock.Reserved);
            var allocation = await verify.EventAllocations.SingleAsync(value => value.EventId == eventId);
            Assert.Equal(2, allocation.QuantityAllocated);
            Assert.Equal(3, (await verify.StockPositions.SingleAsync(value => value.ItemId == itemId && value.LocationId == eventLocationId)).Quantity);
            Assert.Equal(2, (await verify.StockPositions.SingleAsync(value => value.ItemId == itemId && value.LocationId == cdLocationId)).Quantity);
            Assert.Equal(2, await verify.StockMovements.CountAsync(value => value.TransferId != null));
            using var replay = await SendReturnAsync(client, token, eventId, new { items = new[] { new { industry_id = industryId, item_id = itemId, qty = 2 } } });
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            using var conflict = await SendReturnAsync(client, token, eventId, new { items = new[] { new { industry_id = industryId, item_id = itemId, qty = 1 } } });
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal(2, (await verify.Stocks.SingleAsync(value => value.ItemId == itemId)).Reserved);
            Assert.Equal(2, (await verify.EventAllocations.SingleAsync(value => value.EventId == eventId)).QuantityAllocated);
            Assert.Equal(1, await verify.AuditLogs.CountAsync(log => log.EntityType == "event" && log.EntityId == eventId && log.Action == "return"));
            Assert.Equal(2, await verify.StockMovements.CountAsync(value => value.TransferId != null));
        }
        finally { await CleanupAsync(eventId, userId, roleName); }
    }

    [Fact]
    public async Task Industry_scoped_event_manager_cannot_return_another_industry_allocation()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"event-return-scope-{Guid.NewGuid():N}";
        var eventId = Guid.NewGuid();
        var assignedIndustryId = Guid.NewGuid();
        var otherIndustryId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        Guid? userId = null;
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var database = services.GetRequiredService<ApplicationDbContext>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.manage"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Scoped return user", IndustryId = assignedIndustryId };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Events.Add(new Event { Id = eventId, Name = "Scoped return event", Status = "aberto" });
                database.Industries.AddRange(
                    new Industry { Id = assignedIndustryId, Name = "Assigned industry", NameKey = assignedIndustryId.ToString("N") },
                    new Industry { Id = otherIndustryId, Name = "Other industry", NameKey = otherIndustryId.ToString("N") });
                database.Categories.Add(new Category { Id = categoryId, Name = "Scoped return category", NameKey = categoryId.ToString("N") });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"SRT-{itemId:N}"[..12], Name = "Scoped return item", CategoryId = categoryId });
                database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 10, Reserved = 2 });
                database.EventAllocations.Add(new EventAllocation { EventId = eventId, IndustryId = otherIndustryId, ItemId = itemId, QuantityAllocated = 2 });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var response = await SendReturnAsync(client, token, eventId,
                new { items = new[] { new { industry_id = otherIndustryId, item_id = itemId, qty = 1 } } }, "return-scope-001");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally { await CleanupAsync(eventId, userId, roleName); }
    }

    [Fact]
    public async Task Industry_scoped_event_detail_hides_other_industry_allocations()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"event-view-scope-{Guid.NewGuid():N}";
        var eventId = Guid.NewGuid();
        var assignedIndustryId = Guid.NewGuid();
        var otherIndustryId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        Guid? userId = null;
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var database = services.GetRequiredService<ApplicationDbContext>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.view"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Scoped event viewer", IndustryId = assignedIndustryId };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Events.Add(new Event { Id = eventId, Name = "Scoped detail event", Status = "aberto" });
                database.Industries.AddRange(
                    new Industry { Id = assignedIndustryId, Name = "Visible event industry", NameKey = assignedIndustryId.ToString("N") },
                    new Industry { Id = otherIndustryId, Name = "Hidden event industry", NameKey = otherIndustryId.ToString("N") });
                database.Categories.Add(new Category { Id = categoryId, Name = "Event detail category", NameKey = categoryId.ToString("N") });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"EDT-{itemId:N}"[..12], Name = "Event detail item", CategoryId = categoryId });
                database.EventAllocations.AddRange(
                    new EventAllocation { EventId = eventId, IndustryId = assignedIndustryId, ItemId = itemId, QuantityAllocated = 1 },
                    new EventAllocation { EventId = eventId, IndustryId = otherIndustryId, ItemId = itemId, QuantityAllocated = 2 });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/events/{eventId}");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Hidden event industry", body, StringComparison.Ordinal);
            Assert.DoesNotContain(otherIndustryId.ToString(), body, StringComparison.OrdinalIgnoreCase);

            using var legacyRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/events/{eventId}");
            legacyRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var legacyResponse = await client.SendAsync(legacyRequest);
            Assert.Equal(HttpStatusCode.OK, legacyResponse.StatusCode);
            var legacyBody = await legacyResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Hidden event industry", legacyBody, StringComparison.Ordinal);

            using var balanceRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/events/{eventId}/balance?industry_id={otherIndustryId}");
            balanceRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var balanceResponse = await client.SendAsync(balanceRequest);
            Assert.Equal(HttpStatusCode.Forbidden, balanceResponse.StatusCode);
        }
        finally { await CleanupAsync(eventId, userId, roleName); }
    }

    private static async Task<HttpResponseMessage> SendReturnAsync(HttpClient client, string token, Guid eventId, object body, string key = "return-to-cd-0001")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/events/{eventId}/returns") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Event_withdrawal_route_is_registered_and_requires_authentication()
    {
        using var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/v1/events/00000000-0000-0000-0000-000000000001/withdrawals", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/events")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendWithdrawalAsync(HttpClient client, string token, Guid eventId,
        string key, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/events/{eventId}/withdrawals")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Event_industry_can_withdraw_with_quota_stock_signature_and_idempotent_replay()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"event-withdrawer-{Guid.NewGuid():N}";
        var eventId = Guid.NewGuid();
        var industryId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        Guid? userId = null;
        string? signaturePath = null;
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
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.withdraw"))).Succeeded);
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = "Event withdrawer",
                    IndustryId = industryId
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Events.Add(new Event { Id = eventId, Name = "Withdrawal event", Status = "aberto" });
                database.Industries.Add(new Industry { Id = industryId, Name = "Withdrawal industry", NameKey = industryId.ToString("N") });
                database.Categories.Add(new Category { Id = categoryId, Name = "Withdrawal category", NameKey = categoryId.ToString("N") });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"WDR-{itemId:N}"[..12], Name = "Withdrawal item", CategoryId = categoryId });
                database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 10, Reserved = 4 });
                database.EventAllocations.Add(new EventAllocation { EventId = eventId, IndustryId = industryId, ItemId = itemId, QuantityAllocated = 4 });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            var body = new
            {
                industry_id = industryId,
                received_by_name = "Recipient",
                signature = TestSignatureDataUri,
                items = new[] { new { item_id = itemId, qty = 2 } }
            };
            using var invalidKey = await SendWithdrawalAsync(client, token, eventId, "short", body);
            Assert.Equal(HttpStatusCode.BadRequest, invalidKey.StatusCode);
            using var first = await SendWithdrawalAsync(client, token, eventId, "event-withdrawal-001", body);
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            var deliveryId = firstJson.RootElement.GetProperty("data").GetProperty("id").GetGuid();
            using var replay = await SendWithdrawalAsync(client, token, eventId, "event-withdrawal-001", body);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            Assert.Equal(deliveryId, replayJson.RootElement.GetProperty("data").GetProperty("id").GetGuid());
            using var quotaExceeded = await SendWithdrawalAsync(client, token, eventId, "event-withdrawal-002", new
            {
                industry_id = industryId,
                received_by_name = "Recipient",
                signature = TestSignatureDataUri,
                items = new[] { new { item_id = itemId, qty = 3 } }
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, quotaExceeded.StatusCode);
            using var conflict = await SendWithdrawalAsync(client, token, eventId, "event-withdrawal-001", new
            {
                industry_id = industryId,
                received_by_name = "Recipient",
                signature = TestSignatureDataUri,
                items = new[] { new { item_id = itemId, qty = 3 } }
            });
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var delivery = await verify.Deliveries.Include(value => value.Items).SingleAsync(value => value.Id == deliveryId);
            signaturePath = delivery.SignaturePath;
            Assert.Equal(eventId, delivery.EventId);
            Assert.Null(delivery.RequestId);
            Assert.Equal(2, Assert.Single(delivery.Items).Quantity);
            Assert.Equal(2, (await verify.EventAllocations.SingleAsync(value => value.EventId == eventId)).QuantityWithdrawn);
            var stock = await verify.Stocks.SingleAsync(value => value.ItemId == itemId);
            Assert.Equal(8, stock.OnHand);
            Assert.Equal(2, stock.Reserved);
            var movement = await verify.StockMovements.SingleAsync(value => value.DeliveryId == deliveryId);
            Assert.Equal(-2, movement.Quantity);
            Assert.Equal(industryId, movement.IndustryId);
            Assert.Single(await verify.AuditLogs.Where(value => value.EntityType == "delivery" && value.EntityId == deliveryId && value.Action == "withdraw").ToListAsync());
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var deliveries = await database.Deliveries.Where(value => value.EventId == eventId).Select(value => value.Id).ToListAsync();
            database.AuditLogs.RemoveRange(database.AuditLogs.Where(value => value.EntityId != null && deliveries.Contains(value.EntityId.Value)));
            database.StockMovements.RemoveRange(database.StockMovements.Where(value => value.DeliveryId != null && deliveries.Contains(value.DeliveryId.Value)));
            database.Deliveries.RemoveRange(database.Deliveries.Where(value => value.EventId == eventId));
            database.EventAllocations.RemoveRange(database.EventAllocations.Where(value => value.EventId == eventId));
            database.Stocks.RemoveRange(database.Stocks.Where(value => value.ItemId == itemId));
            database.CatalogItems.RemoveRange(database.CatalogItems.Where(value => value.Id == itemId));
            database.Events.RemoveRange(database.Events.Where(value => value.Id == eventId));
            database.Categories.RemoveRange(database.Categories.Where(value => value.Id == categoryId));
            database.Industries.RemoveRange(database.Industries.Where(value => value.Id == industryId));
            await database.SaveChangesAsync();
            if (userId is Guid id)
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(id.ToString());
                if (user is not null) await users.DeleteAsync(user);
            }
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync(roleName);
            if (role is not null) await roles.DeleteAsync(role);
            if (signaturePath is not null) scope.ServiceProvider.GetRequiredService<Gbrindes.Web.Storage.DigitalSignatureStorage>().Delete(signaturePath);
        }
    }

    [Fact]
    public async Task Authenticated_user_without_events_withdraw_is_forbidden()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"event-withdraw-denied-{Guid.NewGuid():N}";
        Guid? userId = null;
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                Assert.True((await roles.CreateAsync(new ApplicationRole(roleName))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Denied event user" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
            }
            var token = await LoginAsync(client, email);
            using var response = await SendWithdrawalAsync(client, token, Guid.NewGuid(), "event-withdraw-denied-1", new { });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            if (userId is Guid id && await users.FindByIdAsync(id.ToString()) is { } user) await users.DeleteAsync(user);
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            if (await roles.FindByNameAsync(roleName) is { } role) await roles.DeleteAsync(role);
        }
    }

    private const string TestSignatureDataUri = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==";
}
