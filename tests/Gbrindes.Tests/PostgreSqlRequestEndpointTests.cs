using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Gbrindes.Web.Data;
using Gbrindes.Web.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Gbrindes.Web.Storage;
using SkiaSharp;

namespace Gbrindes.Tests;

[Collection("PostgreSQL integration")]
public sealed class PostgreSqlRequestEndpointTests(PostgreSqlWebApplicationFactory factory)
    : IClassFixture<PostgreSqlWebApplicationFactory>
{
    [PostgreSqlFact]
    public async Task Approval_rule_crud_and_audit_are_persisted_together_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var managerRoleName = $"postgres-rule-manager-{Guid.NewGuid():N}";
        var approverRoleName = $"postgres-rule-approver-{Guid.NewGuid():N}";
        Guid? managerId = null;
        Guid? ruleId = null;
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var managerRole = new ApplicationRole(managerRoleName);
                Assert.True((await roles.CreateAsync(managerRole)).Succeeded);
                Assert.True((await roles.AddClaimAsync(managerRole, new Claim("permission", "approval_rules.manage"))).Succeeded);
                Assert.True((await roles.CreateAsync(new ApplicationRole(approverRoleName))).Succeeded);
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var manager = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "PostgreSQL rule manager" };
                Assert.True((await users.CreateAsync(manager, "CorrectPassword1")).Succeeded);
                managerId = manager.Id;
                Assert.True((await users.AddToRoleAsync(manager, managerRoleName)).Succeeded);
            }

            var token = await LoginAsync(client, email);
            using (var created = await SendAsync(client, HttpMethod.Post, "/api/v1/approval-rules", token, new
            {
                name = "PostgreSQL rule",
                criterion = "quantidade",
                @operator = ">=",
                value = "7",
                approver_role_id = await GetRoleIdAsync(factory, approverRoleName),
                priority = 20,
                active = true
            }))
            {
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
                ruleId = json.RootElement.GetProperty("data").GetProperty("id").GetGuid();
            }

            using (var updated = await SendAsync(client, HttpMethod.Put, $"/api/v1/approval-rules/{ruleId}", token, new { name = "PostgreSQL updated rule" }))
                Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            using (var deleted = await SendAsync(client, HttpMethod.Delete, $"/api/v1/approval-rules/{ruleId}", token))
                Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

            await using (var verifyScope = factory.Services.CreateAsyncScope())
            {
                var database = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var logs = await database.AuditLogs.Where(log => log.EntityType == "approval_rule" && log.EntityId == ruleId)
                    .OrderBy(log => log.CreatedAt).ThenBy(log => log.Action).ToListAsync();
                Assert.Equal(3, logs.Count);
                Assert.Equal(new[] { "create", "delete", "update" }, logs.Select(log => log.Action).OrderBy(action => action).ToArray());
                Assert.All(logs, log => Assert.Equal(managerId, log.UserId));
                var update = logs.Single(log => log.Action == "update");
                Assert.Contains("PostgreSQL rule", update.BeforeJson);
                Assert.Contains("PostgreSQL updated rule", update.AfterJson);
            }
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            if (ruleId is Guid id)
            {
                database.AuditLogs.RemoveRange(await database.AuditLogs.Where(log => log.EntityType == "approval_rule" && log.EntityId == id).ToListAsync());
                var rule = await database.ApprovalRules.SingleOrDefaultAsync(value => value.Id == id);
                if (rule is not null) database.ApprovalRules.Remove(rule);
                await database.SaveChangesAsync();
            }
            if (managerId is Guid userId)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(userId.ToString());
                if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            if (await roles.FindByNameAsync(managerRoleName) is { } managerRole)
                Assert.True((await roles.DeleteAsync(managerRole)).Succeeded);
            if (await roles.FindByNameAsync(approverRoleName) is { } approverRole)
                Assert.True((await roles.DeleteAsync(approverRole)).Succeeded);
        }
    }

    [PostgreSqlFact]
    public async Task Audit_list_filters_and_json_values_execute_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-audit-viewer-{Guid.NewGuid():N}";
        var auditId = Guid.NewGuid();
        Guid? userId = null;
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "audit.view"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "PostgreSQL auditor" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                userId = user.Id;
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                database.AuditLogs.Add(new AuditLog
                {
                    Id = auditId,
                    UserId = user.Id,
                    UserName = user.FullName,
                    Action = "create",
                    EntityType = "event",
                    EntityLabel = "PostgreSQL Audit Probe",
                    AfterJson = "{\"status\":\"planejado\"}",
                    CreatedAt = new DateTimeOffset(2026, 1, 15, 23, 59, 59, TimeSpan.Zero).AddTicks(9999990)
                });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var response = await SendAsync(client, HttpMethod.Get,
                $"/api/v1/audit?entity_type=event&user_id={userId}&action=create&from=2026-01-15&to=2026-01-15&q=postgresql%20audit%20probe",
                token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var entry = Assert.Single(json.RootElement.GetProperty("data").EnumerateArray());
            Assert.Equal(auditId, entry.GetProperty("id").GetGuid());
            Assert.Equal("planejado", entry.GetProperty("after").GetProperty("status").GetString());
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            database.AuditLogs.RemoveRange(await database.AuditLogs.Where(log => log.Id == auditId).ToListAsync());
            await database.SaveChangesAsync();
            if (userId is Guid id)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(id.ToString());
                if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync(roleName);
            if (role is not null) Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }

    [PostgreSqlFact]
    public async Task Event_list_and_allocation_upsert_use_persistent_rows_and_laravel_pagination_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-event-viewer-{Guid.NewGuid():N}";
        var eventName = $"PostgreSQL listed event {Guid.NewGuid():N}";
        Guid? userId = null;
        Guid? eventId = null;
        Guid? allocationId = null;
        Guid? industryId = null;
        Guid? categoryId = null;
        Guid? itemId = null;
        Guid? eventLocationId = null;
        Guid? cdLocationId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.manage"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.view"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.withdraw"))).Succeeded);
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = "PostgreSQL event viewer"
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
            eventLocationId = Guid.NewGuid();
            cdLocationId = Guid.NewGuid();
            await using (var allocationScope = factory.Services.CreateAsyncScope())
            {
                var database = allocationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                database.Industries.Add(new Industry { Id = industryId.Value, Name = $"PostgreSQL event industry {industryId:N}", NameKey = industryId.Value.ToString("N") });
                database.Categories.Add(new Category { Id = categoryId.Value, Name = $"PostgreSQL event category {categoryId:N}", NameKey = categoryId.Value.ToString("N") });
                database.CatalogItems.Add(new CatalogItem { Id = itemId.Value, Code = $"PGE-{itemId.Value:N}"[..12], Name = "PostgreSQL event allocation item", CategoryId = categoryId.Value });
                database.Stocks.Add(new InventoryStock { ItemId = itemId.Value, OnHand = 10, Reserved = 2 });
                database.Locations.AddRange(
                    new Location { Id = eventLocationId.Value, Name = $"PostgreSQL event {eventLocationId:N}", NameKey = eventLocationId.Value.ToString("N"), Kind = "outro" },
                    new Location { Id = cdLocationId.Value, Name = $"PostgreSQL CD {cdLocationId:N}", NameKey = cdLocationId.Value.ToString("N"), Kind = "cd" });
                var eventRecord = await database.Events.SingleAsync(value => value.Id == eventId);
                eventRecord.LocationId = eventLocationId.Value;
                database.StockPositions.AddRange(
                    new StockPosition { ItemId = itemId.Value, LocationId = eventLocationId.Value, Quantity = 5 },
                    new StockPosition { ItemId = itemId.Value, LocationId = cdLocationId.Value, Quantity = 1 });
                var allocation = new EventAllocation
                {
                    EventId = eventId!.Value,
                    IndustryId = industryId.Value,
                    ItemId = itemId.Value,
                    QuantityAllocated = 4,
                    QuantityWithdrawn = 1
                };
                database.EventAllocations.Add(allocation);
                await database.SaveChangesAsync();
                allocationId = allocation.Id;
            }

            using (var updated = await SendAsync(client, HttpMethod.Post, $"/api/v1/events/{eventId}/allocations", token,
                new { industry_id = industryId, item_id = itemId, qty_allocated = 6 }))
            {
                Assert.Equal(HttpStatusCode.Created, updated.StatusCode);
                using var updatedJson = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
                Assert.Equal(allocationId, updatedJson.RootElement.GetProperty("id").GetGuid());
                Assert.Equal(6, updatedJson.RootElement.GetProperty("qty_allocated").GetInt32());
            }

            using (var replaced = await SendAsync(client, HttpMethod.Put, $"/api/v1/events/{eventId}/allocations", token,
                new { allocations = new[] { new { industry_id = industryId, item_id = itemId, qty_allocated = 6 } } }))
            {
                Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
                using var replacedJson = JsonDocument.Parse(await replaced.Content.ReadAsStringAsync());
                Assert.Equal(1, replacedJson.RootElement.GetProperty("allocations").GetArrayLength());
                Assert.Equal(0, replacedJson.RootElement.GetProperty("allocations")[0].GetProperty("qty_withdrawn").GetInt32());
            }

            using (var legacyReplaced = await SendAsync(client, HttpMethod.Put, $"/api/events/{eventId}/allocations", token,
                new[] { new { industry_id = industryId, item_id = itemId, qty_allocated = 6 } }))
            {
                Assert.Equal(HttpStatusCode.OK, legacyReplaced.StatusCode);
                using var legacyReplacedJson = JsonDocument.Parse(await legacyReplaced.Content.ReadAsStringAsync());
                Assert.True(legacyReplacedJson.RootElement.GetProperty("ok").GetBoolean());
                var allocation = Assert.Single(legacyReplacedJson.RootElement.GetProperty("data").GetProperty("allocations").EnumerateArray());
                Assert.Equal(itemId, allocation.GetProperty("item").GetProperty("id").GetGuid());
                Assert.Equal(6, allocation.GetProperty("qty_allocated").GetInt32());
            }

            using (var opened = await SendAsync(client, HttpMethod.Post, $"/api/v1/events/{eventId}/open", token, new { }))
            {
                Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
                using var openedJson = JsonDocument.Parse(await opened.Content.ReadAsStringAsync());
                Assert.Equal("aberto", openedJson.RootElement.GetProperty("status").GetString());
            }
            await using (var openScope = factory.Services.CreateAsyncScope())
            {
                var database = openScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var openedBy = await database.Database.SqlQuery<Guid?>($"SELECT \"OpenedBy\" AS \"Value\" FROM \"Events\" WHERE \"Id\" = {eventId}").SingleAsync();
                Assert.Equal(userId, openedBy);
                Assert.Equal(8, (await database.Stocks.SingleAsync(stock => stock.ItemId == itemId)).Reserved);
            }
            using (var withdrawalRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/events/{eventId}/withdrawals")
            {
                Content = JsonContent.Create(new
                {
                    industry_id = industryId,
                    received_by_name = "PostgreSQL recipient",
                    signature = CreateSignatureDataUri(),
                    items = new[] { new { item_id = itemId, qty = 1 } }
                })
            })
            {
                withdrawalRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                withdrawalRequest.Headers.Add("Idempotency-Key", $"pg-event-withdraw-{Guid.NewGuid():N}");
                using var withdrawn = await client.SendAsync(withdrawalRequest);
                Assert.Equal(HttpStatusCode.Created, withdrawn.StatusCode);
                using var withdrawnJson = JsonDocument.Parse(await withdrawn.Content.ReadAsStringAsync());
                Assert.StartsWith("PROT-", withdrawnJson.RootElement.GetProperty("data").GetProperty("code").GetString());
            }
            await using (var withdrawalVerifyScope = factory.Services.CreateAsyncScope())
            {
                var withdrawalVerify = withdrawalVerifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Equal(1, (await withdrawalVerify.EventAllocations.SingleAsync(value => value.EventId == eventId)).QuantityWithdrawn);
                Assert.Equal(9, (await withdrawalVerify.Stocks.SingleAsync(value => value.ItemId == itemId)).OnHand);
                Assert.Equal(7, (await withdrawalVerify.Stocks.SingleAsync(value => value.ItemId == itemId)).Reserved);
                var delivery = await withdrawalVerify.Deliveries.Include(value => value.Items).SingleAsync(value => value.EventId == eventId);
                Assert.Null(delivery.RequestId);
                Assert.Single(delivery.Items);
                Assert.Single(await withdrawalVerify.StockMovements.Where(value => value.DeliveryId == delivery.Id).ToListAsync());
                Assert.Single(await withdrawalVerify.AuditLogs.Where(value => value.EntityId == delivery.Id && value.EntityType == "delivery" && value.Action == "withdraw").ToListAsync());
            }

            await using (var withdrawalFailureScope = factory.Services.CreateAsyncScope())
            {
                var failureDatabase = withdrawalFailureScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await failureDatabase.Database.ExecuteSqlRawAsync("ALTER TABLE \"AuditLogs\" DROP CONSTRAINT IF EXISTS \"CK_TestRejectEventWithdrawal\"");
                await failureDatabase.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"AuditLogs\" ADD CONSTRAINT \"CK_TestRejectEventWithdrawal\" CHECK (\"Action\" <> 'withdraw') NOT VALID");
                try
                {
                    using var rejectedWithdrawal = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/events/{eventId}/withdrawals")
                    {
                        Content = JsonContent.Create(new
                        {
                            industry_id = industryId,
                            received_by_name = "PostgreSQL failed recipient",
                            signature = CreateSignatureDataUri(),
                            items = new[] { new { item_id = itemId, qty = 1 } }
                        })
                    };
                    rejectedWithdrawal.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    rejectedWithdrawal.Headers.Add("Idempotency-Key", $"pg-event-withdraw-fail-{Guid.NewGuid():N}");
                    using var failedWithdrawal = await client.SendAsync(rejectedWithdrawal);
                    Assert.Equal(HttpStatusCode.InternalServerError, failedWithdrawal.StatusCode);
                }
                finally
                {
                    await failureDatabase.Database.ExecuteSqlRawAsync("ALTER TABLE \"AuditLogs\" DROP CONSTRAINT IF EXISTS \"CK_TestRejectEventWithdrawal\"");
                }
            }
            await using (var withdrawalRollbackScope = factory.Services.CreateAsyncScope())
            {
                var eventWithdrawalRollbackDatabase = withdrawalRollbackScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("aberto", (await eventWithdrawalRollbackDatabase.Events.AsNoTracking().SingleAsync(value => value.Id == eventId)).Status);
                Assert.Equal(1, (await eventWithdrawalRollbackDatabase.EventAllocations.AsNoTracking().SingleAsync(value => value.EventId == eventId)).QuantityWithdrawn);
                Assert.Equal(9, (await eventWithdrawalRollbackDatabase.Stocks.AsNoTracking().SingleAsync(value => value.ItemId == itemId)).OnHand);
                Assert.Equal(7, (await eventWithdrawalRollbackDatabase.Stocks.AsNoTracking().SingleAsync(value => value.ItemId == itemId)).Reserved);
                Assert.Equal(4, await eventWithdrawalRollbackDatabase.StockPositions.AsNoTracking().Where(value => value.ItemId == itemId && value.LocationId == eventLocationId).Select(value => value.Quantity).SingleAsync());
                Assert.Equal(1, await eventWithdrawalRollbackDatabase.StockPositions.AsNoTracking().Where(value => value.ItemId == itemId && value.LocationId == cdLocationId).Select(value => value.Quantity).SingleAsync());
                var persistedDelivery = await eventWithdrawalRollbackDatabase.Deliveries.AsNoTracking().SingleAsync(value => value.EventId == eventId);
                Assert.Equal(1, await eventWithdrawalRollbackDatabase.DeliveryItems.CountAsync(value => value.DeliveryId == persistedDelivery.Id));
                Assert.Equal(1, await eventWithdrawalRollbackDatabase.StockMovements.CountAsync(value => value.DeliveryId == persistedDelivery.Id));
                Assert.Equal(1, await eventWithdrawalRollbackDatabase.AuditLogs.CountAsync(value => value.EntityId == persistedDelivery.Id && value.EntityType == "delivery" && value.Action == "withdraw"));
            }

            using (var closed = await SendAsync(client, HttpMethod.Post, $"/api/v1/events/{eventId}/close", token, new { }))
            {
                Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
                using var closedJson = JsonDocument.Parse(await closed.Content.ReadAsStringAsync());
                Assert.Equal("encerrado", closedJson.RootElement.GetProperty("status").GetString());
            }
            await using (var closeScope = factory.Services.CreateAsyncScope())
            {
                var database = closeScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var closedBy = await database.Database.SqlQuery<Guid?>($"SELECT \"ClosedBy\" AS \"Value\" FROM \"Events\" WHERE \"Id\" = {eventId}").SingleAsync();
                Assert.Equal(userId, closedBy);
            }

            using (var balance = await SendAsync(client, HttpMethod.Get,
                $"/api/v1/events/{eventId}/balance?industry_id={industryId}", token))
            {
                Assert.Equal(HttpStatusCode.OK, balance.StatusCode);
                using var balanceJson = JsonDocument.Parse(await balance.Content.ReadAsStringAsync());
                var balanceRow = Assert.Single(balanceJson.RootElement.EnumerateArray());
                Assert.Equal(6, balanceRow.GetProperty("qty_allocated").GetInt32());
                Assert.Equal(1, balanceRow.GetProperty("qty_withdrawn").GetInt32());
                Assert.Equal(5, balanceRow.GetProperty("saldo").GetInt32());
            }

            using (var legacyBalance = await SendAsync(client, HttpMethod.Get,
                $"/api/events/{eventId}/balance?industry_id={industryId}", token))
            {
                Assert.Equal(HttpStatusCode.OK, legacyBalance.StatusCode);
                using var legacyBalanceJson = JsonDocument.Parse(await legacyBalance.Content.ReadAsStringAsync());
                Assert.True(legacyBalanceJson.RootElement.GetProperty("ok").GetBoolean());
                var legacyBalanceRow = Assert.Single(legacyBalanceJson.RootElement.GetProperty("data").EnumerateArray());
                Assert.Equal(itemId, legacyBalanceRow.GetProperty("item").GetProperty("id").GetGuid());
                Assert.Equal(6, legacyBalanceRow.GetProperty("qty_allocated").GetInt32());
                Assert.Equal(1, legacyBalanceRow.GetProperty("qty_withdrawn").GetInt32());
                Assert.Equal(5, legacyBalanceRow.GetProperty("saldo").GetInt32());
            }

            using var listed = await SendAsync(client, HttpMethod.Get, "/api/v1/events?page=1", token);
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            using var json = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
            var page = json.RootElement;
            Assert.Equal(25, page.GetProperty("per_page").GetInt32());
            Assert.Contains(page.GetProperty("data").EnumerateArray(), value =>
                value.GetProperty("id").GetGuid() == eventId && value.GetProperty("allocations_count").GetInt32() == 1);

            using var legacyList = await SendAsync(client, HttpMethod.Get,
                $"/api/events?q={Uri.EscapeDataString(eventName.ToUpperInvariant())}&status=encerrado&per_page=1", token);
            Assert.Equal(HttpStatusCode.OK, legacyList.StatusCode);
            using var legacyJson = JsonDocument.Parse(await legacyList.Content.ReadAsStringAsync());
            Assert.True(legacyJson.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(1, legacyJson.RootElement.GetProperty("meta").GetProperty("total").GetInt32());
            var legacyRow = Assert.Single(legacyJson.RootElement.GetProperty("data").EnumerateArray());
            Assert.Equal(eventId, legacyRow.GetProperty("id").GetGuid());
            Assert.Equal("Encerrado", legacyRow.GetProperty("status_label").GetString());

            var returnKey = $"pg-event-return-{Guid.NewGuid():N}";
            using (var returnRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/events/{eventId}/returns")
            {
                Content = JsonContent.Create(new
                {
                    items = new[] { new { industry_id = industryId, item_id = itemId, qty = 2 } }
                })
            })
            {
                returnRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                returnRequest.Headers.Add("Idempotency-Key", returnKey);
                using var returned = await client.SendAsync(returnRequest);
                Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
            }
            using (var replayRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/events/{eventId}/returns")
            {
                Content = JsonContent.Create(new
                {
                    items = new[] { new { industry_id = industryId, item_id = itemId, qty = 2 } }
                })
            })
            {
                replayRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                replayRequest.Headers.Add("Idempotency-Key", returnKey);
                using var replayed = await client.SendAsync(replayRequest);
                Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
            }

            using (var detail = await SendAsync(client, HttpMethod.Get, $"/api/v1/events/{eventId}", token))
            {
                Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
                using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
                var industries = detailJson.RootElement.GetProperty("industries");
                var industry = Assert.Single(industries.EnumerateArray());
                Assert.Equal(industryId, industry.GetProperty("id").GetGuid());
                Assert.Equal($"PostgreSQL event industry {industryId:N}", industry.GetProperty("name").GetString());
                Assert.Equal(4, industry.GetProperty("allocated").GetInt32());
                Assert.Equal(1, industry.GetProperty("withdrawn").GetInt32());
                Assert.Equal(3, industry.GetProperty("saldo").GetInt32());

                var returns = detailJson.RootElement.GetProperty("returns");
                var returnLine = Assert.Single(returns.EnumerateArray());
                Assert.Equal(industryId, returnLine.GetProperty("industry").GetProperty("id").GetGuid());
                Assert.Equal(itemId, returnLine.GetProperty("item").GetProperty("id").GetGuid());
                Assert.Equal($"PGE-{itemId.Value:N}"[..12], returnLine.GetProperty("item").GetProperty("code").GetString());
                Assert.Equal(4, returnLine.GetProperty("qty_allocated").GetInt32());
                Assert.Equal(1, returnLine.GetProperty("qty_withdrawn").GetInt32());
                Assert.Equal(3, returnLine.GetProperty("saldo").GetInt32());
            }

            using (var legacyDetail = await SendAsync(client, HttpMethod.Get, $"/api/events/{eventId}", token))
            {
                Assert.Equal(HttpStatusCode.OK, legacyDetail.StatusCode);
                using var legacyDetailJson = JsonDocument.Parse(await legacyDetail.Content.ReadAsStringAsync());
                var legacyRoot = legacyDetailJson.RootElement;
                Assert.True(legacyRoot.GetProperty("ok").GetBoolean());
                var legacyEvent = legacyRoot.GetProperty("data");
                Assert.Equal(eventId, legacyEvent.GetProperty("id").GetGuid());
                Assert.Equal("Encerrado", legacyEvent.GetProperty("status_label").GetString());
                var legacyAllocation = Assert.Single(legacyEvent.GetProperty("allocations").EnumerateArray());
                Assert.Equal(industryId, legacyAllocation.GetProperty("industry").GetProperty("id").GetGuid());
                Assert.Equal(itemId, legacyAllocation.GetProperty("item").GetProperty("id").GetGuid());
                Assert.Equal(4, legacyAllocation.GetProperty("qty_allocated").GetInt32());
                Assert.Single(legacyEvent.GetProperty("industries").EnumerateArray());
                Assert.Single(legacyEvent.GetProperty("returns").EnumerateArray());
            }

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(1, await verifyDatabase.EventAllocations.CountAsync(value => value.EventId == eventId));
            var persistedAllocation = await verifyDatabase.EventAllocations.SingleAsync(value => value.Id == allocationId);
            Assert.Equal(4, persistedAllocation.QuantityAllocated);
            Assert.Equal(1, persistedAllocation.QuantityWithdrawn);
            Assert.Equal(2, (await verifyDatabase.Stocks.SingleAsync(stock => stock.ItemId == itemId)).Reserved);
            var returnAudit = await verifyDatabase.AuditLogs.SingleAsync(value => value.EntityType == "event"
                && value.EntityId == eventId && value.Action == "return");
            using var returnAuditJson = JsonDocument.Parse(returnAudit.AfterJson!);
            Assert.False(string.IsNullOrWhiteSpace(returnAuditJson.RootElement.GetProperty("idempotency_key_hash").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(returnAuditJson.RootElement.GetProperty("request_hash").GetString()));
            Assert.Equal(2, await verifyDatabase.StockPositions.Where(value => value.ItemId == itemId && value.LocationId == eventLocationId).Select(value => value.Quantity).SingleAsync());
            Assert.Equal(3, await verifyDatabase.StockPositions.Where(value => value.ItemId == itemId && value.LocationId == cdLocationId).Select(value => value.Quantity).SingleAsync());
            var returnMovements = await verifyDatabase.StockMovements.Where(value => value.ItemId == itemId && value.TransferId != null).ToListAsync();
            Assert.Equal(2, returnMovements.Count);
            Assert.Single(returnMovements.Select(value => value.TransferId).Distinct());
            Assert.Equal(0, returnMovements.Sum(value => value.Quantity));
            Assert.All(returnMovements, value =>
            {
                Assert.Equal("transferencia", value.Type);
                Assert.Equal(eventLocationId, value.FromLocationId);
                Assert.Equal(cdLocationId, value.ToLocationId);
            });

            await verifyDatabase.Database.ExecuteSqlRawAsync("ALTER TABLE \"AuditLogs\" DROP CONSTRAINT IF EXISTS \"CK_TestRejectEventReturn\"");
            await verifyDatabase.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"AuditLogs\" ADD CONSTRAINT \"CK_TestRejectEventReturn\" CHECK (\"Action\" <> 'return') NOT VALID");
            try
            {
                using var rejectedReturn = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/events/{eventId}/returns")
                {
                    Content = JsonContent.Create(new
                    {
                        items = new[] { new { industry_id = industryId, item_id = itemId, qty = 1 } }
                    })
                };
                rejectedReturn.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                rejectedReturn.Headers.Add("Idempotency-Key", $"pg-event-return-fail-{Guid.NewGuid():N}");
                using var failedReturn = await client.SendAsync(rejectedReturn);
                Assert.Equal(HttpStatusCode.InternalServerError, failedReturn.StatusCode);
            }
            finally
            {
                await verifyDatabase.Database.ExecuteSqlRawAsync("ALTER TABLE \"AuditLogs\" DROP CONSTRAINT IF EXISTS \"CK_TestRejectEventReturn\"");
            }

            await using var rollbackScope = factory.Services.CreateAsyncScope();
            var rollbackDatabase = rollbackScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(4, (await rollbackDatabase.EventAllocations.AsNoTracking().SingleAsync(value => value.Id == allocationId)).QuantityAllocated);
            Assert.Equal(2, await rollbackDatabase.StockPositions.Where(value => value.ItemId == itemId && value.LocationId == eventLocationId).Select(value => value.Quantity).SingleAsync());
            Assert.Equal(3, await rollbackDatabase.StockPositions.Where(value => value.ItemId == itemId && value.LocationId == cdLocationId).Select(value => value.Quantity).SingleAsync());
            Assert.Equal(2, await rollbackDatabase.StockMovements.CountAsync(value => value.ItemId == itemId && value.TransferId != null));
            Assert.Equal(1, await rollbackDatabase.AuditLogs.CountAsync(value => value.EntityType == "event"
                && value.EntityId == eventId && value.Action == "return"));

            var concurrentKey = $"pg-event-return-race-{Guid.NewGuid():N}";
            var concurrentRequests = Enumerable.Range(0, 2).Select(_ =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/events/{eventId}/returns")
                {
                    Content = JsonContent.Create(new
                    {
                        items = new[] { new { industry_id = industryId, item_id = itemId, qty = 1 } }
                    })
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Add("Idempotency-Key", concurrentKey);
                return request;
            }).ToArray();
            using var eventBlockerScope = factory.Services.CreateAsyncScope();
            var eventBlocker = eventBlockerScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await using var eventBlockerTransaction = await eventBlocker.Database.BeginTransactionAsync();
            _ = await eventBlocker.Events.FromSqlInterpolated(
                $"SELECT * FROM \"Events\" WHERE \"Id\" = {eventId} FOR UPDATE").SingleAsync();
            try
            {
                var concurrentTasks = concurrentRequests.Select(client.SendAsync).ToArray();
                var blockedEventLocks = 0;
                await using (var observerScope = factory.Services.CreateAsyncScope())
                {
                    var observer = observerScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    await observer.Database.OpenConnectionAsync();
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (DateTime.UtcNow < deadline && blockedEventLocks < 2
                        && concurrentTasks.Any(task => !task.IsCompleted))
                    {
                        await using var command = observer.Database.GetDbConnection().CreateCommand();
                        command.CommandText = "SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query ILIKE '%\\\"Events\\\"%' AND query ILIKE '%FOR UPDATE%'";
                        blockedEventLocks = Convert.ToInt32(await command.ExecuteScalarAsync());
                        if (blockedEventLocks < 2)
                            await Task.Delay(50);
                    }
                }
                await eventBlockerTransaction.CommitAsync();
                var concurrentResponses = await Task.WhenAll(concurrentTasks);
                Assert.Equal(2, blockedEventLocks);
                Assert.All(concurrentResponses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
                foreach (var response in concurrentResponses)
                    response.Dispose();
            }
            finally
            {
                foreach (var request in concurrentRequests)
                    request.Dispose();
            }

            await using var concurrentScope = factory.Services.CreateAsyncScope();
            var concurrentDatabase = concurrentScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(3, (await concurrentDatabase.EventAllocations.AsNoTracking().SingleAsync(value => value.Id == allocationId)).QuantityAllocated);
            Assert.Equal(1, await concurrentDatabase.StockPositions.Where(value => value.ItemId == itemId && value.LocationId == eventLocationId).Select(value => value.Quantity).SingleAsync());
            Assert.Equal(4, await concurrentDatabase.StockPositions.Where(value => value.ItemId == itemId && value.LocationId == cdLocationId).Select(value => value.Quantity).SingleAsync());
            Assert.Equal(4, await concurrentDatabase.StockMovements.CountAsync(value => value.ItemId == itemId && value.TransferId != null));
            Assert.Equal(2, await concurrentDatabase.AuditLogs.CountAsync(value => value.EntityType == "event"
                && value.EntityId == eventId && value.Action == "return"));
        }
        finally
        {
            await using var cleanup = factory.Services.CreateAsyncScope();
            var services = cleanup.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            if (eventId is Guid id)
            {
                var deliveryIds = await database.Deliveries.Where(value => value.EventId == id).Select(value => value.Id).ToListAsync();
                database.AuditLogs.RemoveRange(database.AuditLogs.Where(log => log.EntityId != null && deliveryIds.Contains(log.EntityId.Value)));
                database.StockMovements.RemoveRange(database.StockMovements.Where(value => value.DeliveryId != null && deliveryIds.Contains(value.DeliveryId.Value)));
                database.Deliveries.RemoveRange(database.Deliveries.Where(value => value.EventId == id));
                await database.SaveChangesAsync();
                database.AuditLogs.RemoveRange(await database.AuditLogs
                    .Where(log => log.EntityType == "event" && log.EntityId == id).ToListAsync());
                var eventRecord = await database.Events.FindAsync(id);
                if (eventRecord is not null)
                    database.Events.Remove(eventRecord);
                await database.SaveChangesAsync();
            }
            if (userId is Guid idUser)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(idUser.ToString());
                if (user is not null)
                    Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync(roleName);
            if (role is not null)
                Assert.True((await roles.DeleteAsync(role)).Succeeded);
            if (itemId is Guid catalogItemId)
            {
                database.StockMovements.RemoveRange(await database.StockMovements.Where(value => value.ItemId == catalogItemId).ToListAsync());
                database.StockPositions.RemoveRange(await database.StockPositions.Where(value => value.ItemId == catalogItemId).ToListAsync());
                database.Stocks.RemoveRange(await database.Stocks.Where(value => value.ItemId == catalogItemId).ToListAsync());
                await database.SaveChangesAsync();
                var item = await database.CatalogItems.FindAsync(catalogItemId);
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
            foreach (var locationId in new[] { eventLocationId, cdLocationId })
            {
                if (locationId is Guid idLocation)
                {
                    var location = await database.Locations.FindAsync(idLocation);
                    if (location is not null)
                        database.Locations.Remove(location);
                }
            }
            await database.SaveChangesAsync();
        }
    }

    [PostgreSqlFact]
    public async Task Legacy_event_workflow_routes_persist_reservations_actors_and_audit_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-legacy-event-workflow-{Guid.NewGuid():N}";
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
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "events.manage"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "PostgreSQL legacy event manager" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;

                database.Events.Add(new Event { Id = eventId, Name = $"Legacy PostgreSQL event {eventId:N}" });
                database.Industries.Add(new Industry { Id = industryId, Name = $"Legacy PostgreSQL industry {industryId:N}", NameKey = industryId.ToString("N") });
                database.Categories.Add(new Category { Id = categoryId, Name = $"Legacy PostgreSQL category {categoryId:N}", NameKey = categoryId.ToString("N") });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"LGE-{itemId:N}"[..12], Name = "Legacy PostgreSQL item", CategoryId = categoryId });
                database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 20, Reserved = 2 });
                database.EventAllocations.Add(new EventAllocation
                {
                    EventId = eventId,
                    IndustryId = industryId,
                    ItemId = itemId,
                    QuantityAllocated = 5,
                    QuantityWithdrawn = 0
                });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using (var opened = await SendAsync(client, HttpMethod.Post, $"/api/events/{eventId}/open", token, new { }))
            {
                Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
                using var json = JsonDocument.Parse(await opened.Content.ReadAsStringAsync());
                Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
                var data = json.RootElement.GetProperty("data");
                Assert.Equal("aberto", data.GetProperty("status").GetString());
                Assert.Equal("Aberto", data.GetProperty("status_label").GetString());
            }

            await using (var withdrawalScope = factory.Services.CreateAsyncScope())
            {
                var database = withdrawalScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var allocation = await database.EventAllocations.SingleAsync(value => value.EventId == eventId);
                allocation.QuantityWithdrawn = 1;
                var stock = await database.Stocks.SingleAsync(value => value.ItemId == itemId);
                stock.OnHand = 19;
                stock.Reserved = 6;
                await database.SaveChangesAsync();
            }

            using (var closed = await SendAsync(client, HttpMethod.Post, $"/api/events/{eventId}/close", token, new { }))
            {
                Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
                using var json = JsonDocument.Parse(await closed.Content.ReadAsStringAsync());
                Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
                var data = json.RootElement.GetProperty("data");
                Assert.Equal("encerrado", data.GetProperty("status").GetString());
                Assert.Equal("Encerrado", data.GetProperty("status_label").GetString());
            }

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var eventRecord = await verify.Events.SingleAsync(value => value.Id == eventId);
            Assert.Equal(userId, eventRecord.OpenedBy);
            Assert.Equal(userId, eventRecord.ClosedBy);
            Assert.Equal(2, (await verify.Stocks.SingleAsync(value => value.ItemId == itemId)).Reserved);
            Assert.Equal(["close", "open"], await verify.AuditLogs
                .Where(log => log.EntityType == "event" && log.EntityId == eventId)
                .OrderBy(log => log.Action)
                .Select(log => log.Action)
                .ToListAsync());
        }
        finally
        {
            await using var cleanupScope = factory.Services.CreateAsyncScope();
            var services = cleanupScope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            database.AuditLogs.RemoveRange(database.AuditLogs.Where(log => log.EntityId == eventId && log.EntityType == "event"));
            database.EventAllocations.RemoveRange(database.EventAllocations.Where(value => value.EventId == eventId));
            database.Events.RemoveRange(database.Events.Where(value => value.Id == eventId));
            database.Stocks.RemoveRange(database.Stocks.Where(value => value.ItemId == itemId));
            database.CatalogItems.RemoveRange(database.CatalogItems.Where(value => value.Id == itemId));
            database.Categories.RemoveRange(database.Categories.Where(value => value.Id == categoryId));
            database.Industries.RemoveRange(database.Industries.Where(value => value.Id == industryId));
            await database.SaveChangesAsync();
            if (userId is Guid id)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(id.ToString());
                if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync(roleName);
            if (role is not null) Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }

    [PostgreSqlFact]
    public async Task Concurrent_returns_from_separate_events_serialize_shared_stock_positions_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-event-return-race-{Guid.NewGuid():N}";
        var userId = Guid.Empty;
        var token = string.Empty;
        var industryId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var eventLocationId = Guid.NewGuid();
        var cdLocationId = Guid.NewGuid();
        var eventIds = new[] { Guid.NewGuid(), Guid.NewGuid() };

        try
        {
            await using (var setupScope = factory.Services.CreateAsyncScope())
            {
                var services = setupScope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "stock.transfer"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "PostgreSQL event return operator" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;

                database.Categories.Add(new Category { Id = categoryId, Name = $"Event return race {categoryId:N}", NameKey = categoryId.ToString("N") });
                database.Industries.Add(new Industry { Id = industryId, Name = $"Event return race {industryId:N}", NameKey = industryId.ToString("N") });
                database.Locations.AddRange(
                    new Location { Id = eventLocationId, Name = $"Shared event location {eventLocationId:N}", NameKey = eventLocationId.ToString("N"), Kind = "outro" },
                    new Location { Id = cdLocationId, Name = $"Return CD {cdLocationId:N}", NameKey = cdLocationId.ToString("N"), Kind = "cd" });
                await database.SaveChangesAsync();

                database.Events.AddRange(eventIds.Select((id, index) => new Event
                {
                    Id = id,
                    Name = $"Concurrent return event {index}",
                    Status = "encerrado",
                    LocationId = eventLocationId
                }));
                await database.SaveChangesAsync();

                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"EVR-{itemId:N}"[..12], Name = "Concurrent event return item", CategoryId = categoryId });
                database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 10 });
                database.StockPositions.AddRange(
                    new StockPosition { ItemId = itemId, LocationId = eventLocationId, Quantity = 5 },
                    new StockPosition { ItemId = itemId, LocationId = cdLocationId, Quantity = 1 });
                database.EventAllocations.AddRange(eventIds.Select(id => new EventAllocation
                {
                    EventId = id,
                    IndustryId = industryId,
                    ItemId = itemId,
                    QuantityAllocated = 4
                }));
                await database.SaveChangesAsync();
            }

            token = await LoginAsync(client, email);
            using var firstRequest = CreateReturnRequest(eventIds[0], "event-return-race-0001");
            using var secondRequest = CreateReturnRequest(eventIds[1], "event-return-race-0002");

            await using var blockerScope = factory.Services.CreateAsyncScope();
            var blockerDatabase = blockerScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await using var blockerTransaction = await blockerDatabase.Database.BeginTransactionAsync();
            _ = await blockerDatabase.Stocks.FromSqlInterpolated(
                $"SELECT * FROM \"Stock\" WHERE \"ItemId\" = {itemId} FOR UPDATE").SingleAsync();

            var firstTask = client.SendAsync(firstRequest);
            var secondTask = client.SendAsync(secondRequest);
            var blockedReturnLocks = 0;
            await using (var observerScope = factory.Services.CreateAsyncScope())
            {
                var observerDatabase = observerScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await observerDatabase.Database.OpenConnectionAsync();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline && blockedReturnLocks < 2 && !firstTask.IsCompleted && !secondTask.IsCompleted)
                {
                    await using var command = observerDatabase.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query ILIKE '%\"Stock\"%' AND query ILIKE '%FOR UPDATE%'";
                    blockedReturnLocks = Convert.ToInt32(await command.ExecuteScalarAsync());
                    if (blockedReturnLocks < 2)
                        await Task.Delay(50);
                }
            }

            await blockerTransaction.CommitAsync();
            using var firstResponse = await firstTask;
            using var secondResponse = await secondTask;
            Assert.Equal(2, blockedReturnLocks);
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

            await using (var verifyScope = factory.Services.CreateAsyncScope())
            {
                var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Equal(0, await verify.StockPositions.Where(position => position.ItemId == itemId && position.LocationId == eventLocationId).Select(position => position.Quantity).SingleAsync());
                Assert.Equal(6, await verify.StockPositions.Where(position => position.ItemId == itemId && position.LocationId == cdLocationId).Select(position => position.Quantity).SingleAsync());
                var movements = await verify.StockMovements.Where(movement => movement.ItemId == itemId && movement.TransferId != null).ToListAsync();
                Assert.Equal(4, movements.Count);
                Assert.Equal(-5, movements.Where(movement => movement.Quantity < 0).Sum(movement => movement.Quantity));
                Assert.Equal(5, movements.Where(movement => movement.Quantity > 0).Sum(movement => movement.Quantity));
                Assert.Equal(2, await verify.AuditLogs.CountAsync(log => log.EntityType == "event" && eventIds.Contains(log.EntityId!.Value) && log.Action == "return"));
            }
        }
        finally
        {
            await using var cleanupScope = factory.Services.CreateAsyncScope();
            var services = cleanupScope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            database.AuditLogs.RemoveRange(await database.AuditLogs.Where(log => log.EntityType == "event" && eventIds.Contains(log.EntityId!.Value)).ToListAsync());
            database.EventAllocations.RemoveRange(await database.EventAllocations.Where(value => eventIds.Contains(value.EventId)).ToListAsync());
            database.Events.RemoveRange(await database.Events.Where(value => eventIds.Contains(value.Id)).ToListAsync());
            database.StockMovements.RemoveRange(await database.StockMovements.Where(value => value.ItemId == itemId).ToListAsync());
            database.StockPositions.RemoveRange(await database.StockPositions.Where(value => value.ItemId == itemId).ToListAsync());
            database.Stocks.RemoveRange(await database.Stocks.Where(value => value.ItemId == itemId).ToListAsync());
            database.CatalogItems.RemoveRange(await database.CatalogItems.Where(value => value.Id == itemId).ToListAsync());
            database.Categories.RemoveRange(await database.Categories.Where(value => value.Id == categoryId).ToListAsync());
            database.Industries.RemoveRange(await database.Industries.Where(value => value.Id == industryId).ToListAsync());
            database.Locations.RemoveRange(await database.Locations.Where(value => value.Id == eventLocationId || value.Id == cdLocationId).ToListAsync());
            await database.SaveChangesAsync();

            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(userId.ToString());
            if (user is not null)
                Assert.True((await users.DeleteAsync(user)).Succeeded);
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync(roleName);
            if (role is not null)
                Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }

        HttpRequestMessage CreateReturnRequest(Guid eventId, string key)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/events/{eventId}/returns")
            {
                Content = JsonContent.Create(new { items = new[] { new { industry_id = industryId, item_id = itemId, qty = 4 } } })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Idempotency-Key", key);
            return request;
        }
    }

    [PostgreSqlFact]
    public async Task Event_creation_persists_dates_status_and_audit_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-event-manager-{Guid.NewGuid():N}";
        var eventName = $"PostgreSQL event {Guid.NewGuid():N}";
        Guid? userId = null;
        Guid? eventId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
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
                    FullName = "PostgreSQL event manager"
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
            }

            var token = await LoginAsync(client, email);
            using var created = await SendAsync(client, HttpMethod.Post, "/api/v1/events", token, new
            {
                name = eventName,
                description = "Evento persistido no PostgreSQL.",
                starts_on = new DateOnly(2026, 12, 12),
                ends_on = new DateOnly(2026, 12, 13)
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            eventId = createdJson.RootElement.GetProperty("id").GetGuid();

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var eventRecord = await verifyDatabase.Events.SingleAsync(value => value.Id == eventId);
            Assert.Equal("planejado", eventRecord.Status);
            Assert.Equal(new DateOnly(2026, 12, 12), eventRecord.StartsOn);
            Assert.Equal(new DateOnly(2026, 12, 13), eventRecord.EndsOn);
            Assert.Equal(userId, eventRecord.CreatedBy);
            Assert.Equal(userId, eventRecord.UpdatedBy);
            var audit = await verifyDatabase.AuditLogs.SingleAsync(value => value.EntityType == "event" && value.EntityId == eventId);
            Assert.Equal("create", audit.Action);
            Assert.Equal(userId, audit.UserId);
        }
        finally
        {
            await using var cleanup = factory.Services.CreateAsyncScope();
            var services = cleanup.ServiceProvider;
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
            if (userId is Guid idUser)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(idUser.ToString());
                if (user is not null)
                    Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync(roleName);
            if (role is not null)
                Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }

    [PostgreSqlFact]
    public async Task Trade_draft_submission_and_decision_persist_with_history_and_audit_in_postgres()
    {
        var client = factory.CreateClient();
        var requesterEmail = $"{Guid.NewGuid():N}@example.invalid";
        var approverEmail = $"{Guid.NewGuid():N}@example.invalid";
        var receiverEmail = $"{Guid.NewGuid():N}@example.invalid";
        var operatorEmail = $"{Guid.NewGuid():N}@example.invalid";
        var outsiderEmail = $"{Guid.NewGuid():N}@example.invalid";
        var requesterRoleName = $"postgres-trade-requester-{Guid.NewGuid():N}";
        var approverRoleName = $"postgres-trade-approver-{Guid.NewGuid():N}";
        var receiverRoleName = $"CD/Estoque {Guid.NewGuid():N}";
        var operatorRoleName = $"postgres-trade-operator-{Guid.NewGuid():N}";
        var outsiderRoleName = $"postgres-trade-outsider-{Guid.NewGuid():N}";
        var categoryId = Guid.NewGuid();
        var industryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var otherItemId = Guid.NewGuid();
        var requestId = Guid.Empty;
        Guid? requesterId = null;
        Guid? approverId = null;
        Guid? receiverId = null;
        Guid? operatorId = null;
        Guid? outsiderId = null;
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var requesterRole = new ApplicationRole(requesterRoleName);
                var approverRole = new ApplicationRole(approverRoleName);
                var receiverRole = new ApplicationRole(receiverRoleName);
                var operatorRole = new ApplicationRole(operatorRoleName);
                var outsiderRole = new ApplicationRole(outsiderRoleName);
                Assert.True((await roles.CreateAsync(requesterRole)).Succeeded);
                Assert.True((await roles.CreateAsync(approverRole)).Succeeded);
                Assert.True((await roles.CreateAsync(receiverRole)).Succeeded);
                Assert.True((await roles.CreateAsync(operatorRole)).Succeeded);
                Assert.True((await roles.CreateAsync(outsiderRole)).Succeeded);
                Assert.True((await roles.AddClaimAsync(requesterRole, new Claim("permission", "requests.create"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(requesterRole, new Claim("permission", "requests.view_own"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(approverRole, new Claim("permission", "requests.approve"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(receiverRole, new Claim("permission", "stock.receive"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(operatorRole, new Claim("permission", "stock.exit"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(outsiderRole, new Claim("permission", "requests.view_own"))).Succeeded);
                var requester = new ApplicationUser { UserName = requesterEmail, Email = requesterEmail, EmailConfirmed = true, FullName = "PostgreSQL TRADE requester", IndustryId = industryId };
                var approver = new ApplicationUser { UserName = approverEmail, Email = approverEmail, EmailConfirmed = true, FullName = "PostgreSQL TRADE approver" };
                var receiver = new ApplicationUser { UserName = receiverEmail, Email = receiverEmail, EmailConfirmed = true, FullName = "PostgreSQL TRADE receiver" };
                var operatorUser = new ApplicationUser { UserName = operatorEmail, Email = operatorEmail, EmailConfirmed = true, FullName = "PostgreSQL TRADE operator" };
                var outsider = new ApplicationUser { UserName = outsiderEmail, Email = outsiderEmail, EmailConfirmed = true, FullName = "PostgreSQL unrelated requester" };
                Assert.True((await users.CreateAsync(requester, "CorrectPassword1")).Succeeded);
                Assert.True((await users.CreateAsync(approver, "CorrectPassword1")).Succeeded);
                Assert.True((await users.CreateAsync(receiver, "CorrectPassword1")).Succeeded);
                Assert.True((await users.CreateAsync(operatorUser, "CorrectPassword1")).Succeeded);
                Assert.True((await users.CreateAsync(outsider, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(requester, requesterRoleName)).Succeeded);
                Assert.True((await users.AddToRoleAsync(approver, approverRoleName)).Succeeded);
                Assert.True((await users.AddToRoleAsync(receiver, receiverRoleName)).Succeeded);
                Assert.True((await users.AddToRoleAsync(operatorUser, operatorRoleName)).Succeeded);
                Assert.True((await users.AddToRoleAsync(outsider, outsiderRoleName)).Succeeded);
                requesterId = requester.Id;
                approverId = approver.Id;
                receiverId = receiver.Id;
                operatorId = operatorUser.Id;
                outsiderId = outsider.Id;
                database.Categories.Add(new Category { Id = categoryId, Name = $"Trade {categoryId:N}", NameKey = categoryId.ToString("N") });
                database.Industries.Add(new Industry { Id = industryId, Name = $"Trade {industryId:N}", NameKey = industryId.ToString("N") });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"TRA-{itemId:N}"[..12], Name = "PostgreSQL TRADE item", CategoryId = categoryId, UnitValue = 12.50m });
                database.CatalogItems.Add(new CatalogItem { Id = otherItemId, Code = $"TRA-{otherItemId:N}"[..12], Name = "Unrelated PostgreSQL item", CategoryId = categoryId, UnitValue = 4m });
                await database.SaveChangesAsync();
            }

            var requesterToken = await LoginAsync(client, requesterEmail);
            var approverToken = await LoginAsync(client, approverEmail);
            var receiverToken = await LoginAsync(client, receiverEmail);
            var operatorToken = await LoginAsync(client, operatorEmail);
            var outsiderToken = await LoginAsync(client, outsiderEmail);
            using var created = await SendAsync(client, HttpMethod.Post, "/api/v1/trade-requests", requesterToken,
                new { purpose = "PostgreSQL TRADE persistence", items = new[] { new { item_id = itemId, qty_requested = 3 } } });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            requestId = createdJson.RootElement.GetProperty("data").GetProperty("id").GetGuid();
            var requestCode = createdJson.RootElement.GetProperty("data").GetProperty("code").GetString()!;
            var publicCode = createdJson.RootElement.GetProperty("data").GetProperty("public_code").GetString()!;
            Assert.Equal(requestCode, publicCode);
            Assert.Matches("^EME-[0-9]{4}-[0-9]{6}$", publicCode);
            var legacyPublicCode = $"EME-LEGACY-{Guid.NewGuid():N}"[..36];
            await using (var publicCodeScope = factory.Services.CreateAsyncScope())
            {
                var publicCodeDatabase = publicCodeScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var request = await publicCodeDatabase.Requests.SingleAsync(value => value.Id == requestId);
                request.PublicCode = legacyPublicCode;
                var firstRequestItem = await publicCodeDatabase.RequestItems
                    .SingleAsync(value => value.RequestId == requestId && value.ItemId == itemId);
                Assert.Equal(0, firstRequestItem.Position);
                publicCodeDatabase.RequestItems.Add(new InternalRequestItem
                {
                    Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                    RequestId = requestId,
                    ItemId = otherItemId,
                    Position = 1,
                    QuantityRequested = 1
                });
                await publicCodeDatabase.SaveChangesAsync();
            }
            using var publicCodeLookup = await SendAsync(client, HttpMethod.Get,
                $"/api/v1/trade-requests/qr-info?code={Uri.EscapeDataString(legacyPublicCode)}", requesterToken);
            Assert.Equal(HttpStatusCode.OK, publicCodeLookup.StatusCode);
            using (var publicCodeLookupJson = JsonDocument.Parse(await publicCodeLookup.Content.ReadAsStringAsync()))
            {
                Assert.Equal(legacyPublicCode, publicCodeLookupJson.RootElement.GetProperty("data").GetProperty("code").GetString());
                var orderedProducts = publicCodeLookupJson.RootElement.GetProperty("data").GetProperty("products").EnumerateArray().ToArray();
                Assert.Collection(orderedProducts,
                    first => Assert.Equal("PostgreSQL TRADE item", first.GetProperty("name").GetString()),
                    second => Assert.Equal("Unrelated PostgreSQL item", second.GetProperty("name").GetString()));
            }
            await using (var orderCleanupScope = factory.Services.CreateAsyncScope())
            {
                var orderCleanupDatabase = orderCleanupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var temporaryLine = await orderCleanupDatabase.RequestItems
                    .SingleAsync(value => value.Id == Guid.Parse("00000000-0000-0000-0000-000000000001"));
                orderCleanupDatabase.RequestItems.Remove(temporaryLine);
                await orderCleanupDatabase.SaveChangesAsync();
            }
            using var requestQr = await SendAsync(client, HttpMethod.Get, $"/api/v1/trade-requests/{requestId}/qr", requesterToken);
            Assert.Equal(HttpStatusCode.OK, requestQr.StatusCode);
            Assert.Equal("image/png", requestQr.Content.Headers.ContentType?.MediaType);
            Assert.Equal("nosniff", requestQr.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.True(requestQr.Headers.CacheControl?.Private);
            var qrBytes = await requestQr.Content.ReadAsByteArrayAsync();
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, qrBytes[..8]);
            using var deniedQr = await SendAsync(client, HttpMethod.Get, $"/api/v1/trade-requests/{requestId}/qr", approverToken);
            Assert.Equal(HttpStatusCode.Forbidden, deniedQr.StatusCode);
            using var outOfScopeQr = await SendAsync(client, HttpMethod.Get, $"/api/v1/trade-requests/{requestId}/qr", outsiderToken);
            Assert.Equal(HttpStatusCode.NotFound, outOfScopeQr.StatusCode);

            using var submitted = await SendAsync(client, HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/submit", requesterToken);
            Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
            using var approved = await SendAsync(client, HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/approve", approverToken,
                new { approved = true, justification = "Compra autorizada." });
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
            using var purchased = await SendAsync(client, HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/purchase", approverToken,
                new { purchase_ticket_no = "PG-TRADE-102" });
            Assert.Equal(HttpStatusCode.OK, purchased.StatusCode);
            var invoiceBytes = Convert.FromBase64String(CreateSignatureDataUri().Split(',')[1]);
            using var receiveForm = new MultipartFormDataContent();
            receiveForm.Add(new StringContent("PG-NF-902"), "invoice_no");
            receiveForm.Add(new StringContent(JsonSerializer.Serialize(new[] { new { item_id = itemId, qty = 2 } })), "items");
            using var invoiceContent = new ByteArrayContent(invoiceBytes);
            invoiceContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            receiveForm.Add(invoiceContent, "invoice", "nf.png");
            using var receiveRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/receive")
            {
                Content = receiveForm
            };
            receiveRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", receiverToken);
            receiveRequest.Headers.Add("Idempotency-Key", "pg-trade-receive-001");
            using var received = await client.SendAsync(receiveRequest);
            Assert.Equal(HttpStatusCode.Created, received.StatusCode);
            using var receivedJson = JsonDocument.Parse(await received.Content.ReadAsStringAsync());
            var firstReceipt = receivedJson.RootElement.GetProperty("data");
            Assert.Equal("aguardando_recebimento", firstReceipt.GetProperty("status").GetString());
            Assert.Equal(2, firstReceipt.GetProperty("items")[0].GetProperty("qty_received").GetInt32());
            var invoiceUrl = receivedJson.RootElement.GetProperty("data").GetProperty("invoice_url").GetString();
            Assert.Equal($"/api/v1/trade-requests/{requestId}/invoice", invoiceUrl);

            using var rejectedForm = new MultipartFormDataContent();
            rejectedForm.Add(new StringContent("PG-NF-902"), "invoice_no");
            rejectedForm.Add(new StringContent(JsonSerializer.Serialize(new[]
            {
                new { item_id = itemId, qty = 1 }, new { item_id = otherItemId, qty = 1 }
            })), "items");
            using var rejectedReceiveRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/receive")
            {
                Content = rejectedForm
            };
            rejectedReceiveRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", receiverToken);
            rejectedReceiveRequest.Headers.Add("Idempotency-Key", "pg-trade-receive-invalid-1");
            using var rejectedReceipt = await client.SendAsync(rejectedReceiveRequest);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejectedReceipt.StatusCode);

            await using (var partialScope = factory.Services.CreateAsyncScope())
            {
                var partialDatabase = partialScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var partialRequest = await partialDatabase.Requests.Include(value => value.Items)
                    .SingleAsync(value => value.Id == requestId);
                Assert.Equal("aguardando_recebimento", partialRequest.Status);
                Assert.Equal(2, partialRequest.Items.Single().QuantityReceived);
                var partialEntries = await partialDatabase.StockMovements
                    .Where(value => (value.ItemId == itemId || value.ItemId == otherItemId) && value.Type == "entrada")
                    .ToListAsync();
                Assert.Single(partialEntries);
                Assert.Equal(2, partialEntries[0].Quantity);
                Assert.Single(await partialDatabase.AuditLogs.Where(log => log.EntityId == requestId
                    && log.EntityType == "request" && log.Action == "receive").ToListAsync());
            }

            using var finalReceiveForm = new MultipartFormDataContent();
            finalReceiveForm.Add(new StringContent("PG-NF-902"), "invoice_no");
            finalReceiveForm.Add(new StringContent(JsonSerializer.Serialize(new[] { new { item_id = itemId, qty = 1 } })), "items");
            using var finalReceiveRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/receive")
            {
                Content = finalReceiveForm
            };
            finalReceiveRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", receiverToken);
            finalReceiveRequest.Headers.Add("Idempotency-Key", "pg-trade-receive-002");
            using var finalReceipt = await client.SendAsync(finalReceiveRequest);
            Assert.Equal(HttpStatusCode.Created, finalReceipt.StatusCode);
            using var finalReceiptJson = JsonDocument.Parse(await finalReceipt.Content.ReadAsStringAsync());
            Assert.Equal("pronta", finalReceiptJson.RootElement.GetProperty("data").GetProperty("status").GetString());
            Assert.Equal(3, finalReceiptJson.RootElement.GetProperty("data").GetProperty("items")[0].GetProperty("qty_received").GetInt32());

            using var invoiceDownload = await SendAsync(client, HttpMethod.Get, invoiceUrl!, receiverToken);
            Assert.Equal(HttpStatusCode.OK, invoiceDownload.StatusCode);
            Assert.Equal("image/png", invoiceDownload.Content.Headers.ContentType?.MediaType);
            Assert.Equal(invoiceBytes, await invoiceDownload.Content.ReadAsByteArrayAsync());
            using var deniedInvoiceDownload = await SendAsync(client, HttpMethod.Get, invoiceUrl!, approverToken);
            Assert.Equal(HttpStatusCode.Forbidden, deniedInvoiceDownload.StatusCode);
            using var outOfScopeInvoice = await SendAsync(client, HttpMethod.Get, invoiceUrl!, outsiderToken);
            Assert.Equal(HttpStatusCode.NotFound, outOfScopeInvoice.StatusCode);

            var signature = CreateSignatureDataUri();
            using var withdrawRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/withdraw")
            {
                Content = JsonContent.Create(new
                {
                    received_by_name = "Pessoa retirada PostgreSQL",
                    received_by_document = "123.456.789-00",
                    signature,
                    items = new[] { new { item_id = itemId, qty = 1 } }
                })
            };
            withdrawRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
            withdrawRequest.Headers.Add("Idempotency-Key", "pg-trade-withdraw-001");
            using var withdrawn = await client.SendAsync(withdrawRequest);
            Assert.Equal(HttpStatusCode.Created, withdrawn.StatusCode);
            using var withdrawnJson = JsonDocument.Parse(await withdrawn.Content.ReadAsStringAsync());
            var withdrawnData = withdrawnJson.RootElement.GetProperty("data");
            var deliveryId = withdrawnData.GetProperty("id").GetGuid();
            Assert.StartsWith("PROT-", withdrawnData.GetProperty("code").GetString());
            Assert.False(withdrawnData.TryGetProperty("signature_path", out _));

            using var replayRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/withdraw")
            {
                Content = JsonContent.Create(new
                {
                    received_by_name = "Pessoa retirada PostgreSQL",
                    received_by_document = "123.456.789-00",
                    signature,
                    items = new[] { new { item_id = itemId, qty = 1 } }
                })
            };
            replayRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
            replayRequest.Headers.Add("Idempotency-Key", "pg-trade-withdraw-001");
            using var replay = await client.SendAsync(replayRequest);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            Assert.Equal(deliveryId, replayJson.RootElement.GetProperty("data").GetProperty("id").GetGuid());

            using var changedRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/withdraw")
            {
                Content = JsonContent.Create(new
                {
                    received_by_name = "Pessoa retirada PostgreSQL",
                    received_by_document = "123.456.789-00",
                    signature,
                    items = new[] { new { item_id = itemId, qty = 2 } }
                })
            };
            changedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
            changedRequest.Headers.Add("Idempotency-Key", "pg-trade-withdraw-001");
            using var changedReplay = await client.SendAsync(changedRequest);
            Assert.Equal(HttpStatusCode.Conflict, changedReplay.StatusCode);

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var saved = await verify.Requests.Include(request => request.Items).Include(request => request.History)
                .Include(request => request.Approvals).SingleAsync(request => request.Id == requestId);
            Assert.Equal("trade", saved.Flow);
            Assert.Equal("retirado", saved.Status);
            Assert.Equal("PG-TRADE-102", saved.PurchaseTicketNo);
            Assert.Equal("PG-NF-902", await verify.Database.SqlQueryRaw<string>(
                "SELECT \"InvoiceNo\" AS \"Value\" FROM \"Requests\" WHERE \"Id\" = {0}", requestId).SingleAsync());
            Assert.False(string.IsNullOrWhiteSpace(saved.InvoicePath));
            Assert.True(File.Exists(Path.Combine(factory.InvoiceRoot, saved.InvoicePath!.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Equal(3, saved.Items.Single().QuantityRequested);
            Assert.Equal(3, await verify.Database.SqlQueryRaw<int>(
                "SELECT \"QuantityReceived\" AS \"Value\" FROM \"RequestItems\" WHERE \"RequestId\" = {0}", requestId).SingleAsync());
            Assert.Equal(12.50m, saved.Items.Single().UnitValue);
            Assert.Equal(8, saved.History.Count);
            Assert.Equal("aprovado", saved.Approvals.Single().Decision);
            Assert.Equal("Compra autorizada.", saved.Approvals.Single().Justification);
            Assert.Equal(approverId, saved.Approvals.Single().DecidedById);
            Assert.Equal(["approve", "create", "purchase", "receive", "receive", "submit"], await verify.AuditLogs
                .Where(log => log.EntityType == "request" && log.EntityId == requestId)
                .OrderBy(log => log.Action).Select(log => log.Action).ToArrayAsync());
            var receiptMovements = await verify.StockMovements.Where(value => value.ItemId == itemId && value.Type == "entrada")
                .ToListAsync();
            Assert.Equal(2, receiptMovements.Count);
            Assert.Equal(3, receiptMovements.Sum(value => value.Quantity));
            Assert.All(receiptMovements, movement =>
            {
                Assert.Equal(industryId, movement.IndustryId);
                Assert.Equal("PG-NF-902", movement.DocumentRef);
            });
            var exit = await verify.StockMovements.SingleAsync(value => value.ItemId == itemId && value.Type == "saida");
            Assert.Equal(-1, exit.Quantity);
            Assert.Equal(deliveryId, exit.DeliveryId);
            Assert.Equal(2, (await verify.Stocks.SingleAsync(value => value.ItemId == itemId)).OnHand);
            var delivery = await verify.Deliveries.Include(value => value.Items).SingleAsync(value => value.Id == deliveryId);
            Assert.Equal("Pessoa retirada PostgreSQL", delivery.ReceivedByName);
            Assert.Equal(1, Assert.Single(delivery.Items).Quantity);
            Assert.True(File.Exists(Path.Combine(factory.SignatureRoot, delivery.SignaturePath.Replace('/', Path.DirectorySeparatorChar))));
            Assert.True(await verify.AuditLogs.AnyAsync(log => log.EntityType == "delivery" && log.EntityId == deliveryId && log.Action == "withdraw"));
            var qrInfoPath = $"/api/v1/trade-requests/qr-info?code={Uri.EscapeDataString(requestCode)}";
            using var qrInfo = await SendAsync(client, HttpMethod.Get, qrInfoPath, requesterToken);
            Assert.Equal(HttpStatusCode.OK, qrInfo.StatusCode);
            using var qrInfoJson = JsonDocument.Parse(await qrInfo.Content.ReadAsStringAsync());
            var qrInfoData = qrInfoJson.RootElement.GetProperty("data");
            Assert.Equal(legacyPublicCode, qrInfoData.GetProperty("code").GetString());
            Assert.Equal($"Trade {industryId:N}", qrInfoData.GetProperty("industry").GetString());
            var qrProduct = Assert.Single(qrInfoData.GetProperty("products").EnumerateArray());
            Assert.Equal(2, qrProduct.GetProperty("saldo").GetInt32());
            Assert.False(qrInfoData.TryGetProperty("recipient", out _));
            using var deniedQrInfo = await SendAsync(client, HttpMethod.Get, qrInfoPath, approverToken);
            Assert.Equal(HttpStatusCode.Forbidden, deniedQrInfo.StatusCode);
            using var outOfScopeQrInfo = await SendAsync(client, HttpMethod.Get, qrInfoPath, outsiderToken);
            Assert.Equal(HttpStatusCode.NotFound, outOfScopeQrInfo.StatusCode);
            using var internalList = await SendAsync(client, HttpMethod.Get, "/api/v1/requests", requesterToken);
            Assert.Equal(HttpStatusCode.OK, internalList.StatusCode);
            using var internalListJson = JsonDocument.Parse(await internalList.Content.ReadAsStringAsync());
            Assert.Equal(0, internalListJson.RootElement.GetProperty("meta").GetProperty("total").GetInt32());
        }
        finally
        {
            await using var cleanup = factory.Services.CreateAsyncScope();
            var services = cleanup.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            var deliveryIds = await database.Deliveries.Where(delivery => delivery.RequestId == requestId)
                .Select(delivery => delivery.Id).ToListAsync();
            database.AuditLogs.RemoveRange(database.AuditLogs.Where(log => log.EntityType == "delivery" && deliveryIds.Contains(log.EntityId!.Value)));
            database.StockMovements.RemoveRange(database.StockMovements.Where(movement => movement.DeliveryId != null && deliveryIds.Contains(movement.DeliveryId.Value)));
            database.Deliveries.RemoveRange(database.Deliveries.Where(delivery => delivery.RequestId == requestId));
            await database.SaveChangesAsync();
            if (requestId != Guid.Empty)
            {
                database.Requests.RemoveRange(database.Requests.Where(request => request.Id == requestId));
                await database.SaveChangesAsync();
            }
            database.AuditLogs.RemoveRange(database.AuditLogs.Where(log => log.EntityId == requestId && log.EntityType == "request"));
            database.StockMovements.RemoveRange(database.StockMovements.Where(movement => movement.ItemId == itemId || movement.ItemId == otherItemId));
            database.Stocks.RemoveRange(database.Stocks.Where(stock => stock.ItemId == itemId || stock.ItemId == otherItemId));
            database.StockPositions.RemoveRange(database.StockPositions.Where(position => position.ItemId == itemId || position.ItemId == otherItemId));
            database.CatalogItems.RemoveRange(database.CatalogItems.Where(item => item.Id == itemId || item.Id == otherItemId));
            database.Categories.RemoveRange(database.Categories.Where(category => category.Id == categoryId));
            await database.SaveChangesAsync();
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            foreach (var id in new[] { requesterId, approverId, receiverId, operatorId, outsiderId }.OfType<Guid>())
            {
                var user = await users.FindByIdAsync(id.ToString());
                if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            database.Industries.RemoveRange(database.Industries.Where(industry => industry.Id == industryId));
            await database.SaveChangesAsync();
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            foreach (var name in new[] { requesterRoleName, approverRoleName, receiverRoleName, operatorRoleName, outsiderRoleName })
            {
                var role = await roles.FindByNameAsync(name);
                if (role is not null) Assert.True((await roles.DeleteAsync(role)).Succeeded);
            }
        }
    }

    [PostgreSqlFact]
    public async Task Stock_transfer_commits_both_positions_movements_and_audit_in_one_postgres_operation()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-transfer-{Guid.NewGuid():N}";
        var industryId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var fromId = Guid.NewGuid();
        var toId = Guid.NewGuid();
        Guid? userId = null;
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "stock.transfer"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "stock.view"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "PostgreSQL stock transfer" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Categories.Add(new Category { Id = categoryId, Name = $"Transfer {categoryId:N}", NameKey = categoryId.ToString("N") });
                database.Industries.Add(new Industry { Id = industryId, Name = $"Transfer {industryId:N}", NameKey = industryId.ToString("N") });
                database.Locations.AddRange(
                    new Location { Id = fromId, Name = $"From {fromId:N}", NameKey = fromId.ToString("N"), Kind = "cd" },
                    new Location { Id = toId, Name = $"To {toId:N}", NameKey = toId.ToString("N"), Kind = "outro" });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"PG-{itemId:N}"[..12], Name = "Brinde transferido", CategoryId = categoryId });
                database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 10 });
                database.StockPositions.Add(new StockPosition { ItemId = itemId, LocationId = fromId, Quantity = 10 });
                database.StockMovements.Add(new StockMovement { ItemId = itemId, IndustryId = industryId, Type = "entrada", Quantity = 10, BalanceAfter = 10, LocationId = fromId });
                await database.SaveChangesAsync();
            }

            using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            var token = loginJson.RootElement.GetProperty("accessToken").GetString()!;
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/transfers")
            {
                Content = JsonContent.Create(new { item_id = itemId, quantity = 4, industry_id = industryId, from_location_id = fromId, to_location_id = toId })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Idempotency-Key", "postgres-transfer-001");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var ids = result.RootElement.GetProperty("data").GetProperty("movements").EnumerateArray().Select(movement => movement.GetProperty("id").GetGuid()).ToArray();
            Assert.Equal(2, ids.Length);
            using var search = await SendAsync(client, HttpMethod.Get,
                $"/api/v1/stock/movements?itemId={itemId}&q=BRINDE%20TRANSFERIDO", token);
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);
            using var searchJson = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
            Assert.Equal(3, searchJson.RootElement.GetProperty("items").GetArrayLength());
            using var filtered = await SendAsync(client, HttpMethod.Get,
                $"/api/v1/stock/movements?itemId={itemId}&user_id={userId}&industry_id={industryId}&from=2000-01-01&to=2999-12-31", token);
            Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
            using var filteredJson = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
            Assert.Equal(2, filteredJson.RootElement.GetProperty("items").GetArrayLength());
            using var categoryFiltered = await SendAsync(client, HttpMethod.Get,
                $"/api/v1/stock/movements?category_id={categoryId}", token);
            Assert.Equal(HttpStatusCode.OK, categoryFiltered.StatusCode);
            using var categoryFilteredJson = JsonDocument.Parse(await categoryFiltered.Content.ReadAsStringAsync());
            Assert.Equal(3, categoryFilteredJson.RootElement.GetProperty("items").GetArrayLength());
            using var byIndustry = await SendAsync(client, HttpMethod.Get,
                $"/api/v1/stock/by-industry?industry_id={industryId}", token);
            Assert.Equal(HttpStatusCode.OK, byIndustry.StatusCode);
            using var byIndustryJson = JsonDocument.Parse(await byIndustry.Content.ReadAsStringAsync());
            var industryRow = Assert.Single(byIndustryJson.RootElement.GetProperty("data").EnumerateArray());
            Assert.Equal(industryId, industryRow.GetProperty("industry").GetProperty("id").GetGuid());
            Assert.Equal(itemId, industryRow.GetProperty("item").GetProperty("id").GetGuid());
            Assert.Equal(10, industryRow.GetProperty("received").GetInt32());
            Assert.Equal(0, industryRow.GetProperty("withdrawn").GetInt32());
            Assert.Equal(10, industryRow.GetProperty("saldo").GetInt32());

            await using (var verifyScope = factory.Services.CreateAsyncScope())
            {
                var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Equal(6, (await verify.StockPositions.SingleAsync(position => position.ItemId == itemId && position.LocationId == fromId)).Quantity);
                Assert.Equal(4, (await verify.StockPositions.SingleAsync(position => position.ItemId == itemId && position.LocationId == toId)).Quantity);
                Assert.Equal(10, (await verify.Stocks.SingleAsync(stock => stock.ItemId == itemId)).OnHand);
                Assert.Equal(2, await verify.StockMovements.CountAsync(movement => movement.TransferId == verify.StockMovements.Where(row => ids.Contains(row.Id)).Select(row => row.TransferId).First()));
                var audit = await verify.AuditLogs.SingleAsync(log => log.Action == "stock_transfer" && log.EntityId == itemId);
                Assert.Equal(userId, audit.UserId);
                using var before = JsonDocument.Parse(audit.BeforeJson!);
                using var after = JsonDocument.Parse(audit.AfterJson!);
                Assert.Equal(10, before.RootElement.GetProperty("on_hand").GetInt32());
                Assert.Equal(10, after.RootElement.GetProperty("on_hand").GetInt32());
            }

            using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/transfers")
            {
                Content = JsonContent.Create(new { item_id = itemId, quantity = 4, industry_id = industryId, from_location_id = fromId, to_location_id = toId })
            };
            replayRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            replayRequest.Headers.Add("Idempotency-Key", "postgres-transfer-001");
            using var replay = await client.SendAsync(replayRequest);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            Assert.Equal(ids, replayJson.RootElement.GetProperty("data").GetProperty("movements").EnumerateArray().Select(movement => movement.GetProperty("id").GetGuid()).ToArray());
        }
        finally
        {
            await using var cleanup = factory.Services.CreateAsyncScope();
            var services = cleanup.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            database.AuditLogs.RemoveRange(database.AuditLogs.Where(log => log.EntityId == itemId));
            database.StockMovements.RemoveRange(database.StockMovements.Where(movement => movement.ItemId == itemId));
            database.StockPositions.RemoveRange(database.StockPositions.Where(position => position.ItemId == itemId));
            database.Stocks.RemoveRange(database.Stocks.Where(stock => stock.ItemId == itemId));
            database.CatalogItems.RemoveRange(database.CatalogItems.Where(item => item.Id == itemId));
            database.Categories.RemoveRange(database.Categories.Where(category => category.Id == categoryId));
            database.Industries.RemoveRange(database.Industries.Where(industry => industry.Id == industryId));
            database.Locations.RemoveRange(database.Locations.Where(location => location.Id == fromId || location.Id == toId));
            await database.SaveChangesAsync();
            if (userId is Guid id)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(id.ToString());
                if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync(roleName);
            if (role is not null) Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }

    [PostgreSqlFact]
    public async Task Event_stock_transfer_updates_allocation_reservation_positions_and_replay_atomically_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-event-transfer-{Guid.NewGuid():N}";
        var industryId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var fromId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var key = $"pg-event-transfer-{Guid.NewGuid():N}";
        Guid? userId = null;
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "stock.transfer"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "PostgreSQL event transfer" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                userId = user.Id;
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                database.Categories.Add(new Category { Id = categoryId, Name = $"Event transfer {categoryId:N}", NameKey = categoryId.ToString("N") });
                database.Industries.Add(new Industry { Id = industryId, Name = $"Event transfer {industryId:N}", NameKey = industryId.ToString("N") });
                database.Locations.Add(new Location { Id = fromId, Name = $"Event source {fromId:N}", NameKey = fromId.ToString("N"), Kind = "cd" });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"EV-{itemId:N}"[..12], Name = "PostgreSQL event transfer item", CategoryId = categoryId });
                database.Events.Add(new Event { Id = eventId, Name = "PostgreSQL open event", Status = "aberto" });
                database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 8, Reserved = 1 });
                database.StockPositions.Add(new StockPosition { ItemId = itemId, LocationId = fromId, Quantity = 8 });
                database.EventAllocations.Add(new EventAllocation { EventId = eventId, IndustryId = industryId, ItemId = itemId, QuantityAllocated = 2, QuantityWithdrawn = 1 });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            async Task<HttpResponseMessage> TransferAsync(int quantity)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/transfers")
                {
                    Content = JsonContent.Create(new { item_id = itemId, quantity, industry_id = industryId, from_location_id = fromId, event_id = eventId })
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Add("Idempotency-Key", key);
                return await client.SendAsync(request);
            }

            using var created = await TransferAsync(3);
            Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            using var replay = await TransferAsync(3);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            using var conflict = await TransferAsync(2);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var eventLocationId = (await verify.Events.SingleAsync(value => value.Id == eventId)).LocationId;
            Assert.NotNull(eventLocationId);
            var destinationLocation = await verify.Locations.SingleAsync(value => value.Id == eventLocationId);
            Assert.Equal("evento", destinationLocation.Kind);
            Assert.Equal($"Evento: PostgreSQL open event", destinationLocation.Name);
            Assert.Equal(5, (await verify.StockPositions.SingleAsync(value => value.ItemId == itemId && value.LocationId == fromId)).Quantity);
            Assert.Equal(3, (await verify.StockPositions.SingleAsync(value => value.ItemId == itemId && value.LocationId == eventLocationId)).Quantity);
            var stock = await verify.Stocks.SingleAsync(value => value.ItemId == itemId);
            Assert.Equal(8, stock.OnHand);
            Assert.Equal(4, stock.Reserved);
            var allocation = await verify.EventAllocations.SingleAsync(value => value.EventId == eventId && value.IndustryId == industryId && value.ItemId == itemId);
            Assert.Equal(5, allocation.QuantityAllocated);
            Assert.Equal(1, allocation.QuantityWithdrawn);
            var movements = await verify.StockMovements.Where(value => value.ItemId == itemId && value.Type == "transferencia").ToListAsync();
            Assert.Equal(2, movements.Count);
            Assert.Equal(0, movements.Sum(value => value.Quantity));
            Assert.Single(movements.Select(value => value.TransferId).Distinct());
            Assert.Single(await verify.AuditLogs.Where(value => value.Action == "stock_transfer" && value.EntityId == itemId).ToListAsync());
        }
        finally
        {
            await using var cleanup = factory.Services.CreateAsyncScope();
            var services = cleanup.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            database.AuditLogs.RemoveRange(database.AuditLogs.Where(value => value.EntityId == itemId));
            database.StockMovements.RemoveRange(database.StockMovements.Where(value => value.ItemId == itemId));
            database.EventAllocations.RemoveRange(database.EventAllocations.Where(value => value.EventId == eventId));
            database.StockPositions.RemoveRange(database.StockPositions.Where(value => value.ItemId == itemId));
            database.Stocks.RemoveRange(database.Stocks.Where(value => value.ItemId == itemId));
            database.Events.RemoveRange(database.Events.Where(value => value.Id == eventId));
            database.CatalogItems.RemoveRange(database.CatalogItems.Where(value => value.Id == itemId));
            database.Categories.RemoveRange(database.Categories.Where(value => value.Id == categoryId));
            database.Industries.RemoveRange(database.Industries.Where(value => value.Id == industryId));
            var eventLocationId = await database.Events.Where(value => value.Id == eventId).Select(value => value.LocationId).SingleOrDefaultAsync();
            database.Locations.RemoveRange(database.Locations.Where(value => value.Id == fromId || value.Id == eventLocationId));
            await database.SaveChangesAsync();
            if (userId is Guid id)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(id.ToString());
                if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync(roleName);
            if (role is not null) Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }

    [PostgreSqlFact]
    public async Task Concurrent_stock_transfers_cannot_overdraw_a_source_position_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-transfer-race-{Guid.NewGuid():N}";
        var industryId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var fromId = Guid.NewGuid();
        var toId = Guid.NewGuid();
        Guid? userId = null;
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "stock.transfer"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "PostgreSQL concurrent transfer" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Categories.Add(new Category { Id = categoryId, Name = $"Transfer race {categoryId:N}", NameKey = categoryId.ToString("N") });
                database.Industries.Add(new Industry { Id = industryId, Name = $"Transfer race {industryId:N}", NameKey = industryId.ToString("N") });
                database.Locations.AddRange(
                    new Location { Id = fromId, Name = $"Race from {fromId:N}", NameKey = fromId.ToString("N"), Kind = "cd" },
                    new Location { Id = toId, Name = $"Race to {toId:N}", NameKey = toId.ToString("N"), Kind = "outro" });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"PG-{itemId:N}"[..12], Name = "Concurrent transfer item", CategoryId = categoryId });
                database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 10 });
                database.StockPositions.Add(new StockPosition { ItemId = itemId, LocationId = fromId, Quantity = 10 });
                database.StockMovements.Add(new StockMovement { ItemId = itemId, IndustryId = industryId, Type = "entrada", Quantity = 10, BalanceAfter = 10, LocationId = fromId });
                await database.SaveChangesAsync();
            }

            using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            var token = loginJson.RootElement.GetProperty("accessToken").GetString()!;
            HttpRequestMessage CreateTransferRequest(string idempotencyKey)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/transfers")
                {
                    Content = JsonContent.Create(new { item_id = itemId, quantity = 7, industry_id = industryId, from_location_id = fromId, to_location_id = toId })
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Add("Idempotency-Key", idempotencyKey);
                return request;
            }

            using var firstRequest = CreateTransferRequest("postgres-transfer-race-001");
            using var secondRequest = CreateTransferRequest("postgres-transfer-race-002");
            var responses = await Task.WhenAll(client.SendAsync(firstRequest), client.SendAsync(secondRequest));
            using var firstResponse = responses[0];
            using var secondResponse = responses[1];
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.UnprocessableEntity));
            var success = responses.Single(response => response.StatusCode == HttpStatusCode.Created);
            using var successJson = JsonDocument.Parse(await success.Content.ReadAsStringAsync());
            var successData = successJson.RootElement.GetProperty("data");
            Assert.Equal(10, successData.GetProperty("stock").GetProperty("on_hand").GetInt32());
            Assert.Equal(2, successData.GetProperty("movements").GetArrayLength());
            var rejection = responses.Single(response => response.StatusCode == HttpStatusCode.UnprocessableEntity);
            using var rejectionJson = JsonDocument.Parse(await rejection.Content.ReadAsStringAsync());
            Assert.Equal("STOCK_INSUFFICIENT", rejectionJson.RootElement.GetProperty("error").GetProperty("code").GetString());

            await using (var verifyScope = factory.Services.CreateAsyncScope())
            {
                var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Equal(3, (await verify.StockPositions.SingleAsync(position => position.ItemId == itemId && position.LocationId == fromId)).Quantity);
                Assert.Equal(7, (await verify.StockPositions.SingleAsync(position => position.ItemId == itemId && position.LocationId == toId)).Quantity);
                var stock = await verify.Stocks.SingleAsync(value => value.ItemId == itemId);
                Assert.Equal(10, stock.OnHand);
                Assert.Equal(0, stock.Reserved);
                var transferMovements = await verify.StockMovements
                    .Where(movement => movement.ItemId == itemId && movement.Type == "transferencia").ToListAsync();
                Assert.Equal(2, transferMovements.Count);
                Assert.Equal(0, transferMovements.Sum(movement => movement.Quantity));
                Assert.Single(transferMovements.Select(movement => movement.TransferId).Distinct());
                Assert.Single(await verify.AuditLogs.Where(log => log.Action == "stock_transfer" && log.EntityId == itemId).ToListAsync());
            }
        }
        finally
        {
            await using var cleanup = factory.Services.CreateAsyncScope();
            var services = cleanup.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            database.AuditLogs.RemoveRange(database.AuditLogs.Where(log => log.EntityId == itemId));
            database.StockMovements.RemoveRange(database.StockMovements.Where(movement => movement.ItemId == itemId));
            database.StockPositions.RemoveRange(database.StockPositions.Where(position => position.ItemId == itemId));
            database.Stocks.RemoveRange(database.Stocks.Where(stock => stock.ItemId == itemId));
            database.CatalogItems.RemoveRange(database.CatalogItems.Where(item => item.Id == itemId));
            database.Categories.RemoveRange(database.Categories.Where(category => category.Id == categoryId));
            database.Industries.RemoveRange(database.Industries.Where(industry => industry.Id == industryId));
            database.Locations.RemoveRange(database.Locations.Where(location => location.Id == fromId || location.Id == toId));
            await database.SaveChangesAsync();
            if (userId is Guid id)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(id.ToString());
                if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync(roleName);
            if (role is not null) Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }

    }

    [PostgreSqlFact]
    public async Task Item_photo_paths_and_audit_are_persisted_and_removed_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-item-photo-{Guid.NewGuid():N}";
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        Guid? userId = null;
        var code = $"PIC-{itemId:N}"[..12];
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                foreach (var permission in new[] { "items.view", "items.manage" })
                    Assert.True((await roles.AddClaimAsync(role, new Claim("permission", permission))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "PostgreSQL item photo" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Categories.Add(new Category { Id = categoryId, Name = $"Photo {categoryId:N}", NameKey = categoryId.ToString("N") });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = code, Name = "PostgreSQL photo", CategoryId = categoryId });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
            using var multipart = new MultipartFormDataContent();
            using var image = new ByteArrayContent(png);
            image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            multipart.Add(image, "photo", "pixel.png");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/items/{itemId}/photo") { Content = multipart };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var uploaded = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
            using var uploadedJson = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
            var photoUrl = uploadedJson.RootElement.GetProperty("photo_url").GetString()!;
            var thumbUrl = uploadedJson.RootElement.GetProperty("thumb_url").GetString()!;

            using var itemList = await SendAsync(client, HttpMethod.Get, $"/api/v1/items?q={Uri.EscapeDataString(code)}&page=1&per_page=1", token);
            using var itemListJson = JsonDocument.Parse(await itemList.Content.ReadAsStringAsync());
            Assert.True(itemListJson.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(itemId, Assert.Single(itemListJson.RootElement.GetProperty("data").EnumerateArray()).GetProperty("id").GetGuid());
            Assert.Equal(1, itemListJson.RootElement.GetProperty("meta").GetProperty("total").GetInt32());
            Assert.Equal(1, itemListJson.RootElement.GetProperty("meta").GetProperty("per_page").GetInt32());

            var root = factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()
                .GetSection("Storage:ItemPhotos:RootPath").Value!;
            var itemRoot = Path.Combine(root, "items", itemId.ToString("N"));
            Assert.Equal(2, Directory.GetFiles(itemRoot, "*.jpg").Length);
            await using (var verifyScope = factory.Services.CreateAsyncScope())
            {
                var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var savedItem = await verify.CatalogItems.SingleAsync(item => item.Id == itemId);
                Assert.NotNull(savedItem.PhotoPath);
                Assert.NotNull(savedItem.ThumbPath);
                var audit = await verify.AuditLogs.SingleAsync(log => log.EntityType == "item" && log.EntityId == itemId && log.Action == "photo_update");
                Assert.Equal(userId, audit.UserId);
            }

            using var full = await SendAsync(client, HttpMethod.Get, photoUrl, token);
            using var thumb = await SendAsync(client, HttpMethod.Get, thumbUrl, token);
            Assert.Equal(HttpStatusCode.OK, full.StatusCode);
            Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
            using var deleted = await SendAsync(client, HttpMethod.Delete, $"/api/v1/items/{itemId}/photo", token);
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            Assert.Empty(Directory.GetFiles(itemRoot, "*.jpg"));

            await using var finalScope = factory.Services.CreateAsyncScope();
            var finalDatabase = finalScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var finalItem = await finalDatabase.CatalogItems.SingleAsync(item => item.Id == itemId);
            Assert.Null(finalItem.PhotoPath);
            Assert.Null(finalItem.ThumbPath);
            var actions = await finalDatabase.AuditLogs.Where(log => log.EntityType == "item" && log.EntityId == itemId)
                .Select(log => log.Action).ToListAsync();
            Assert.Equal(["photo_delete", "photo_update"], actions.OrderBy(action => action));
        }
        finally
        {
            await using var cleanup = factory.Services.CreateAsyncScope();
            var services = cleanup.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            var savedItem = await database.CatalogItems.SingleOrDefaultAsync(item => item.Id == itemId);
            if (savedItem is not null)
            {
                services.GetRequiredService<ItemPhotoStorage>().Delete(savedItem.PhotoPath, savedItem.ThumbPath);
                database.CatalogItems.Remove(savedItem);
            }
            database.AuditLogs.RemoveRange(database.AuditLogs.Where(log => log.EntityId == itemId && log.EntityType == "item"));
            database.Categories.RemoveRange(database.Categories.Where(category => category.Id == categoryId));
            await database.SaveChangesAsync();
            if (userId is Guid id)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(id.ToString());
                if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync(roleName);
            if (role is not null) Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }

    [PostgreSqlFact]
    public async Task Category_purge_removes_trashed_category_and_persists_audit_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-category-purge-{Guid.NewGuid():N}";
        var categoryId = Guid.NewGuid();
        Guid? userId = null;
        var categoryName = $"Purge category {categoryId:N}";
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "lookups.view"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "lookups.delete"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "lookups.purge"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "PostgreSQL category purge" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Categories.Add(new Category { Id = categoryId, Name = categoryName, NameKey = categoryId.ToString("N") });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var deleted = await SendAsync(client, HttpMethod.Delete, $"/api/v1/categories/{categoryId}", token);
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            using var purge = await SendAsync(client, HttpMethod.Post, $"/api/v1/categories/{categoryId}/purge", token,
                new { confirm = categoryName });
            Assert.Equal(HttpStatusCode.OK, purge.StatusCode);

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False(await verify.Categories.AnyAsync(category => category.Id == categoryId));
            var deleteAudit = await verify.AuditLogs.SingleAsync(log => log.EntityId == categoryId && log.EntityType == "category" && log.Action == "delete");
            Assert.Equal(userId, deleteAudit.UserId);
            using var deleteBefore = JsonDocument.Parse(deleteAudit.BeforeJson!);
            Assert.Equal(categoryName, deleteBefore.RootElement.GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, deleteBefore.RootElement.GetProperty("deleted_at").ValueKind);
            Assert.Null(deleteAudit.AfterJson);
            var audit = await verify.AuditLogs.SingleAsync(log => log.EntityId == categoryId && log.EntityType == "category" && log.Action == "purge");
            Assert.Equal(userId, audit.UserId);
            using var before = JsonDocument.Parse(audit.BeforeJson!);
            Assert.Equal(categoryName, before.RootElement.GetProperty("name").GetString());
        }
        finally
        {
            await using var cleanup = factory.Services.CreateAsyncScope();
            var services = cleanup.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            database.AuditLogs.RemoveRange(database.AuditLogs.Where(log => log.EntityId == categoryId && log.EntityType == "category"));
            database.Categories.RemoveRange(database.Categories.Where(category => category.Id == categoryId));
            await database.SaveChangesAsync();
            if (userId is Guid id)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(id.ToString());
                if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync(roleName);
            if (role is not null) Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }

    [PostgreSqlFact]
    public async Task Contact_lookup_purges_lock_remove_and_audit_industry_and_supplier_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-contact-purge-{Guid.NewGuid():N}";
        var industryId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        Guid? userId = null;
        var industryName = $"Industry {industryId:N}";
        var supplierName = $"Supplier {supplierId:N}";
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                foreach (var permission in new[] { "lookups.view", "lookups.delete", "lookups.purge" })
                    Assert.True((await roles.AddClaimAsync(role, new Claim("permission", permission))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "PostgreSQL contact purge" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Industries.Add(new Industry { Id = industryId, Name = industryName, NameKey = industryId.ToString("N") });
                database.Suppliers.Add(new Supplier { Id = supplierId, Name = supplierName, NameKey = supplierId.ToString("N") });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            foreach (var (route, id, name) in new[] { ("industries", industryId, industryName), ("suppliers", supplierId, supplierName) })
            {
                using var deleted = await SendAsync(client, HttpMethod.Delete, $"/api/v1/{route}/{id}", token);
                Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
                await using (var auditScope = factory.Services.CreateAsyncScope())
                {
                    var auditDatabase = auditScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var entityType = route == "industries" ? "industry" : "supplier";
                    var deleteAudit = await auditDatabase.AuditLogs.SingleAsync(log => log.EntityType == entityType
                        && log.EntityId == id && log.Action == "delete");
                    Assert.Equal(userId, deleteAudit.UserId);
                    using var beforeDelete = JsonDocument.Parse(deleteAudit.BeforeJson!);
                    Assert.Equal(name, beforeDelete.RootElement.GetProperty("name").GetString());
                }
                using var purge = await SendAsync(client, HttpMethod.Post, $"/api/v1/{route}/{id}/purge", token, new { confirm = name });
                Assert.True(purge.StatusCode == HttpStatusCode.OK,
                    $"{route} purge returned {(int)purge.StatusCode}: {await purge.Content.ReadAsStringAsync()}");
            }

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False(await verify.Industries.AnyAsync(value => value.Id == industryId));
            Assert.False(await verify.Suppliers.AnyAsync(value => value.Id == supplierId));
            foreach (var (entityType, id) in new[] { ("industry", industryId), ("supplier", supplierId) })
            {
                var audit = await verify.AuditLogs.SingleAsync(log => log.EntityType == entityType && log.EntityId == id && log.Action == "purge");
                Assert.Equal(userId, audit.UserId);
            }
        }
        finally
        {
            await using var cleanup = factory.Services.CreateAsyncScope();
            var services = cleanup.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            database.AuditLogs.RemoveRange(database.AuditLogs.Where(log => log.EntityId == industryId || log.EntityId == supplierId));
            database.Industries.RemoveRange(database.Industries.Where(value => value.Id == industryId));
            database.Suppliers.RemoveRange(database.Suppliers.Where(value => value.Id == supplierId));
            await database.SaveChangesAsync();
            if (userId is Guid id)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(id.ToString());
                if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync(roleName);
            if (role is not null) Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }

    [PostgreSqlFact]
    public async Task Stock_entry_supplier_reference_persists_and_blocks_supplier_purge_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-supplier-stock-{Guid.NewGuid():N}";
        var supplierId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        Guid? userId = null;
        var supplierName = $"Supplier {supplierId:N}";
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                foreach (var permission in new[] { "lookups.view", "lookups.delete", "lookups.purge", "items.view", "items.manage", "stock.entry", "stock.view" })
                    Assert.True((await roles.AddClaimAsync(role, new Claim("permission", permission))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "PostgreSQL supplier stock" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Suppliers.Add(new Supplier { Id = supplierId, Name = supplierName, NameKey = supplierId.ToString("N") });
                database.Categories.Add(new Category { Id = categoryId, Name = $"Category {categoryId:N}", NameKey = categoryId.ToString("N") });
                database.Locations.Add(new Location { Id = locationId, Name = $"Location {locationId:N}", NameKey = locationId.ToString("N"), Kind = "cd" });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"PG-{itemId:N}"[..12], Name = "Supplier entry item", CategoryId = categoryId, SupplierId = supplierId, LocationId = locationId });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var entry = await SendAsync(client, HttpMethod.Post, $"/api/v1/items/{itemId}/entry", token,
                new { quantity = 2, supplier_id = supplierId, idempotency_key = $"pg-supplier-{Guid.NewGuid():N}" });
            Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
            Assert.Equal(supplierId, JsonDocument.Parse(await entry.Content.ReadAsStringAsync()).RootElement.GetProperty("supplier_id").GetGuid());
            using var filtered = await SendAsync(client, HttpMethod.Get,
                $"/api/v1/stock/movements?itemId={itemId}&supplier_id={supplierId}", token);
            Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
            Assert.Equal(1, JsonDocument.Parse(await filtered.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength());

            using var clearedItem = await SendAsync(client, HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { supplierId = (Guid?)null });
            Assert.Equal(HttpStatusCode.OK, clearedItem.StatusCode);
            using var deletedSupplier = await SendAsync(client, HttpMethod.Delete, $"/api/v1/suppliers/{supplierId}", token);
            Assert.Equal(HttpStatusCode.OK, deletedSupplier.StatusCode);
            using var blockedPurge = await SendAsync(client, HttpMethod.Post, $"/api/v1/suppliers/{supplierId}/purge", token, new { confirm = supplierName });
            Assert.Equal(HttpStatusCode.Conflict, blockedPurge.StatusCode);

            await using (var removeMovementScope = factory.Services.CreateAsyncScope())
            {
                var database = removeMovementScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                database.StockMovements.RemoveRange(database.StockMovements.Where(movement => movement.ItemId == itemId));
                await database.SaveChangesAsync();
            }
            using var purgedSupplier = await SendAsync(client, HttpMethod.Post, $"/api/v1/suppliers/{supplierId}/purge", token, new { confirm = supplierName });
            Assert.Equal(HttpStatusCode.OK, purgedSupplier.StatusCode);
            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False(await verify.Suppliers.AnyAsync(supplier => supplier.Id == supplierId));
            Assert.Equal(userId, (await verify.AuditLogs.SingleAsync(log => log.EntityType == "supplier" && log.EntityId == supplierId && log.Action == "purge")).UserId);
        }
        finally
        {
            await using var cleanup = factory.Services.CreateAsyncScope();
            var database = cleanup.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.AuditLogs.RemoveRange(database.AuditLogs.Where(log => log.EntityId == supplierId || log.EntityId == itemId));
            database.StockPositions.RemoveRange(database.StockPositions.Where(position => position.ItemId == itemId));
            database.StockMovements.RemoveRange(database.StockMovements.Where(movement => movement.ItemId == itemId));
            database.Stocks.RemoveRange(database.Stocks.Where(stock => stock.ItemId == itemId));
            database.CatalogItems.RemoveRange(database.CatalogItems.Where(item => item.Id == itemId));
            database.Categories.RemoveRange(database.Categories.Where(category => category.Id == categoryId));
            database.Locations.RemoveRange(database.Locations.Where(location => location.Id == locationId));
            database.Suppliers.RemoveRange(database.Suppliers.Where(supplier => supplier.Id == supplierId));
            await database.SaveChangesAsync();
            if (userId is Guid id)
            {
                var users = cleanup.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(id.ToString());
                if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = cleanup.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync(roleName);
            if (role is not null) Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }

    [PostgreSqlFact]
    public async Task Location_purge_respects_catalog_references_and_audits_removal_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-location-purge-{Guid.NewGuid():N}";
        var locationId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        Guid? userId = null;
        var name = $"Location {locationId:N}";
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "lookups.delete"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "lookups.purge"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "PostgreSQL location purge" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Locations.Add(new Location { Id = locationId, Name = name, NameKey = locationId.ToString("N"), Kind = "cd" });
                database.Categories.Add(new Category { Id = categoryId, Name = $"Location {categoryId:N}", NameKey = categoryId.ToString("N") });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"PGL-{itemId:N}"[..12], Name = "Brinde referenciado", CategoryId = categoryId, LocationId = locationId });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var deleted = await SendAsync(client, HttpMethod.Delete, $"/api/v1/locations/{locationId}", token);
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            using var blocked = await SendAsync(client, HttpMethod.Post, $"/api/v1/locations/{locationId}/purge", token, new { confirm = name });
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            Assert.Contains("IN_USE", await blocked.Content.ReadAsStringAsync());

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                database.CatalogItems.RemoveRange(database.CatalogItems.Where(item => item.Id == itemId));
                await database.SaveChangesAsync();
            }
            using var purge = await SendAsync(client, HttpMethod.Post, $"/api/v1/locations/{locationId}/purge", token, new { confirm = name });
            Assert.Equal(HttpStatusCode.OK, purge.StatusCode);
            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False(await verify.Locations.AnyAsync(location => location.Id == locationId));
            var audit = await verify.AuditLogs.SingleAsync(log => log.EntityType == "location" && log.EntityId == locationId && log.Action == "purge");
            Assert.Equal(userId, audit.UserId);
        }
        finally
        {
            await using var cleanup = factory.Services.CreateAsyncScope();
            var services = cleanup.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            database.AuditLogs.RemoveRange(database.AuditLogs.Where(log => log.EntityId == locationId));
            database.StockPositions.RemoveRange(database.StockPositions.Where(position => position.LocationId == locationId));
            database.StockMovements.RemoveRange(database.StockMovements.Where(movement => movement.LocationId == locationId || movement.FromLocationId == locationId || movement.ToLocationId == locationId));
            database.CatalogItems.RemoveRange(database.CatalogItems.Where(item => item.Id == itemId));
            database.Categories.RemoveRange(database.Categories.Where(category => category.Id == categoryId));
            database.Locations.RemoveRange(database.Locations.Where(location => location.Id == locationId));
            await database.SaveChangesAsync();
            if (userId is Guid id)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(id.ToString());
                if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync(roleName);
            if (role is not null) Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }

    [PostgreSqlFact]
    public async Task Department_purge_persists_removal_and_audit_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-department-purge-{Guid.NewGuid():N}";
        var departmentId = Guid.NewGuid();
        Guid? userId = null;
        var name = $"Department {departmentId:N}";
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "lookups.view"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "lookups.delete"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "lookups.purge"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "PostgreSQL department purge" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Departments.Add(new Department { Id = departmentId, Name = name, NameKey = departmentId.ToString("N") });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var deleted = await SendAsync(client, HttpMethod.Delete, $"/api/v1/departments/{departmentId}", token);
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            await using (var auditScope = factory.Services.CreateAsyncScope())
            {
                var auditDatabase = auditScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var deleteAudit = await auditDatabase.AuditLogs.SingleAsync(log => log.EntityType == "department"
                    && log.EntityId == departmentId && log.Action == "delete");
                Assert.Equal(userId, deleteAudit.UserId);
                using var beforeDelete = JsonDocument.Parse(deleteAudit.BeforeJson!);
                Assert.Equal(name, beforeDelete.RootElement.GetProperty("name").GetString());
            }
            using var purge = await SendAsync(client, HttpMethod.Post, $"/api/v1/departments/{departmentId}/purge", token,
                new { confirm = name });
            Assert.Equal(HttpStatusCode.OK, purge.StatusCode);

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False(await verify.Departments.AnyAsync(department => department.Id == departmentId));
            var audit = await verify.AuditLogs.SingleAsync(log => log.EntityId == departmentId && log.EntityType == "department" && log.Action == "purge");
            Assert.Equal(userId, audit.UserId);
            using var before = JsonDocument.Parse(audit.BeforeJson!);
            Assert.Equal(name, before.RootElement.GetProperty("name").GetString());
        }
        finally
        {
            await using var cleanup = factory.Services.CreateAsyncScope();
            var services = cleanup.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            database.AuditLogs.RemoveRange(database.AuditLogs.Where(log => log.EntityId == departmentId && log.EntityType == "department"));
            database.Departments.RemoveRange(database.Departments.Where(department => department.Id == departmentId));
            await database.SaveChangesAsync();
            if (userId is Guid id)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(id.ToString());
                if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync(roleName);
            if (role is not null) Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }

    [PostgreSqlFact]
    public async Task Stock_movement_department_reference_is_persisted_filters_history_and_blocks_department_purge_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-department-movement-{Guid.NewGuid():N}";
        var departmentId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var movementId = Guid.NewGuid();
        Guid? userId = null;
        var name = $"Department {departmentId:N}";
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                foreach (var permission in new[] { "lookups.view", "lookups.delete", "lookups.purge", "stock.view" })
                    Assert.True((await roles.AddClaimAsync(role, new Claim("permission", permission))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "PostgreSQL department movement" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Departments.Add(new Department { Id = departmentId, Name = name, NameKey = departmentId.ToString("N"), DeletedAt = DateTimeOffset.UtcNow });
                database.Categories.Add(new Category { Id = categoryId, Name = $"Category {categoryId:N}", NameKey = categoryId.ToString("N") });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"PG-{itemId:N}"[..12], Name = "Department movement item", CategoryId = categoryId });
                database.StockMovements.Add(new StockMovement
                {
                    Id = movementId,
                    ItemId = itemId,
                    DepartmentId = departmentId,
                    Type = "saida",
                    Quantity = -1,
                    BalanceAfter = 0
                });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var filtered = await SendAsync(client, HttpMethod.Get, $"/api/v1/stock/movements?department_id={departmentId}", token);
            Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
            using var filteredJson = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
            var movements = filteredJson.RootElement.GetProperty("items");
            Assert.Single(movements.EnumerateArray());
            Assert.Equal(departmentId, movements[0].GetProperty("department_id").GetGuid());

            using var purge = await SendAsync(client, HttpMethod.Post, $"/api/v1/departments/{departmentId}/purge", token,
                new { confirm = name });
            Assert.Equal(HttpStatusCode.Conflict, purge.StatusCode);
            Assert.Contains("IN_USE", await purge.Content.ReadAsStringAsync());
            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.True(await verify.Departments.AnyAsync(department => department.Id == departmentId));
            Assert.Empty(await verify.AuditLogs.Where(log => log.EntityType == "department" && log.EntityId == departmentId && log.Action == "purge").ToListAsync());
        }
        finally
        {
            await using var cleanup = factory.Services.CreateAsyncScope();
            var services = cleanup.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            database.AuditLogs.RemoveRange(database.AuditLogs.Where(log => log.EntityId == departmentId || log.EntityId == itemId));
            database.StockMovements.RemoveRange(database.StockMovements.Where(movement => movement.Id == movementId));
            database.CatalogItems.RemoveRange(database.CatalogItems.Where(item => item.Id == itemId));
            database.Categories.RemoveRange(database.Categories.Where(category => category.Id == categoryId));
            database.Departments.RemoveRange(database.Departments.Where(department => department.Id == departmentId));
            await database.SaveChangesAsync();
            if (userId is Guid id)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(id.ToString());
                if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync(roleName);
            if (role is not null) Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }

    [PostgreSqlFact]
    public async Task Industry_purge_is_blocked_by_event_allocations_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-event-purge-{Guid.NewGuid():N}";
        var industryId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        Guid? userId = null;
        var name = $"Event industry {industryId:N}";

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                foreach (var permission in new[] { "lookups.purge" })
                    Assert.True((await roles.AddClaimAsync(role, new Claim("permission", permission))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "PostgreSQL event purge" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Industries.Add(new Industry { Id = industryId, Name = name, NameKey = industryId.ToString("N"), DeletedAt = DateTimeOffset.UtcNow });
                database.Categories.Add(new Category { Id = categoryId, Name = $"Event category {categoryId:N}", NameKey = categoryId.ToString("N") });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"PG-{itemId:N}"[..12], Name = "Event allocated item", CategoryId = categoryId });
                database.Events.Add(new Event { Id = eventId, Name = $"Event {eventId:N}" });
                database.EventAllocations.Add(new EventAllocation { EventId = eventId, IndustryId = industryId, ItemId = itemId, QuantityAllocated = 3 });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var purge = await SendAsync(client, HttpMethod.Post, $"/api/v1/industries/{industryId}/purge", token, new { confirm = name });
            Assert.Equal(HttpStatusCode.Conflict, purge.StatusCode);
            Assert.Contains("IN_USE", await purge.Content.ReadAsStringAsync());
            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.True(await verify.Industries.AnyAsync(industry => industry.Id == industryId));
            Assert.True(await verify.EventAllocations.AnyAsync(allocation => allocation.EventId == eventId));
            Assert.Empty(await verify.AuditLogs.Where(log => log.EntityType == "industry" && log.EntityId == industryId && log.Action == "purge").ToListAsync());
        }
        finally
        {
            await using var cleanup = factory.Services.CreateAsyncScope();
            var services = cleanup.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            database.AuditLogs.RemoveRange(database.AuditLogs.Where(log => log.EntityId == industryId));
            database.EventAllocations.RemoveRange(database.EventAllocations.Where(allocation => allocation.EventId == eventId));
            database.Events.RemoveRange(database.Events.Where(eventRecord => eventRecord.Id == eventId));
            database.CatalogItems.RemoveRange(database.CatalogItems.Where(item => item.Id == itemId));
            database.Categories.RemoveRange(database.Categories.Where(category => category.Id == categoryId));
            database.Industries.RemoveRange(database.Industries.Where(industry => industry.Id == industryId));
            await database.SaveChangesAsync();
            if (userId is Guid id)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(id.ToString());
                if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roleManager = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var createdRole = await roleManager.FindByNameAsync(roleName);
            if (createdRole is not null) Assert.True((await roleManager.DeleteAsync(createdRole)).Succeeded);
        }
    }

    [PostgreSqlFact]
    public async Task Industry_purge_is_blocked_by_delivery_protocols_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-delivery-purge-{Guid.NewGuid():N}";
        var industryId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        Guid? userId = null;
        var name = $"Delivered industry {industryId:N}";
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "lookups.purge"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "PostgreSQL delivery purge" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Industries.Add(new Industry { Id = industryId, Name = name, NameKey = industryId.ToString("N"), DeletedAt = DateTimeOffset.UtcNow });
                database.Requests.Add(new InternalRequest
                {
                    Id = requestId,
                    Code = $"REQ-{requestId:N}"[..24],
                    RequesterId = user.Id,
                    Purpose = "Protocolo de entrega"
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
                    IdempotencyKeyHash = Guid.NewGuid().ToString("N").PadLeft(64, '0'),
                    RequestHash = Guid.NewGuid().ToString("N").PadLeft(64, '0')
                });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var purge = await SendAsync(client, HttpMethod.Post, $"/api/v1/industries/{industryId}/purge", token, new { confirm = name });
            Assert.Equal(HttpStatusCode.Conflict, purge.StatusCode);
            Assert.Contains("IN_USE", await purge.Content.ReadAsStringAsync());
            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.True(await verify.Industries.AnyAsync(industry => industry.Id == industryId));
            Assert.True(await verify.Deliveries.AnyAsync(delivery => delivery.Id == deliveryId && delivery.IndustryId == industryId));
            Assert.Empty(await verify.AuditLogs.Where(log => log.EntityType == "industry" && log.EntityId == industryId && log.Action == "purge").ToListAsync());
        }
        finally
        {
            await using var cleanup = factory.Services.CreateAsyncScope();
            var services = cleanup.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            database.AuditLogs.RemoveRange(database.AuditLogs.Where(log => log.EntityId == industryId));
            database.Deliveries.RemoveRange(database.Deliveries.Where(delivery => delivery.Id == deliveryId));
            database.Requests.RemoveRange(database.Requests.Where(request => request.Id == requestId));
            database.Industries.RemoveRange(database.Industries.Where(industry => industry.Id == industryId));
            await database.SaveChangesAsync();
            if (userId is Guid id)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(id.ToString());
                if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var createdRole = await roles.FindByNameAsync(roleName);
            if (createdRole is not null) Assert.True((await roles.DeleteAsync(createdRole)).Succeeded);
        }
    }

    [PostgreSqlFact]
    public async Task Exit_cancellation_releases_reservation_and_persists_audit_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-exit-cancel-{Guid.NewGuid():N}";
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        Guid? userId = null;
        Guid? orderId = null;
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "stock.exit"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "PostgreSQL exit cancellation" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;
                database.Categories.Add(new Category { Id = categoryId, Name = $"Exit cancellation {categoryId:N}", NameKey = categoryId.ToString("N") });
                database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"PC-{itemId:N}"[..12], Name = "Cancelável", CategoryId = categoryId });
                database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 7 });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/exits")
            {
                Content = JsonContent.Create(new { item_id = itemId, quantity = 3, purpose = "Teste PostgreSQL" })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Idempotency-Key", $"pg-exit-{Guid.NewGuid():N}");
            using var created = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            orderId = createdJson.RootElement.GetProperty("data").GetProperty("order").GetProperty("id").GetGuid();

            using var cancel = await SendAsync(client, HttpMethod.Post, $"/api/v1/stock/exit-orders/{orderId}/cancel", token,
                new { reason = "Solicitação retirada" });
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stock = await verify.Stocks.SingleAsync(value => value.ItemId == itemId);
            Assert.Equal(7, stock.OnHand);
            Assert.Equal(0, stock.Reserved);
            var order = await verify.StockExitOrders.SingleAsync(value => value.Id == orderId);
            Assert.Equal("cancelada", order.Status);
            var audit = await verify.AuditLogs.SingleAsync(log => log.Action == "stock_exit_cancel" && log.EntityId == orderId);
            Assert.Equal(userId, audit.UserId);
            using var before = JsonDocument.Parse(audit.BeforeJson!);
            using var after = JsonDocument.Parse(audit.AfterJson!);
            Assert.Equal("autorizada", before.RootElement.GetProperty("status").GetString());
            Assert.Equal("cancelada", after.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            await using var cleanup = factory.Services.CreateAsyncScope();
            var services = cleanup.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            if (orderId is Guid exitId)
            {
                database.AuditLogs.RemoveRange(database.AuditLogs.Where(log => log.EntityId == exitId));
                database.StockExitOrders.RemoveRange(database.StockExitOrders.Where(order => order.Id == exitId));
            }
            database.StockMovements.RemoveRange(database.StockMovements.Where(movement => movement.ItemId == itemId));
            database.Stocks.RemoveRange(database.Stocks.Where(stock => stock.ItemId == itemId));
            database.CatalogItems.RemoveRange(database.CatalogItems.Where(item => item.Id == itemId));
            database.Categories.RemoveRange(database.Categories.Where(category => category.Id == categoryId));
            await database.SaveChangesAsync();
            if (userId is Guid id)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(id.ToString());
                if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync(roleName);
            if (role is not null) Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }

    [PostgreSqlFact]
    public async Task Request_create_detail_and_draft_edit_use_postgres_persistence_and_row_locking()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-request-{Guid.NewGuid():N}";
        Guid? userId = null;
        Guid? categoryId = null;
        Guid? itemId = null;
        Guid? requestId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "requests.create"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "requests.view_own"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "requests.process"))).Succeeded);
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = "PostgreSQL request test"
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;

                var category = new Category
                {
                    Name = $"PostgreSQL-{Guid.NewGuid():N}",
                    NameKey = Guid.NewGuid().ToString("N")
                };
                var item = new CatalogItem
                {
                    Code = $"PG-{Guid.NewGuid():N}"[..12],
                    Name = "Caneca PostgreSQL",
                    CategoryId = category.Id,
                    UnitValue = 4.17m
                };
                database.Categories.Add(category);
                database.CatalogItems.Add(item);
                await database.SaveChangesAsync();
                categoryId = category.Id;
                itemId = item.Id;
            }

            using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
            {
                email,
                password = "CorrectPassword1"
            });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            var token = loginJson.RootElement.GetProperty("accessToken").GetString()!;

            using var created = await SendAsync(client, HttpMethod.Post, "/api/v1/requests", token, new
            {
                purpose = "Validação PostgreSQL",
                items = new[] { new { item_id = itemId, qty_requested = 2 } }
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            requestId = createdJson.RootElement.GetProperty("data").GetProperty("id").GetGuid();
            await using (var createScope = factory.Services.CreateAsyncScope())
            {
                var createDatabase = createScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var createAudit = await createDatabase.AuditLogs.SingleAsync(log => log.EntityType == "request"
                    && log.EntityId == requestId && log.Action == "create");
                Assert.Equal(userId, createAudit.UserId);
                Assert.Null(createAudit.BeforeJson);
                using var createAfter = JsonDocument.Parse(createAudit.AfterJson!);
                Assert.Equal(createdJson.RootElement.GetProperty("data").GetProperty("code").GetString(),
                    createAfter.RootElement.GetProperty("code").GetString());
                Assert.Equal("Validação PostgreSQL", createAfter.RootElement.GetProperty("purpose").GetString());
            }

            using var detail = await SendAsync(client, HttpMethod.Get, $"/api/v1/requests/{requestId}", token);
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            Assert.Equal("Validação PostgreSQL", detailJson.RootElement.GetProperty("data").GetProperty("purpose").GetString());

            using var updated = await SendAsync(client, HttpMethod.Put, $"/api/v1/requests/{requestId}", token, new
            {
                purpose = "Rascunho PostgreSQL atualizado",
                items = new[] { new { item_id = itemId, qty_requested = 5 } }
            });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            using var updatedJson = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
            Assert.Equal(20.85m, updatedJson.RootElement.GetProperty("data").GetProperty("total_value").GetDecimal());

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var saved = await verifyDatabase.Requests
                .Include(request => request.Items)
                .SingleAsync(request => request.Id == requestId);
            Assert.Equal("Rascunho PostgreSQL atualizado", saved.Purpose);
            Assert.Equal(5, Assert.Single(saved.Items).QuantityRequested);
            Assert.Equal(20.85m, saved.TotalValue);
            var editAudit = await verifyDatabase.AuditLogs.SingleAsync(log => log.EntityType == "request"
                && log.EntityId == requestId && log.Action == "update");
            using var editBefore = JsonDocument.Parse(editAudit.BeforeJson!);
            using var editAfter = JsonDocument.Parse(editAudit.AfterJson!);
            Assert.Equal("Validação PostgreSQL", editBefore.RootElement.GetProperty("purpose").GetString());
            Assert.Equal(8.34m, editBefore.RootElement.GetProperty("total_value").GetDecimal());
            Assert.Equal("Rascunho PostgreSQL atualizado", editAfter.RootElement.GetProperty("purpose").GetString());
            Assert.Equal(20.85m, editAfter.RootElement.GetProperty("total_value").GetDecimal());

            var persistedItem = await verifyDatabase.CatalogItems.SingleAsync(item => item.Id == itemId);
            verifyDatabase.Stocks.Add(new InventoryStock { ItemId = itemId.Value, Item = persistedItem, OnHand = 1 });
            await verifyDatabase.SaveChangesAsync();

            using var submitted = await SendAsync(client, HttpMethod.Post, $"/api/v1/requests/{requestId}/submit", token);
            Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
            using var submittedJson = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());
            var submittedData = submittedJson.RootElement.GetProperty("data");
            Assert.Equal("aprovada", submittedData.GetProperty("status").GetString());
            Assert.True(submittedData.GetProperty("needs_purchase").GetBoolean());
            Assert.Equal(5, submittedData.GetProperty("items")[0].GetProperty("qty_approved").GetInt32());
            Assert.Equal(1, submittedData.GetProperty("items")[0].GetProperty("qty_reserved").GetInt32());
            var submitAudit = await verifyDatabase.AuditLogs.SingleAsync(log => log.EntityType == "request"
                && log.EntityId == requestId && log.Action == "submit");
            Assert.Equal(userId, submitAudit.UserId);
            using var submitBefore = JsonDocument.Parse(submitAudit.BeforeJson!);
            using var submitAfter = JsonDocument.Parse(submitAudit.AfterJson!);
            Assert.Equal("rascunho", submitBefore.RootElement.GetProperty("status").GetString());
            Assert.Equal("aprovada", submitAfter.RootElement.GetProperty("status").GetString());

            using var picking = await SendAsync(client, HttpMethod.Post, $"/api/v1/requests/{requestId}/start-picking", token);
            Assert.Equal(HttpStatusCode.OK, picking.StatusCode);
            using var ready = await SendAsync(client, HttpMethod.Post, $"/api/v1/requests/{requestId}/ready", token);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            using var readyJson = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
            Assert.Equal("pronta", readyJson.RootElement.GetProperty("data").GetProperty("status").GetString());
            var pickingAudit = await verifyDatabase.AuditLogs.SingleAsync(log => log.EntityType == "request"
                && log.EntityId == requestId && log.Action == "update" && log.AfterJson == "{\"status\":\"em_separacao\"}");
            var readyAudit = await verifyDatabase.AuditLogs.SingleAsync(log => log.EntityType == "request"
                && log.EntityId == requestId && log.Action == "update" && log.AfterJson == "{\"status\":\"pronta\"}");
            using var pickingBefore = JsonDocument.Parse(pickingAudit.BeforeJson!);
            using var readyBefore = JsonDocument.Parse(readyAudit.BeforeJson!);
            Assert.Equal("aprovada", pickingBefore.RootElement.GetProperty("status").GetString());
            Assert.Equal("em_separacao", readyBefore.RootElement.GetProperty("status").GetString());

            verifyDatabase.ChangeTracker.Clear();
            var submittedSaved = await verifyDatabase.Requests.Include(request => request.Items)
                .SingleAsync(request => request.Id == requestId);
            Assert.Equal("pronta", submittedSaved.Status);
            Assert.Equal(1, (await verifyDatabase.Stocks.SingleAsync(stock => stock.ItemId == itemId)).Reserved);

            using var listed = await SendAsync(client, HttpMethod.Get,
                "/api/v1/requests?q=Rascunho%20PostgreSQL%20atualizado&status=pronta", token);
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            using var listedJson = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
            Assert.Equal(1, listedJson.RootElement.GetProperty("meta").GetProperty("total").GetInt32());
            Assert.Equal("Pronto para retirada", listedJson.RootElement.GetProperty("data")[0].GetProperty("status_label").GetString());

            using var cancelled = await SendAsync(client, HttpMethod.Post, $"/api/v1/requests/{requestId}/cancel", token,
                new { reason = "Cancelamento PostgreSQL." });
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
            using var cancelledJson = JsonDocument.Parse(await cancelled.Content.ReadAsStringAsync());
            var cancelledData = cancelledJson.RootElement.GetProperty("data");
            Assert.Equal("cancelada", cancelledData.GetProperty("status").GetString());
            Assert.Equal("Cancelamento PostgreSQL.", cancelledData.GetProperty("cancel_reason").GetString());
            Assert.NotEqual(JsonValueKind.Null, cancelledData.GetProperty("cancelled_at").ValueKind);
            verifyDatabase.ChangeTracker.Clear();
            var cancelledSaved = await verifyDatabase.Requests.Include(request => request.Items).Include(request => request.History)
                .SingleAsync(request => request.Id == requestId);
            Assert.Equal(0, cancelledSaved.Items.Single().QuantityReserved);
            Assert.Equal("Cancelamento PostgreSQL.", cancelledSaved.History.OrderBy(history => history.CreatedAt).Last().Comment);
            Assert.Equal(0, (await verifyDatabase.Stocks.SingleAsync(stock => stock.ItemId == itemId)).Reserved);
            var cancelAudit = await verifyDatabase.AuditLogs.SingleAsync(log => log.EntityType == "request"
                && log.EntityId == requestId && log.Action == "cancel");
            Assert.Equal(userId, cancelAudit.UserId);
            using var cancelBefore = JsonDocument.Parse(cancelAudit.BeforeJson!);
            using var cancelAfter = JsonDocument.Parse(cancelAudit.AfterJson!);
            Assert.Equal("pronta", cancelBefore.RootElement.GetProperty("status").GetString());
            Assert.Equal("cancelada", cancelAfter.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            await CleanupAsync(roleName, userId, categoryId, itemId, requestId);
        }
    }

    [PostgreSqlFact]
    public async Task Approval_rule_submission_and_assigned_decision_persist_and_reserve_stock_in_postgres()
    {
        var client = factory.CreateClient();
        var requesterEmail = $"{Guid.NewGuid():N}@example.invalid";
        var approverEmail = $"{Guid.NewGuid():N}@example.invalid";
        var requesterRoleName = $"pg-requester-{Guid.NewGuid():N}";
        var approverRoleName = $"pg-approver-{Guid.NewGuid():N}";
        Guid? requesterId = null;
        Guid? approverId = null;
        Guid? categoryId = null;
        Guid? itemId = null;
        Guid? ruleId = null;
        Guid? requestId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var requesterRole = new ApplicationRole(requesterRoleName);
                Assert.True((await roles.CreateAsync(requesterRole)).Succeeded);
                Assert.True((await roles.AddClaimAsync(requesterRole, new Claim("permission", "requests.create"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(requesterRole, new Claim("permission", "requests.view_own"))).Succeeded);
                var approverRole = new ApplicationRole(approverRoleName);
                Assert.True((await roles.CreateAsync(approverRole)).Succeeded);
                Assert.True((await roles.AddClaimAsync(approverRole, new Claim("permission", "requests.approve"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(approverRole, new Claim("permission", "requests.process"))).Succeeded);
                var requester = new ApplicationUser { UserName = requesterEmail, Email = requesterEmail, EmailConfirmed = true, FullName = "PG requester" };
                var approver = new ApplicationUser { UserName = approverEmail, Email = approverEmail, EmailConfirmed = true, FullName = "PG approver" };
                Assert.True((await users.CreateAsync(requester, "CorrectPassword1")).Succeeded);
                Assert.True((await users.CreateAsync(approver, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(requester, requesterRoleName)).Succeeded);
                Assert.True((await users.AddToRoleAsync(approver, approverRoleName)).Succeeded);
                requesterId = requester.Id;
                approverId = approver.Id;

                var category = new Category { Name = $"PG-Approval-{Guid.NewGuid():N}", NameKey = Guid.NewGuid().ToString("N") };
                var item = new CatalogItem { Code = $"PGA-{Guid.NewGuid():N}"[..12], Name = "Approval cup", CategoryId = category.Id, UnitValue = 4.17m };
                database.Categories.Add(category);
                database.CatalogItems.Add(item);
                database.ApprovalRules.Add(new ApprovalRule
                {
                    Name = "PG quantity approval",
                    Criterion = "quantidade",
                    Operator = ">=",
                    Value = "2",
                    ApproverRoleId = approverRole.Id
                });
                database.Stocks.Add(new InventoryStock { ItemId = item.Id, OnHand = 3 });
                await database.SaveChangesAsync();
                categoryId = category.Id;
                itemId = item.Id;
                ruleId = database.ApprovalRules.Local.Single().Id;
            }

            var requesterToken = await LoginAsync(client, requesterEmail);
            var approverToken = await LoginAsync(client, approverEmail);
            using var created = await SendAsync(client, HttpMethod.Post, "/api/v1/requests", requesterToken, new
            {
                purpose = "Approval with PostgreSQL",
                items = new[] { new { item_id = itemId, qty_requested = 2 } }
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            requestId = createdJson.RootElement.GetProperty("data").GetProperty("id").GetGuid();

            using var submitted = await SendAsync(client, HttpMethod.Post, $"/api/v1/requests/{requestId}/submit", requesterToken);
            Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
            using var submittedJson = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());
            Assert.Equal("aguardando_aprovacao", submittedJson.RootElement.GetProperty("data").GetProperty("status").GetString());
            await using (var notificationScope = factory.Services.CreateAsyncScope())
            {
                var notificationDatabase = notificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var submissionNotification = await notificationDatabase.Notifications.SingleAsync(notification =>
                    notification.UserId == approverId && notification.RelatedId == requestId
                    && notification.RelatedType == "request" && notification.Channel == "in_app"
                    && notification.Subject == "Nova solicitação aguardando aprovação");
                Assert.Equal("Nova solicitação aguardando aprovação", submissionNotification.Subject);
                Assert.Equal($"A solicitação {createdJson.RootElement.GetProperty("data").GetProperty("code").GetString()} precisa da sua aprovação.",
                    submissionNotification.Body);
                var processNotification = await notificationDatabase.Notifications.SingleAsync(notification =>
                    notification.UserId == approverId && notification.RelatedId == requestId
                    && notification.RelatedType == "request" && notification.Channel == "in_app"
                    && notification.Subject == "Nova solicitação");
                Assert.Equal($"A solicitação {createdJson.RootElement.GetProperty("data").GetProperty("code").GetString()} foi enviada.",
                    processNotification.Body);
            }
            Assert.Equal(0, (await GetStockAsync(factory, itemId.Value)).Reserved);

            using var approverList = await SendAsync(client, HttpMethod.Get, "/api/v1/requests", approverToken);
            Assert.Equal(HttpStatusCode.OK, approverList.StatusCode);
            using var approverListJson = JsonDocument.Parse(await approverList.Content.ReadAsStringAsync());
            Assert.Equal(1, approverListJson.RootElement.GetProperty("meta").GetProperty("total").GetInt32());
            Assert.Equal(requestId, approverListJson.RootElement.GetProperty("data")[0].GetProperty("id").GetGuid());

            using var approverDetail = await SendAsync(client, HttpMethod.Get, $"/api/v1/requests/{requestId}", approverToken);
            Assert.Equal(HttpStatusCode.OK, approverDetail.StatusCode);

            using var approved = await SendAsync(client, HttpMethod.Post, $"/api/v1/requests/{requestId}/approve", approverToken,
                new
                {
                    justification = "PostgreSQL approval verified.",
                    items = new[] { new { item_id = itemId, qty_approved = 1 } }
                });
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
            using var approvedJson = JsonDocument.Parse(await approved.Content.ReadAsStringAsync());
            Assert.Equal("aprovada", approvedJson.RootElement.GetProperty("data").GetProperty("status").GetString());
            using var requesterInbox = await SendAsync(client, HttpMethod.Get, "/api/v1/notifications", requesterToken);
            Assert.Equal(HttpStatusCode.OK, requesterInbox.StatusCode);
            using var requesterInboxJson = JsonDocument.Parse(await requesterInbox.Content.ReadAsStringAsync());
            var inboxMeta = requesterInboxJson.RootElement.GetProperty("meta");
            Assert.Equal(1, inboxMeta.GetProperty("page").GetInt32());
            Assert.Equal(25, inboxMeta.GetProperty("per_page").GetInt32());
            Assert.Equal(1, inboxMeta.GetProperty("total").GetInt32());
            Assert.Equal(1, inboxMeta.GetProperty("last_page").GetInt32());
            Assert.Equal(1, inboxMeta.GetProperty("unread").GetInt32());
            var notification = requesterInboxJson.RootElement.GetProperty("data").EnumerateArray()
                .Single(entry => entry.GetProperty("related_id").GetGuid() == requestId);
            var notificationId = notification.GetProperty("id").GetGuid();
            Assert.False(notification.TryGetProperty("channel", out _));
            Assert.Contains("aprovada", notification.GetProperty("subject").GetString(), StringComparison.OrdinalIgnoreCase);
            using var approverInbox = await SendAsync(client, HttpMethod.Get, "/api/v1/notifications", approverToken);
            Assert.Equal(HttpStatusCode.OK, approverInbox.StatusCode);
            using var approverInboxJson = JsonDocument.Parse(await approverInbox.Content.ReadAsStringAsync());
            Assert.DoesNotContain(approverInboxJson.RootElement.GetProperty("data").EnumerateArray(),
                entry => entry.GetProperty("id").GetGuid() == notificationId);
            using var markedRead = await SendAsync(client, HttpMethod.Post,
                $"/api/v1/notifications/{notificationId}/read", requesterToken);
            Assert.Equal(HttpStatusCode.OK, markedRead.StatusCode);
            using var unreadInbox = await SendAsync(client, HttpMethod.Get, "/api/v1/notifications?unread=true", requesterToken);
            Assert.Equal(HttpStatusCode.OK, unreadInbox.StatusCode);
            using var unreadInboxJson = JsonDocument.Parse(await unreadInbox.Content.ReadAsStringAsync());
            Assert.DoesNotContain(unreadInboxJson.RootElement.GetProperty("data").EnumerateArray(),
                entry => entry.GetProperty("related_id").GetGuid() == requestId);
            Assert.Equal(0, unreadInboxJson.RootElement.GetProperty("meta").GetProperty("unread").GetInt32());
            await using (var unreadScope = factory.Services.CreateAsyncScope())
            {
                var unreadDatabase = unreadScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var notificationEntity = await unreadDatabase.Notifications.SingleAsync(entry => entry.Id == notificationId);
                notificationEntity.ReadAt = null;
                await unreadDatabase.SaveChangesAsync();
            }
            using var markedAll = await SendAsync(client, HttpMethod.Post, "/api/v1/notifications/read-all", requesterToken);
            Assert.Equal(HttpStatusCode.OK, markedAll.StatusCode);
            using var unreadAfterAll = await SendAsync(client, HttpMethod.Get, "/api/v1/notifications?unread=true", requesterToken);
            Assert.Equal(HttpStatusCode.OK, unreadAfterAll.StatusCode);
            using var unreadAfterAllJson = JsonDocument.Parse(await unreadAfterAll.Content.ReadAsStringAsync());
            Assert.DoesNotContain(unreadAfterAllJson.RootElement.GetProperty("data").EnumerateArray(),
                entry => entry.GetProperty("related_id").GetGuid() == requestId);
            Assert.Equal(0, unreadAfterAllJson.RootElement.GetProperty("meta").GetProperty("unread").GetInt32());
            using var started = await SendAsync(client, HttpMethod.Post, $"/api/v1/requests/{requestId}/start-picking", approverToken);
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
            using var ready = await SendAsync(client, HttpMethod.Post, $"/api/v1/requests/{requestId}/ready", approverToken);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            var requestCode = createdJson.RootElement.GetProperty("data").GetProperty("code").GetString();
            await using (var readyNotificationScope = factory.Services.CreateAsyncScope())
            {
                var readyNotificationDatabase = readyNotificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var readyNotification = await readyNotificationDatabase.Notifications.SingleAsync(entry =>
                    entry.UserId == requesterId && entry.RelatedType == "request" && entry.RelatedId == requestId
                    && entry.Subject == $"Brindes prontos para retirada — {requestCode}");
                Assert.Equal($"A solicitação {requestCode} está pronta para retirada/entrega.", readyNotification.Body);
                Assert.Equal($"/solicitacoes/{requestId}", readyNotification.LinkUrl);
                Assert.Equal("enviado", readyNotification.Status);
            }
            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var saved = await verifyDatabase.Requests.Include(request => request.Items).Include(request => request.Approvals)
                .SingleAsync(request => request.Id == requestId);
            Assert.Equal("aprovado", Assert.Single(saved.Approvals).Decision);
            Assert.Equal("PostgreSQL approval verified.", saved.Approvals.Single().Justification);
            Assert.Equal(1, saved.Items.Single().QuantityApproved);
            Assert.Equal(1, saved.Items.Single().QuantityReserved);
            Assert.Equal(1, (await verifyDatabase.Stocks.SingleAsync(stock => stock.ItemId == itemId)).Reserved);
            var requesterNotificationCount = await verifyDatabase.Database.SqlQuery<int>(
                    $"SELECT COUNT(*)::int AS \"Value\" FROM \"Notifications\" WHERE \"UserId\" = {requesterId} AND \"Channel\" = 'in_app' AND \"RelatedType\" = 'request' AND \"RelatedId\" = {requestId}")
                .SingleAsync();
            Assert.Equal(2, requesterNotificationCount);
            var audit = await verifyDatabase.AuditLogs.SingleAsync(log => log.EntityType == "request"
                && log.EntityId == requestId && log.Action == "approve");
            Assert.Equal(approverId, audit.UserId);
            Assert.Equal("PG approver", audit.UserName);
            Assert.Equal(saved.Code, audit.EntityLabel);
            using var before = JsonDocument.Parse(audit.BeforeJson!);
            using var after = JsonDocument.Parse(audit.AfterJson!);
            Assert.Equal("aguardando_aprovacao", before.RootElement.GetProperty("status").GetString());
            Assert.Equal("aprovada", after.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            if (requestId is not null)
                database.AuditLogs.RemoveRange(await database.AuditLogs.Where(log => log.EntityId == requestId).ToListAsync());
            if (requestId is not null)
                database.Notifications.RemoveRange(await database.Notifications.Where(notification =>
                    notification.RelatedType == "request" && notification.RelatedId == requestId).ToListAsync());
            if (requestId is not null)
                database.Requests.RemoveRange(await database.Requests.Where(request => request.Id == requestId).ToListAsync());
            if (ruleId is not null)
                database.ApprovalRules.RemoveRange(await database.ApprovalRules.Where(rule => rule.Id == ruleId).ToListAsync());
            if (itemId is not null)
            {
                database.Stocks.RemoveRange(await database.Stocks.Where(stock => stock.ItemId == itemId).ToListAsync());
                database.CatalogItems.RemoveRange(await database.CatalogItems.Where(item => item.Id == itemId).ToListAsync());
            }
            if (categoryId is not null)
                database.Categories.RemoveRange(await database.Categories.Where(category => category.Id == categoryId).ToListAsync());
            await database.SaveChangesAsync();
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            foreach (var userId in new[] { requesterId, approverId }.Where(id => id is not null))
            {
                var user = await users.FindByIdAsync(userId!.Value.ToString());
                if (user is not null)
                    Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            foreach (var name in new[] { requesterRoleName, approverRoleName })
            {
                var role = await roles.FindByNameAsync(name);
                if (role is not null)
                    Assert.True((await roles.DeleteAsync(role)).Succeeded);
            }
        }
    }

    [PostgreSqlFact]
    public async Task User_creation_persists_only_the_explicit_role_and_audit_in_postgres()
    {
        var client = factory.CreateClient();
        var managerEmail = $"{Guid.NewGuid():N}@example.invalid";
        var managerRoleName = $"postgres-user-manager-{Guid.NewGuid():N}";
        var selectedRoleName = $"postgres-created-user-{Guid.NewGuid():N}";
        Guid? managerId = null;
        Guid? createdUserId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roleManager = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
                var managerRole = new ApplicationRole(managerRoleName);
                var selectedRole = new ApplicationRole(selectedRoleName);
                Assert.True((await roleManager.CreateAsync(managerRole)).Succeeded);
                Assert.True((await roleManager.AddClaimAsync(managerRole, new Claim("permission", "users.manage"))).Succeeded);
                Assert.True((await roleManager.AddClaimAsync(managerRole, new Claim("permission", "users.view"))).Succeeded);
                Assert.True((await roleManager.CreateAsync(selectedRole)).Succeeded);

                var manager = new ApplicationUser
                {
                    UserName = managerEmail,
                    Email = managerEmail,
                    EmailConfirmed = true,
                    FullName = "PostgreSQL user manager"
                };
                Assert.True((await userManager.CreateAsync(manager, "CorrectPassword1")).Succeeded);
                Assert.True((await userManager.AddToRoleAsync(manager, managerRoleName)).Succeeded);
                managerId = manager.Id;
            }

            var token = await LoginAsync(client, managerEmail);
            using (var options = await SendAsync(client, HttpMethod.Get, "/api/v1/users/create-options", token))
            {
                Assert.Equal(HttpStatusCode.OK, options.StatusCode);
                using var optionsJson = JsonDocument.Parse(await options.Content.ReadAsStringAsync());
                var selectedRoleId = await GetRoleIdAsync(factory, selectedRoleName);
                Assert.Contains(optionsJson.RootElement.GetProperty("data").EnumerateArray(),
                    role => role.GetProperty("id").GetGuid() == selectedRoleId);
            }
            using var response = await SendAsync(client, HttpMethod.Post, "/api/v1/users", token, new
            {
                name = "PostgreSQL created user",
                email = $"{Guid.NewGuid():N}@example.invalid",
                password = "Correct#Password1",
                role_id = await GetRoleIdAsync(factory, selectedRoleName),
                must_change_password = true
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var responseJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            createdUserId = responseJson.RootElement.GetProperty("data").GetProperty("id").GetGuid();
            Assert.Equal($"/api/v1/users/{createdUserId}", response.Headers.Location?.ToString());

            using var detail = await SendAsync(client, HttpMethod.Get, $"/api/v1/users/{createdUserId}", token);
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            Assert.Equal("PostgreSQL created user", detailJson.RootElement.GetProperty("data").GetProperty("name").GetString());

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var savedRoles = await verifyDatabase.UserRoles.Where(link => link.UserId == createdUserId).ToListAsync();
            Assert.Equal(selectedRoleName, (await verifyScope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>()
                .FindByIdAsync(Assert.Single(savedRoles).RoleId.ToString()))?.Name);
            var audit = await verifyDatabase.AuditLogs.SingleAsync(log => log.EntityType == "user"
                && log.EntityId == createdUserId && log.Action == "create");
            Assert.Equal(managerId, audit.UserId);
            Assert.Null(audit.BeforeJson);
            using var auditAfter = JsonDocument.Parse(audit.AfterJson!);
            Assert.Equal("PostgreSQL created user", auditAfter.RootElement.GetProperty("name").GetString());
            Assert.False(auditAfter.RootElement.TryGetProperty("password", out _));
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            if (createdUserId is Guid createdId)
                database.AuditLogs.RemoveRange(await database.AuditLogs.Where(log => log.EntityId == createdId).ToListAsync());
            await database.SaveChangesAsync();

            var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
            foreach (var id in new[] { createdUserId, managerId })
            {
                if (id is Guid userId)
                {
                    var user = await userManager.FindByIdAsync(userId.ToString());
                    if (user is not null)
                        Assert.True((await userManager.DeleteAsync(user)).Succeeded);
                }
            }

            var roleManager = services.GetRequiredService<RoleManager<ApplicationRole>>();
            foreach (var name in new[] { selectedRoleName, managerRoleName })
            {
                var role = await roleManager.FindByNameAsync(name);
                if (role is not null)
                    Assert.True((await roleManager.DeleteAsync(role)).Succeeded);
            }
        }
    }

    [PostgreSqlFact]
    public async Task User_role_update_persists_audit_and_revokes_old_postgres_sessions()
    {
        var client = factory.CreateClient();
        var managerEmail = $"{Guid.NewGuid():N}@example.invalid";
        var managerRoleName = $"postgres-update-manager-{Guid.NewGuid():N}";
        var initialRoleName = $"postgres-initial-role-{Guid.NewGuid():N}";
        var replacementRoleName = $"postgres-replacement-role-{Guid.NewGuid():N}";
        var targetEmail = $"{Guid.NewGuid():N}@example.invalid";
        Guid? managerId = null;
        Guid? targetId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roleManager = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
                var managerRole = new ApplicationRole(managerRoleName);
                var initialRole = new ApplicationRole(initialRoleName);
                var replacementRole = new ApplicationRole(replacementRoleName);
                foreach (var role in new[] { managerRole, initialRole, replacementRole })
                    Assert.True((await roleManager.CreateAsync(role)).Succeeded);
                foreach (var permission in new[] { "users.manage", "users.view" })
                    Assert.True((await roleManager.AddClaimAsync(managerRole, new Claim("permission", permission))).Succeeded);

                var manager = new ApplicationUser { UserName = managerEmail, Email = managerEmail, EmailConfirmed = true, FullName = "PG updater" };
                var target = new ApplicationUser { UserName = targetEmail, Email = targetEmail, EmailConfirmed = true, FullName = "PG target" };
                Assert.True((await userManager.CreateAsync(manager, "CorrectPassword1")).Succeeded);
                Assert.True((await userManager.CreateAsync(target, "CorrectPassword1")).Succeeded);
                Assert.True((await userManager.AddToRoleAsync(manager, managerRoleName)).Succeeded);
                Assert.True((await userManager.AddToRoleAsync(target, initialRoleName)).Succeeded);
                managerId = manager.Id;
                targetId = target.Id;
            }

            var managerToken = await LoginAsync(client, managerEmail);
            var oldTargetToken = await LoginAsync(client, targetEmail);
            using var updated = await SendAsync(client, HttpMethod.Put, $"/api/v1/users/{targetId}", managerToken, new
            {
                name = "PG target updated",
                role_id = await GetRoleIdAsync(factory, replacementRoleName),
                password = "NewTemporaryPass123!",
                must_change_password = true
            });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            using var oldSession = await SendAsync(client, HttpMethod.Get, "/api/v1/auth/me", oldTargetToken);
            Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);

            using var newLogin = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = targetEmail, password = "NewTemporaryPass123!" });
            Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
            using var newLoginBody = JsonDocument.Parse(await newLogin.Content.ReadAsStringAsync());
            Assert.True(newLoginBody.RootElement.GetProperty("mustChangePassword").GetBoolean());

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyServices = verifyScope.ServiceProvider;
            var verifyDatabase = verifyServices.GetRequiredService<ApplicationDbContext>();
            var targetRoles = await verifyDatabase.UserRoles.Where(link => link.UserId == targetId).ToListAsync();
            Assert.Equal(replacementRoleName, (await verifyServices.GetRequiredService<RoleManager<ApplicationRole>>()
                .FindByIdAsync(Assert.Single(targetRoles).RoleId.ToString()))?.Name);
            var audit = await verifyDatabase.AuditLogs.SingleAsync(log => log.EntityType == "user"
                && log.EntityId == targetId && log.Action == "update");
            Assert.Equal(managerId, audit.UserId);
            using var before = JsonDocument.Parse(audit.BeforeJson!);
            using var after = JsonDocument.Parse(audit.AfterJson!);
            Assert.Equal("PG target", before.RootElement.GetProperty("name").GetString());
            Assert.Equal("PG target updated", after.RootElement.GetProperty("name").GetString());
            Assert.True(after.RootElement.GetProperty("must_change_password").GetBoolean());
            Assert.DoesNotContain("NewTemporaryPass123!", audit.BeforeJson + audit.AfterJson, StringComparison.Ordinal);
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            if (targetId is Guid targetGuid)
                database.AuditLogs.RemoveRange(await database.AuditLogs.Where(log => log.EntityId == targetGuid).ToListAsync());
            await database.SaveChangesAsync();
            var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
            foreach (var id in new[] { targetId, managerId })
            {
                if (id is Guid userId)
                {
                    var user = await userManager.FindByIdAsync(userId.ToString());
                    if (user is not null)
                        Assert.True((await userManager.DeleteAsync(user)).Succeeded);
                }
            }
            var roleManager = services.GetRequiredService<RoleManager<ApplicationRole>>();
            foreach (var name in new[] { replacementRoleName, initialRoleName, managerRoleName })
            {
                var role = await roleManager.FindByNameAsync(name);
                if (role is not null)
                    Assert.True((await roleManager.DeleteAsync(role)).Succeeded);
            }
        }
    }

    [PostgreSqlFact]
    public async Task User_trash_restore_and_purge_preserve_audit_and_keep_old_sessions_revoked_in_postgres()
    {
        var client = factory.CreateClient();
        var managerEmail = $"{Guid.NewGuid():N}@example.invalid";
        var managerRoleName = $"postgres-trash-manager-{Guid.NewGuid():N}";
        var targetRoleName = $"postgres-trash-target-{Guid.NewGuid():N}";
        var targetEmail = $"{Guid.NewGuid():N}@example.invalid";
        Guid? managerId = null;
        Guid? targetId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roleManager = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
                var managerRole = new ApplicationRole(managerRoleName);
                var targetRole = new ApplicationRole(targetRoleName);
                Assert.True((await roleManager.CreateAsync(managerRole)).Succeeded);
                Assert.True((await roleManager.AddClaimAsync(managerRole, new Claim("permission", "users.manage"))).Succeeded);
                Assert.True((await roleManager.AddClaimAsync(managerRole, new Claim("permission", "users.view"))).Succeeded);
                Assert.True((await roleManager.CreateAsync(targetRole)).Succeeded);
                var manager = new ApplicationUser { UserName = managerEmail, Email = managerEmail, EmailConfirmed = true, FullName = "PG trash manager" };
                var target = new ApplicationUser { UserName = targetEmail, Email = targetEmail, EmailConfirmed = true, FullName = "PG trash target" };
                Assert.True((await userManager.CreateAsync(manager, "CorrectPassword1")).Succeeded);
                Assert.True((await userManager.CreateAsync(target, "CorrectPassword1")).Succeeded);
                Assert.True((await userManager.AddToRoleAsync(manager, managerRoleName)).Succeeded);
                Assert.True((await userManager.AddToRoleAsync(target, targetRoleName)).Succeeded);
                managerId = manager.Id;
                targetId = target.Id;
            }

            var managerToken = await LoginAsync(client, managerEmail);
            var oldTargetToken = await LoginAsync(client, targetEmail);
            using var trashed = await SendAsync(client, HttpMethod.Delete, $"/api/v1/users/{targetId}", managerToken);
            Assert.Equal(HttpStatusCode.OK, trashed.StatusCode);
            using var oldDeletedSession = await SendAsync(client, HttpMethod.Get, "/api/v1/auth/me", oldTargetToken);
            Assert.Equal(HttpStatusCode.Unauthorized, oldDeletedSession.StatusCode);

            await using (var trashScope = factory.Services.CreateAsyncScope())
            {
                var database = trashScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var saved = await database.Users.SingleAsync(user => user.Id == targetId);
                Assert.NotNull(saved.DeletedAt);
                Assert.Equal(managerId, saved.DeletedBy);
            }

            using var restored = await SendAsync(client, HttpMethod.Post, $"/api/v1/users/{targetId}/restore", managerToken);
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
            using var oldRestoredSession = await SendAsync(client, HttpMethod.Get, "/api/v1/auth/me", oldTargetToken);
            Assert.Equal(HttpStatusCode.Unauthorized, oldRestoredSession.StatusCode);

            await using (var restoreScope = factory.Services.CreateAsyncScope())
            {
                var database = restoreScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var saved = await database.Users.SingleAsync(user => user.Id == targetId);
                Assert.Null(saved.DeletedAt);
                Assert.Null(saved.DeletedBy);
            }

            using var trashedAgain = await SendAsync(client, HttpMethod.Delete, $"/api/v1/users/{targetId}", managerToken);
            Assert.Equal(HttpStatusCode.OK, trashedAgain.StatusCode);
            using var wrongConfirm = await SendAsync(client, HttpMethod.Post, $"/api/v1/users/{targetId}/purge", managerToken,
                new { confirm = "wrong" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongConfirm.StatusCode);
            using var purged = await SendAsync(client, HttpMethod.Post, $"/api/v1/users/{targetId}/purge", managerToken,
                new { confirm = targetEmail });
            Assert.Equal(HttpStatusCode.OK, purged.StatusCode);

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False(await verifyDatabase.Users.AnyAsync(user => user.Id == targetId));
            var auditActions = await verifyDatabase.AuditLogs.Where(log => log.EntityType == "user" && log.EntityId == targetId)
                .Select(log => log.Action).ToListAsync();
            Assert.Equal(2, auditActions.Count(action => action == "delete"));
            Assert.Single(auditActions, action => action == "restore");
            Assert.Single(auditActions, action => action == "purge");
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            if (targetId is Guid targetGuid)
                database.AuditLogs.RemoveRange(await database.AuditLogs.Where(log => log.EntityId == targetGuid).ToListAsync());
            await database.SaveChangesAsync();
            var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
            foreach (var id in new[] { targetId, managerId })
            {
                if (id is Guid userId)
                {
                    var user = await userManager.FindByIdAsync(userId.ToString());
                    if (user is not null)
                        Assert.True((await userManager.DeleteAsync(user)).Succeeded);
                }
            }
            var roleManager = services.GetRequiredService<RoleManager<ApplicationRole>>();
            foreach (var name in new[] { targetRoleName, managerRoleName })
            {
                var role = await roleManager.FindByNameAsync(name);
                if (role is not null)
                    Assert.True((await roleManager.DeleteAsync(role)).Succeeded);
            }
        }
    }

    [PostgreSqlFact]
    public async Task User_purge_rejects_delivery_references_in_postgres()
    {
        var client = factory.CreateClient();
        var managerEmail = $"{Guid.NewGuid():N}@example.invalid";
        var managerRoleName = $"postgres-purge-manager-{Guid.NewGuid():N}";
        var targetRoleName = $"postgres-purge-target-{Guid.NewGuid():N}";
        var targetEmail = $"{Guid.NewGuid():N}@example.invalid";
        Guid? managerId = null;
        Guid? targetId = null;
        Guid? deliveryId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var managerRole = new ApplicationRole(managerRoleName);
                var targetRole = new ApplicationRole(targetRoleName);
                Assert.True((await roles.CreateAsync(managerRole)).Succeeded);
                Assert.True((await roles.AddClaimAsync(managerRole, new Claim("permission", "users.manage"))).Succeeded);
                Assert.True((await roles.CreateAsync(targetRole)).Succeeded);
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var manager = new ApplicationUser { UserName = managerEmail, Email = managerEmail, EmailConfirmed = true, FullName = "PG purge manager" };
                var target = new ApplicationUser { UserName = targetEmail, Email = targetEmail, EmailConfirmed = true, FullName = "PG purge target" };
                Assert.True((await users.CreateAsync(manager, "CorrectPassword1")).Succeeded);
                Assert.True((await users.CreateAsync(target, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(manager, managerRoleName)).Succeeded);
                Assert.True((await users.AddToRoleAsync(target, targetRoleName)).Succeeded);
                managerId = manager.Id;
                targetId = target.Id;

                var delivery = new Delivery
                {
                    Code = $"P{Guid.NewGuid():N}"[..20],
                    DeliveredById = target.Id,
                    ReceivedByName = "Recebedor de teste",
                    SignaturePath = "test/signature.png",
                    IdempotencyKeyHash = Guid.NewGuid().ToString("N"),
                    RequestHash = Guid.NewGuid().ToString("N")
                };
                database.Deliveries.Add(delivery);
                await database.SaveChangesAsync();
                deliveryId = delivery.Id;
            }

            var managerToken = await LoginAsync(client, managerEmail);
            using var trashed = await SendAsync(client, HttpMethod.Delete, $"/api/v1/users/{targetId}", managerToken);
            Assert.Equal(HttpStatusCode.OK, trashed.StatusCode);
            using var purge = await SendAsync(client, HttpMethod.Post, $"/api/v1/users/{targetId}/purge", managerToken,
                new { confirm = targetEmail });
            Assert.Equal(HttpStatusCode.Conflict, purge.StatusCode);
            using var purgeJson = JsonDocument.Parse(await purge.Content.ReadAsStringAsync());
            Assert.Equal("IN_USE", purgeJson.RootElement.GetProperty("error").GetString());
            Assert.Contains(purgeJson.RootElement.GetProperty("details").GetProperty("uses").EnumerateArray(),
                value => value.GetString()!.Contains("protocolo", StringComparison.OrdinalIgnoreCase));

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.True(await verifyDatabase.Users.AnyAsync(user => user.Id == targetId));
            Assert.Equal(targetId, await verifyDatabase.Deliveries.Where(delivery => delivery.Id == deliveryId)
                .Select(delivery => delivery.DeliveredById).SingleAsync());
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            if (deliveryId is Guid deliveryGuid)
                database.Deliveries.RemoveRange(await database.Deliveries.Where(delivery => delivery.Id == deliveryGuid).ToListAsync());
            if (targetId is Guid targetGuid)
                database.AuditLogs.RemoveRange(await database.AuditLogs.Where(log => log.EntityId == targetGuid || log.UserId == targetGuid).ToListAsync());
            await database.SaveChangesAsync();

            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            foreach (var id in new[] { targetId, managerId })
            {
                if (id is Guid userId && await users.FindByIdAsync(userId.ToString()) is { } user)
                    Assert.True((await users.DeleteAsync(user)).Succeeded);
            }

            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            foreach (var name in new[] { targetRoleName, managerRoleName })
            {
                if (await roles.FindByNameAsync(name) is { } role)
                    Assert.True((await roles.DeleteAsync(role)).Succeeded);
            }
        }
    }

    [PostgreSqlFact]
    public async Task Industry_scope_is_persisted_and_isolates_postgres_request_lists_and_details()
    {
        var client = factory.CreateClient();
        var roleName = $"postgres-industry-scope-{Guid.NewGuid():N}";
        var emails = Enumerable.Range(0, 3).Select(_ => $"{Guid.NewGuid():N}@example.invalid").ToArray();
        var industryA = new Industry { Name = $"Industry-A-{Guid.NewGuid():N}", NameKey = Guid.NewGuid().ToString("N") };
        var industryB = new Industry { Name = $"Industry-B-{Guid.NewGuid():N}", NameKey = Guid.NewGuid().ToString("N") };
        var userIds = new List<Guid>();
        var requestIds = new List<Guid>();

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roleManager = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roleManager.CreateAsync(role)).Succeeded);
                Assert.True((await roleManager.AddClaimAsync(role, new Claim("permission", "requests.view_own"))).Succeeded);
                database.Industries.AddRange(industryA, industryB);
                await database.SaveChangesAsync();

                for (var index = 0; index < emails.Length; index++)
                {
                    var user = new ApplicationUser
                    {
                        UserName = emails[index],
                        Email = emails[index],
                        EmailConfirmed = true,
                        FullName = $"Industry scoped user {index + 1}",
                        IndustryId = index < 2 ? industryA.Id : industryB.Id
                    };
                    Assert.True((await userManager.CreateAsync(user, "CorrectPassword1")).Succeeded);
                    Assert.True((await userManager.AddToRoleAsync(user, roleName)).Succeeded);
                    userIds.Add(user.Id);

                    var request = new InternalRequest
                    {
                        Code = $"PG-IND-{Guid.NewGuid():N}"[..20],
                        RequesterId = user.Id,
                        IndustryId = user.IndustryId,
                        Flow = "interna",
                        Status = "aprovada",
                        Purpose = $"Industry scoped request {index + 1}"
                    };
                    database.Requests.Add(request);
                    requestIds.Add(request.Id);
                }
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, emails[0]);
            using var list = await SendAsync(client, HttpMethod.Get, "/api/v1/requests", token);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            using var listBody = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            Assert.Equal(2, listBody.RootElement.GetProperty("meta").GetProperty("total").GetInt32());
            var visibleIds = listBody.RootElement.GetProperty("data").EnumerateArray()
                .Select(request => request.GetProperty("id").GetGuid()).ToHashSet();
            Assert.Contains(requestIds[0], visibleIds);
            Assert.Contains(requestIds[1], visibleIds);
            Assert.DoesNotContain(requestIds[2], visibleIds);

            using var sameIndustry = await SendAsync(client, HttpMethod.Get, $"/api/v1/requests/{requestIds[1]}", token);
            Assert.Equal(HttpStatusCode.OK, sameIndustry.StatusCode);
            using var otherIndustry = await SendAsync(client, HttpMethod.Get, $"/api/v1/requests/{requestIds[2]}", token);
            Assert.Equal(HttpStatusCode.NotFound, otherIndustry.StatusCode);
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            database.Requests.RemoveRange(await database.Requests.Where(request => requestIds.Contains(request.Id)).ToListAsync());
            await database.SaveChangesAsync();
            var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
            foreach (var id in userIds)
            {
                var user = await userManager.FindByIdAsync(id.ToString());
                if (user is not null)
                    Assert.True((await userManager.DeleteAsync(user)).Succeeded);
            }
            database.Industries.RemoveRange(await database.Industries.Where(industry => industry.Id == industryA.Id || industry.Id == industryB.Id).ToListAsync());
            await database.SaveChangesAsync();
            var roleManager = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roleManager.FindByNameAsync(roleName);
            if (role is not null)
                Assert.True((await roleManager.DeleteAsync(role)).Succeeded);
        }
    }

    [PostgreSqlFact]
    public async Task Item_creation_with_initial_quantity_persists_stock_movement_and_audit_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-initial-stock-{Guid.NewGuid():N}";
        Guid? userId = null;
        Guid? categoryId = null;
        Guid? itemId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "items.view"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "items.manage"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "stock.entry"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "stock.exit"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "stock.exit_confirm"))).Succeeded);

                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = "PostgreSQL initial stock test"
                };
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);

                var category = new Category
                {
                    Name = $"PostgreSQL initial stock {Guid.NewGuid():N}",
                    NameKey = Guid.NewGuid().ToString("N")
                };
                database.Categories.Add(category);
                await database.SaveChangesAsync();
                userId = user.Id;
                categoryId = category.Id;
            }

            var token = await LoginAsync(client, email);
            using var preview = await SendAsync(client, HttpMethod.Get, "/api/v1/items/next-code", token);
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            using var previewJson = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
            var code = previewJson.RootElement.GetProperty("data").GetProperty("code").GetString()!;
            using var rejected = await SendAsync(client, HttpMethod.Post, "/api/v1/items", token, new
            {
                name = "Rejected sequence allocation",
                categoryId,
                kind = "invalid"
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
            using var retryPreview = await SendAsync(client, HttpMethod.Get, "/api/v1/items/next-code", token);
            using var retryPreviewJson = JsonDocument.Parse(await retryPreview.Content.ReadAsStringAsync());
            Assert.Equal(code, retryPreviewJson.RootElement.GetProperty("data").GetProperty("code").GetString());
            using var created = await SendAsync(client, HttpMethod.Post, "/api/v1/items", token, new
            {
                name = "PostgreSQL initial stock",
                categoryId,
                initialQuantity = 7
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var itemJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            Assert.Equal(code, itemJson.RootElement.GetProperty("code").GetString());
            itemId = itemJson.RootElement.GetProperty("id").GetGuid();

            using var history = await SendAsync(client, HttpMethod.Get, $"/api/v1/items/{itemId}/history", token);
            Assert.Equal(HttpStatusCode.OK, history.StatusCode);
            using var historyJson = JsonDocument.Parse(await history.Content.ReadAsStringAsync());
            var historyEntries = historyJson.RootElement.GetProperty("data");
            Assert.Equal(2, historyEntries.GetArrayLength());
            Assert.Contains(historyEntries.EnumerateArray(), entry => entry.GetProperty("kind").GetString() == "movement");
            Assert.Contains(historyEntries.EnumerateArray(), entry => entry.GetProperty("action").GetString() == "create");

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stock = await verifyDatabase.Stocks.SingleAsync(entry => entry.ItemId == itemId);
            var movement = await verifyDatabase.StockMovements.SingleAsync(entry => entry.ItemId == itemId);
            var audit = await verifyDatabase.AuditLogs.SingleAsync(entry => entry.EntityType == "item"
                && entry.EntityId == itemId && entry.Action == "create");
            var sequence = await verifyDatabase.SequenceCounters.SingleAsync(counter => counter.Name == "item" && counter.Period == "all");
            Assert.Equal(7, stock.OnHand);
            Assert.Equal(int.Parse(code[4..], System.Globalization.CultureInfo.InvariantCulture), sequence.LastValue);
            Assert.Equal("entrada", movement.Type);
            Assert.Equal(7, movement.Quantity);
            Assert.Equal(7, movement.BalanceAfter);
            Assert.Equal("Saldo inicial", movement.Reason);
            Assert.Equal(userId, movement.UserId);
            Assert.Equal(userId, audit.UserId);
            using var auditSnapshot = JsonDocument.Parse(audit.AfterJson!);
            Assert.Equal(7, auditSnapshot.RootElement.GetProperty("initial_quantity").GetInt32());

            using var metadataEntry = await SendAsync(client, HttpMethod.Post, $"/api/v1/items/{itemId}/entry", token, new
            {
                quantity = 2,
                purpose = "Kit de evento",
                recipient = "Equipe comercial",
                purchase_ticket_no = "PG-OC-2026-17",
                idempotency_key = "pg-entry-metadata-001"
            });
            Assert.Equal(HttpStatusCode.Created, metadataEntry.StatusCode);
            using var metadataEntryJson = JsonDocument.Parse(await metadataEntry.Content.ReadAsStringAsync());
            Assert.Equal("Kit de evento", metadataEntryJson.RootElement.GetProperty("purpose").GetString());
            Assert.Equal("Equipe comercial", metadataEntryJson.RootElement.GetProperty("recipient").GetString());
            Assert.Equal("PG-OC-2026-17", metadataEntryJson.RootElement.GetProperty("purchaseTicketNo").GetString());

            await using var metadataScope = factory.Services.CreateAsyncScope();
            var metadataDatabase = metadataScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var persistedMetadata = await metadataDatabase.StockMovements.SingleAsync(entry => entry.ItemId == itemId
                && entry.PurchaseTicketNo == "PG-OC-2026-17");
            Assert.Equal("Kit de evento", persistedMetadata.Purpose);
            Assert.Equal("Equipe comercial", persistedMetadata.Recipient);

            using var metadataHistory = await SendAsync(client, HttpMethod.Get, $"/api/v1/items/{itemId}/history", token);
            Assert.Equal(HttpStatusCode.OK, metadataHistory.StatusCode);
            using var metadataHistoryJson = JsonDocument.Parse(await metadataHistory.Content.ReadAsStringAsync());
            var metadataHistoryRow = Assert.Single(metadataHistoryJson.RootElement.GetProperty("data").EnumerateArray(),
                entry => entry.TryGetProperty("purchase_ticket_no", out var ticket)
                    && ticket.GetString() == "PG-OC-2026-17");
            Assert.Equal("Kit de evento", metadataHistoryRow.GetProperty("purpose").GetString());
            Assert.Equal("Equipe comercial", metadataHistoryRow.GetProperty("recipient").GetString());

            using var exitRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/exits")
            {
                Content = JsonContent.Create(new
                {
                    item_id = itemId,
                    quantity = 2,
                    purpose = "Evento de homologação",
                    recipient = "Equipe local",
                    purchase_ticket_no = "PG-EXIT-OC-17",
                    document_ref = "NF-EXIT-17"
                })
            };
            exitRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            exitRequest.Headers.Add("Idempotency-Key", "pg-exit-order-001");
            using var authorizedExit = await client.SendAsync(exitRequest);
            Assert.Equal(HttpStatusCode.Created, authorizedExit.StatusCode);
            using var authorizedExitJson = JsonDocument.Parse(await authorizedExit.Content.ReadAsStringAsync());
            var exitOrder = authorizedExitJson.RootElement.GetProperty("data").GetProperty("order");
            Assert.StartsWith($"SAI-{DateTime.UtcNow.Year}-", exitOrder.GetProperty("code").GetString());
            Assert.Equal("autorizada", exitOrder.GetProperty("status").GetString());
            Assert.Equal("PG-EXIT-OC-17", exitOrder.GetProperty("purchaseTicketNo").GetString());
            var exitStock = authorizedExitJson.RootElement.GetProperty("data").GetProperty("stock");
            Assert.Equal(9, exitStock.GetProperty("onHand").GetInt32());
            Assert.Equal(2, exitStock.GetProperty("reserved").GetInt32());

            await using var exitScope = factory.Services.CreateAsyncScope();
            var exitDatabase = exitScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var savedExit = await exitDatabase.StockExitOrders.SingleAsync(order => order.ItemId == itemId);
            Assert.Equal("Evento de homologação", savedExit.Purpose);
            Assert.Equal("PG-EXIT-OC-17", savedExit.PurchaseTicketNo);
            Assert.Equal(userId, savedExit.AuthorizedBy);
            var exitAudit = await exitDatabase.AuditLogs.SingleAsync(log => log.EntityType == "stock_exit_order"
                && log.EntityId == savedExit.Id && log.Action == "stock_exit_authorize");
            Assert.Equal(userId, exitAudit.UserId);

            using var confirmRequest = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/stock/exit-orders/{savedExit.Id}/confirm");
            confirmRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            confirmRequest.Headers.Add("Idempotency-Key", "pg-exit-confirm-001");
            using var confirmedExit = await client.SendAsync(confirmRequest);
            Assert.Equal(HttpStatusCode.OK, confirmedExit.StatusCode);
            using var confirmedJson = JsonDocument.Parse(await confirmedExit.Content.ReadAsStringAsync());
            var confirmedOrder = confirmedJson.RootElement.GetProperty("data").GetProperty("order");
            Assert.Equal("confirmada", confirmedOrder.GetProperty("status").GetString());
            var confirmedMovementId = confirmedOrder.GetProperty("movementId").GetGuid();

            using var confirmReplayRequest = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/stock/exit-orders/{savedExit.Id}/confirm");
            confirmReplayRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            confirmReplayRequest.Headers.Add("Idempotency-Key", "pg-exit-confirm-001");
            using var replayedExit = await client.SendAsync(confirmReplayRequest);
            Assert.Equal(HttpStatusCode.OK, replayedExit.StatusCode);
            Assert.Equal("true", replayedExit.Headers.GetValues("Idempotent-Replayed").Single());
            using var replayedJson = JsonDocument.Parse(await replayedExit.Content.ReadAsStringAsync());
            Assert.Equal(confirmedMovementId, replayedJson.RootElement.GetProperty("data").GetProperty("order")
                .GetProperty("movementId").GetGuid());

            await using var confirmedScope = factory.Services.CreateAsyncScope();
            var confirmedDatabase = confirmedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var confirmedStock = await confirmedDatabase.Stocks.AsNoTracking()
                .SingleAsync(stock => stock.ItemId == itemId);
            var confirmedMovement = await confirmedDatabase.StockMovements.AsNoTracking()
                .SingleAsync(movement => movement.Id == confirmedMovementId);
            Assert.Equal(7, confirmedStock.OnHand);
            Assert.Equal(0, confirmedStock.Reserved);
            Assert.Equal("saida", confirmedMovement.Type);
            Assert.Equal(-2, confirmedMovement.Quantity);
            Assert.Equal(7, confirmedMovement.BalanceAfter);
            Assert.Equal("Evento de homologação", confirmedMovement.Purpose);
            Assert.Equal("Equipe local", confirmedMovement.Recipient);
            Assert.Equal("PG-EXIT-OC-17", confirmedMovement.PurchaseTicketNo);
            Assert.Equal("NF-EXIT-17", confirmedMovement.DocumentRef);
            Assert.Single(await confirmedDatabase.StockMovements.AsNoTracking()
                .Where(movement => movement.ItemId == itemId && movement.Type == "saida").ToListAsync());
            var stockExitAudit = await confirmedDatabase.AuditLogs.AsNoTracking().SingleAsync(log =>
                log.Action == "stock_saida" && log.EntityType == "item" && log.EntityId == itemId);
            var orderConfirmAudit = await confirmedDatabase.AuditLogs.AsNoTracking().SingleAsync(log =>
                log.Action == "stock_exit_confirm" && log.EntityType == "stock_exit_order" && log.EntityId == savedExit.Id);
            Assert.Equal(userId, stockExitAudit.UserId);
            Assert.Equal(userId, orderConfirmAudit.UserId);
        }
        finally
        {
            await CleanupAsync(roleName, userId, categoryId, itemId, null);
        }
    }

    [PostgreSqlFact]
    public async Task Item_purge_is_locked_confirmed_and_audited_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-item-purge-{Guid.NewGuid():N}";
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var referencedEventId = Guid.NewGuid();
        var referencedIndustryId = Guid.NewGuid();
        Guid? userId = null;
        var code = $"PG-PURGE-{Guid.NewGuid():N}"[..24];

        await using (var configScope = factory.Services.CreateAsyncScope())
        {
            var configuredConnection = configScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.GetConnectionString();
            Assert.Equal(Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection"), configuredConnection);
        }

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                foreach (var permission in new[] { "items.view", "items.manage", "items.delete", "items.purge" })
                    Assert.True((await roles.AddClaimAsync(role, new Claim("permission", permission))).Succeeded);
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = "PostgreSQL item purge"
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;

                database.Categories.Add(new Category { Id = categoryId, Name = $"Purge-{Guid.NewGuid():N}", NameKey = Guid.NewGuid().ToString("N") });
                var item = new CatalogItem { Id = itemId, Code = code, Name = "Purge PostgreSQL", CategoryId = categoryId };
                database.CatalogItems.Add(item);
                database.Stocks.Add(new InventoryStock { ItemId = itemId, Item = item, OnHand = 3, Reserved = 1 });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using var reservedTrash = await SendAsync(client, HttpMethod.Delete, $"/api/v1/items/{itemId}", token);
            Assert.Equal(HttpStatusCode.Conflict, reservedTrash.StatusCode);
            await using (var rejectedScope = factory.Services.CreateAsyncScope())
            {
                var database = rejectedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.False(await database.AuditLogs.AnyAsync(log => log.EntityType == "item" && log.EntityId == itemId && log.Action == "delete"));
            }
            using var reservedDeactivation = await SendAsync(client, HttpMethod.Post, $"/api/v1/items/{itemId}/deactivate", token);
            Assert.Equal(HttpStatusCode.Conflict, reservedDeactivation.StatusCode);
            await using (var stockScope = factory.Services.CreateAsyncScope())
            {
                var database = stockScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var stock = await database.Stocks.SingleAsync(entry => entry.ItemId == itemId);
                stock.Reserved = 0;
                await database.SaveChangesAsync();
            }
            using var deactivated = await SendAsync(client, HttpMethod.Post, $"/api/v1/items/{itemId}/deactivate", token);
            Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
            using var activated = await SendAsync(client, HttpMethod.Post, $"/api/v1/items/{itemId}/activate", token);
            Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
            using var updated = await SendAsync(client, HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { name = "Updated PostgreSQL item" });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            using var noOpUpdate = await SendAsync(client, HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { name = "Updated PostgreSQL item" });
            Assert.Equal(HttpStatusCode.OK, noOpUpdate.StatusCode);
            await using (var updateAuditScope = factory.Services.CreateAsyncScope())
            {
                var database = updateAuditScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Equal(1, await database.AuditLogs.CountAsync(log => log.EntityType == "item" && log.EntityId == itemId && log.Action == "update"));
                var persistedUpdateAudit = await database.AuditLogs.SingleAsync(log => log.EntityType == "item" && log.EntityId == itemId && log.Action == "update");
                using var before = JsonDocument.Parse(persistedUpdateAudit.BeforeJson!);
                using var after = JsonDocument.Parse(persistedUpdateAudit.AfterJson!);
                Assert.Equal("Purge PostgreSQL", before.RootElement.GetProperty("name").GetString());
                Assert.Equal("Updated PostgreSQL item", after.RootElement.GetProperty("name").GetString());
                Assert.False(before.RootElement.TryGetProperty("code", out _));
                Assert.False(after.RootElement.TryGetProperty("code", out _));
            }
            await using (var stockScope = factory.Services.CreateAsyncScope())
            {
                var database = stockScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var stock = await database.Stocks.SingleAsync(entry => entry.ItemId == itemId);
                stock.OnHand = 0;
                await database.SaveChangesAsync();
            }
            using var activePurge = await SendAsync(client, HttpMethod.Post, $"/api/v1/items/{itemId}/purge", token, new { confirm = code });
            Assert.Equal(HttpStatusCode.Conflict, activePurge.StatusCode);
            using var trashed = await SendAsync(client, HttpMethod.Delete, $"/api/v1/items/{itemId}", token);
            Assert.Equal(HttpStatusCode.OK, trashed.StatusCode);
            await using (var trashAuditScope = factory.Services.CreateAsyncScope())
            {
                var database = trashAuditScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var trashAudit = await database.AuditLogs.SingleAsync(log => log.EntityType == "item" && log.EntityId == itemId && log.Action == "delete");
                Assert.Equal(userId, trashAudit.UserId);
                Assert.Null(trashAudit.AfterJson);
                var deletedBy = await database.Database.SqlQuery<Guid?>($"SELECT \"DeletedBy\" AS \"Value\" FROM \"CatalogItems\" WHERE \"Id\" = {itemId}").SingleAsync();
                Assert.Equal(userId, deletedBy);
                using var beforeTrash = JsonDocument.Parse(trashAudit.BeforeJson!);
                Assert.Equal("Updated PostgreSQL item", beforeTrash.RootElement.GetProperty("name").GetString());
                Assert.Equal(JsonValueKind.Null, beforeTrash.RootElement.GetProperty("deleted_at").ValueKind);
            }
            using var restored = await SendAsync(client, HttpMethod.Post, $"/api/v1/items/{itemId}/restore", token);
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
            await using (var restoreAuditScope = factory.Services.CreateAsyncScope())
            {
                var database = restoreAuditScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var restoreLog = await database.AuditLogs.SingleAsync(log => log.EntityType == "item" && log.EntityId == itemId && log.Action == "restore");
                Assert.Equal(userId, restoreLog.UserId);
                Assert.Null(restoreLog.BeforeJson);
                Assert.Null(restoreLog.AfterJson);
                var deletedBy = await database.Database.SqlQuery<Guid?>($"SELECT \"DeletedBy\" AS \"Value\" FROM \"CatalogItems\" WHERE \"Id\" = {itemId}").SingleAsync();
                Assert.Null(deletedBy);
            }
            using var trashedAgain = await SendAsync(client, HttpMethod.Delete, $"/api/v1/items/{itemId}", token);
            Assert.Equal(HttpStatusCode.OK, trashedAgain.StatusCode);
            await using (var referenceScope = factory.Services.CreateAsyncScope())
            {
                var database = referenceScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                database.Events.Add(new Event { Id = referencedEventId, Name = $"Referenced event {referencedEventId:N}" });
                database.Industries.Add(new Industry { Id = referencedIndustryId, Name = $"Referenced industry {referencedIndustryId:N}", NameKey = referencedIndustryId.ToString("N") });
                database.EventAllocations.Add(new EventAllocation
                {
                    EventId = referencedEventId,
                    IndustryId = referencedIndustryId,
                    ItemId = itemId,
                    QuantityAllocated = 1
                });
                await database.SaveChangesAsync();
            }
            using (var referencedPurge = await SendAsync(client, HttpMethod.Post, $"/api/v1/items/{itemId}/purge", token, new { confirm = code }))
            {
                Assert.Equal(HttpStatusCode.Conflict, referencedPurge.StatusCode);
                using var referencedJson = JsonDocument.Parse(await referencedPurge.Content.ReadAsStringAsync());
                Assert.Contains("cotas de eventos", referencedJson.RootElement.GetProperty("uses").EnumerateArray().Select(value => value.GetString()));
            }
            await using (var removeReferenceScope = factory.Services.CreateAsyncScope())
            {
                var database = removeReferenceScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                database.Events.Remove(await database.Events.SingleAsync(value => value.Id == referencedEventId));
                database.Industries.Remove(await database.Industries.SingleAsync(value => value.Id == referencedIndustryId));
                await database.SaveChangesAsync();
            }
            using var wrongConfirmation = await SendAsync(client, HttpMethod.Post, $"/api/v1/items/{itemId}/purge", token, new { confirm = "wrong" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongConfirmation.StatusCode);
            using var purged = await SendAsync(client, HttpMethod.Post, $"/api/v1/items/{itemId}/purge", token, new { confirm = code });
            Assert.Equal(HttpStatusCode.OK, purged.StatusCode);

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Null(await verifyDatabase.CatalogItems.SingleOrDefaultAsync(item => item.Id == itemId));
            var deleteAudits = await verifyDatabase.AuditLogs.Where(log => log.EntityType == "item" && log.EntityId == itemId && log.Action == "delete").ToListAsync();
            Assert.Equal(2, deleteAudits.Count);
            Assert.All(deleteAudits, audit => Assert.Equal(userId, audit.UserId));
            var restoreAudit = await verifyDatabase.AuditLogs.SingleAsync(log => log.EntityType == "item" && log.EntityId == itemId && log.Action == "restore");
            Assert.Equal(userId, restoreAudit.UserId);
            var deactivationAudit = await verifyDatabase.AuditLogs.SingleAsync(log => log.EntityType == "item" && log.EntityId == itemId && log.Action == "deactivate");
            var activationAudit = await verifyDatabase.AuditLogs.SingleAsync(log => log.EntityType == "item" && log.EntityId == itemId && log.Action == "activate");
            using var beforeDeactivation = JsonDocument.Parse(deactivationAudit.BeforeJson!);
            using var afterDeactivation = JsonDocument.Parse(deactivationAudit.AfterJson!);
            using var beforeActivation = JsonDocument.Parse(activationAudit.BeforeJson!);
            using var afterActivation = JsonDocument.Parse(activationAudit.AfterJson!);
            Assert.Equal("ativo", beforeDeactivation.RootElement.GetProperty("status").GetString());
            Assert.Equal("inativo", afterDeactivation.RootElement.GetProperty("status").GetString());
            Assert.Equal("inativo", beforeActivation.RootElement.GetProperty("status").GetString());
            Assert.Equal("ativo", afterActivation.RootElement.GetProperty("status").GetString());
            var updateAudit = await verifyDatabase.AuditLogs.SingleAsync(log => log.EntityType == "item" && log.EntityId == itemId && log.Action == "update");
            Assert.Equal(userId, updateAudit.UserId);
            using var beforeUpdate = JsonDocument.Parse(updateAudit.BeforeJson!);
            using var afterUpdate = JsonDocument.Parse(updateAudit.AfterJson!);
            Assert.Equal("Purge PostgreSQL", beforeUpdate.RootElement.GetProperty("name").GetString());
            Assert.Equal("Updated PostgreSQL item", afterUpdate.RootElement.GetProperty("name").GetString());
            var audit = await verifyDatabase.AuditLogs.SingleAsync(log => log.EntityType == "item" && log.EntityId == itemId && log.Action == "purge");
            Assert.Equal("purge", audit.Action);
            Assert.Equal(userId, audit.UserId);
            Assert.Contains(code, audit.BeforeJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            database.AuditLogs.RemoveRange(await database.AuditLogs.Where(log => log.EntityType == "item" && log.EntityId == itemId).ToListAsync());
            var referencedEvent = await database.Events.FindAsync(referencedEventId);
            if (referencedEvent is not null)
                database.Events.Remove(referencedEvent);
            var referencedIndustry = await database.Industries.FindAsync(referencedIndustryId);
            if (referencedIndustry is not null)
                database.Industries.Remove(referencedIndustry);
            database.Stocks.RemoveRange(await database.Stocks.Where(stock => stock.ItemId == itemId).ToListAsync());
            database.CatalogItems.RemoveRange(await database.CatalogItems.Where(item => item.Id == itemId).ToListAsync());
            database.Categories.RemoveRange(await database.Categories.Where(category => category.Id == categoryId).ToListAsync());
            await database.SaveChangesAsync();
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            if (userId is Guid id)
            {
                var user = await users.FindByIdAsync(id.ToString());
                if (user is not null)
                    Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync(roleName);
            if (role is not null)
                Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }

    [PostgreSqlFact]
    public async Task Role_permission_changes_and_audit_are_atomic_and_persist_in_postgres()
    {
        var client = factory.CreateClient();
        var managerEmail = $"{Guid.NewGuid():N}@example.invalid";
        var managerRoleName = $"postgres-roles-manager-{Guid.NewGuid():N}";
        var targetRoleName = $"postgres-roles-target-{Guid.NewGuid():N}";
        Guid? managerId = null;
        Guid? targetRoleId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var setupServices = scope.ServiceProvider;
                var setupDatabase = setupServices.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", setupDatabase.Database.ProviderName);
                var setupRoles = setupServices.GetRequiredService<RoleManager<ApplicationRole>>();
                var setupUsers = setupServices.GetRequiredService<UserManager<ApplicationUser>>();
                var managerRole = new ApplicationRole(managerRoleName);
                var targetRole = new ApplicationRole(targetRoleName);
                Assert.True((await setupRoles.CreateAsync(managerRole)).Succeeded);
                Assert.True((await setupRoles.AddClaimAsync(managerRole, new Claim("permission", "roles.manage"))).Succeeded);
                Assert.True((await setupRoles.CreateAsync(targetRole)).Succeeded);
                Assert.True((await setupRoles.AddClaimAsync(targetRole, new Claim("permission", "items.view"))).Succeeded);
                var manager = new ApplicationUser
                {
                    UserName = managerEmail,
                    Email = managerEmail,
                    EmailConfirmed = true,
                    FullName = "PostgreSQL roles manager"
                };
                Assert.True((await setupUsers.CreateAsync(manager, "CorrectPassword1")).Succeeded);
                Assert.True((await setupUsers.AddToRoleAsync(manager, managerRoleName)).Succeeded);
                managerId = manager.Id;
                targetRoleId = targetRole.Id;
            }

            var token = await LoginAsync(client, managerEmail);
            using var response = await SendAsync(client, HttpMethod.Put, $"/api/v1/roles/{targetRoleId}/permissions", token,
                new { permissions = new[] { "requests.create", "items.view" } });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var services = verifyScope.ServiceProvider;
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var savedRole = await roles.FindByIdAsync(targetRoleId!.Value.ToString());
            Assert.NotNull(savedRole);
            var grants = await roles.GetClaimsAsync(savedRole!);
            Assert.Equal(new[] { "items.view", "requests.create" }, grants
                .Where(claim => claim.Type == "permission").Select(claim => claim.Value).OrderBy(value => value).ToArray());
            var database = services.GetRequiredService<ApplicationDbContext>();
            var audit = await database.AuditLogs.SingleAsync(log => log.EntityType == "role" && log.EntityId == targetRoleId);
            Assert.Equal("permissions_update", audit.Action);
            Assert.Equal(managerId, audit.UserId);
            Assert.Contains("items.view", audit.BeforeJson);
            Assert.Contains("requests.create", audit.AfterJson);
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            if (targetRoleId is Guid roleId)
            {
                database.AuditLogs.RemoveRange(await database.AuditLogs
                    .Where(log => log.EntityType == "role" && log.EntityId == roleId).ToListAsync());
                await database.SaveChangesAsync();
            }
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            if (managerId is Guid userId)
            {
                var manager = await users.FindByIdAsync(userId.ToString());
                if (manager is not null)
                    Assert.True((await users.DeleteAsync(manager)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            foreach (var name in new[] { targetRoleName, managerRoleName })
            {
                var role = await roles.FindByNameAsync(name);
                if (role is not null)
                    Assert.True((await roles.DeleteAsync(role)).Succeeded);
            }
        }
    }

    [PostgreSqlFact]
    public async Task Grouped_reports_execute_and_aggregate_correctly_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-report-viewer-{Guid.NewGuid():N}";
        var departmentId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var firstIndustryId = Guid.NewGuid();
        var secondIndustryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var uniqueItemId = Guid.NewGuid();
        var departmentName = $"PostgreSQL report department {Guid.NewGuid():N}";
        var userName = $"PostgreSQL report user {Guid.NewGuid():N}";
        var firstIndustryName = $"A-{Guid.NewGuid():N}";
        var secondIndustryName = $"Z-{Guid.NewGuid():N}";
        var itemCode = $"PGR-{Guid.NewGuid():N}"[..15];
        var uniqueItemCode = $"PGU-{Guid.NewGuid():N}"[..15];
        Guid? userId = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "reports.view"))).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "stock.view"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = userName };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                userId = user.Id;

                database.Departments.Add(new Department { Id = departmentId, Name = departmentName, NameKey = departmentId.ToString("N") });
                database.Categories.Add(new Category { Id = categoryId, Name = $"PostgreSQL report category {categoryId:N}", NameKey = categoryId.ToString("N") });
                database.Industries.AddRange(
                    new Industry { Id = firstIndustryId, Name = firstIndustryName, NameKey = firstIndustryId.ToString("N") },
                    new Industry { Id = secondIndustryId, Name = secondIndustryName, NameKey = secondIndustryId.ToString("N") });
                database.CatalogItems.AddRange(
                    new CatalogItem { Id = itemId, Code = itemCode, Name = "PostgreSQL grouped item", CategoryId = categoryId, UnitValue = 2m },
                    new CatalogItem { Id = uniqueItemId, Code = uniqueItemCode, Name = "PostgreSQL unique-industry item", CategoryId = categoryId, UnitValue = 2m });
                database.StockMovements.AddRange(
                    new StockMovement { ItemId = itemId, UserId = userId, IndustryId = firstIndustryId, DepartmentId = departmentId, Type = "saida", Quantity = -4, CreatedAt = new DateTimeOffset(2025, 1, 31, 23, 59, 59, 999, TimeSpan.Zero) },
                    new StockMovement { ItemId = itemId, UserId = userId, IndustryId = secondIndustryId, DepartmentId = departmentId, Type = "saida", Quantity = -2, CreatedAt = new DateTimeOffset(2025, 1, 31, 12, 0, 0, TimeSpan.Zero) },
                    new StockMovement { ItemId = itemId, UserId = userId, Type = "entrada", Quantity = 11 },
                    new StockMovement { ItemId = uniqueItemId, UserId = userId, IndustryId = firstIndustryId, Type = "entrada", Quantity = 2 },
                    new StockMovement { ItemId = uniqueItemId, UserId = userId, Type = "entrada", Quantity = 3 });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using (var response = await SendAsync(client, HttpMethod.Get, "/api/v1/reports/by_item?from=2025-01-01&to=2025-01-31", token))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var row = Assert.Single(json.RootElement.GetProperty("data").GetProperty("rows").EnumerateArray(), value => value[0].GetString() == itemCode);
                Assert.Equal(6, row[3].GetInt32());
                Assert.Equal(12m, row[4].GetDecimal());
            }
            using (var response = await SendAsync(client, HttpMethod.Get, "/api/v1/reports/by_department?from=2025-01-01&to=2025-01-31", token))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var row = Assert.Single(json.RootElement.GetProperty("data").GetProperty("rows").EnumerateArray(), value => value[0].GetString() == departmentName);
                Assert.Equal($"{firstIndustryName}, {secondIndustryName}", row[1].GetString());
                Assert.Equal(6, row[2].GetInt32());
                Assert.Equal(12m, row[3].GetDecimal());
            }
            using (var response = await SendAsync(client, HttpMethod.Get, "/api/v1/reports/by_industry?from=2025-01-01&to=2025-01-31", token))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var rows = json.RootElement.GetProperty("data").GetProperty("rows").EnumerateArray()
                    .Where(value => value[0].GetString() == firstIndustryName || value[0].GetString() == secondIndustryName).ToArray();
                Assert.Equal(2, rows.Length);
                Assert.Equal(firstIndustryName, rows[0][0].GetString());
                Assert.Equal(4, rows[0][1].GetInt32());
                Assert.Equal(8m, rows[0][2].GetDecimal());
                Assert.Equal(secondIndustryName, rows[1][0].GetString());
                Assert.Equal(2, rows[1][1].GetInt32());
                Assert.Equal(4m, rows[1][2].GetDecimal());
            }
            using (var response = await SendAsync(client, HttpMethod.Get, "/api/v1/reports/by_requester?from=2025-01-01&to=2025-01-31", token))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var row = Assert.Single(json.RootElement.GetProperty("data").GetProperty("rows").EnumerateArray(), value => value[0].GetString() == userName);
                Assert.Equal(6, row[2].GetInt32());
                Assert.Equal(12m, row[3].GetDecimal());
            }
            using (var response = await SendAsync(client, HttpMethod.Get, "/api/v1/reports/stock_industry", token))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var rows = json.RootElement.GetProperty("data").GetProperty("rows").EnumerateArray().ToArray();
                var uniqueRow = Assert.Single(rows, row => row[1].GetString() == uniqueItemCode);
                Assert.Equal(5, uniqueRow[3].GetInt32());
                Assert.Equal(5, uniqueRow[5].GetInt32());
                var ambiguousRows = rows.Where(row => row[1].GetString() == itemCode).ToArray();
                Assert.Equal(2, ambiguousRows.Length);
                Assert.All(ambiguousRows, row => Assert.Equal(0, row[3].GetInt32()));
            }
            using (var response = await SendAsync(client, HttpMethod.Get, "/api/v1/stock/by-industry", token))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var rows = json.RootElement.GetProperty("data").EnumerateArray().ToArray();
                var uniqueRow = Assert.Single(rows, row => row.GetProperty("item").GetProperty("code").GetString() == uniqueItemCode);
                Assert.Equal(5, uniqueRow.GetProperty("received").GetInt32());
                Assert.Equal(5, uniqueRow.GetProperty("saldo").GetInt32());
                var ambiguousRows = rows.Where(row => row.GetProperty("item").GetProperty("code").GetString() == itemCode).ToArray();
                Assert.Equal(2, ambiguousRows.Length);
                Assert.All(ambiguousRows, row => Assert.Equal(0, row.GetProperty("received").GetInt32()));
            }
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            database.StockMovements.RemoveRange(await database.StockMovements.Where(row => row.ItemId == itemId || row.ItemId == uniqueItemId).ToListAsync());
            var items = await database.CatalogItems.Where(row => row.Id == itemId || row.Id == uniqueItemId).ToListAsync();
            database.CatalogItems.RemoveRange(items);
            var category = await database.Categories.SingleOrDefaultAsync(row => row.Id == categoryId);
            if (category is not null)
                database.Categories.Remove(category);
            var department = await database.Departments.SingleOrDefaultAsync(row => row.Id == departmentId);
            if (department is not null)
                database.Departments.Remove(department);
            var industries = await database.Industries.Where(row => row.Id == firstIndustryId || row.Id == secondIndustryId).ToListAsync();
            database.Industries.RemoveRange(industries);
            await database.SaveChangesAsync();

            if (userId is Guid id)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(id.ToString());
                if (user is not null)
                    Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync(roleName);
            if (role is not null)
                Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }

    private static async Task<Guid> GetRoleIdAsync(PostgreSqlWebApplicationFactory factory, string roleName)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        return (await roles.FindByNameAsync(roleName))!.Id;
    }

    [PostgreSqlFact]
    public async Task Concurrent_identical_imports_create_one_item_and_one_audit_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-import-{Guid.NewGuid():N}";
        var categoryId = Guid.NewGuid();
        var categoryName = $"Import-{Guid.NewGuid():N}";
        var code = $"PC-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        Guid? userId = null;
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                database.Categories.Add(new Category { Id = categoryId, Name = categoryName, NameKey = categoryName.ToUpperInvariant() });
                await database.SaveChangesAsync();
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "import.run"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "PostgreSQL import" };
                Assert.True((await services.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(user, "CorrectPassword1")).Succeeded);
                userId = user.Id;
                Assert.True((await services.GetRequiredService<UserManager<ApplicationUser>>().AddToRoleAsync(user, roleName)).Succeeded);
            }

            var token = await LoginAsync(client, email);
            async Task<HttpResponseMessage> ApplyAsync(string name = "Importação concorrente")
            {
                var content = new MultipartFormDataContent();
                content.Add(new ByteArrayContent(ImportEndpointsTests.CreateWorkbook(
                    ["codigo", "nome", "categoria"], [code, name, categoryName])), "file", "items.xlsx");
                var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/import/items/apply") { Content = content };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return await client.SendAsync(request);
            }

            var responses = await Task.WhenAll(ApplyAsync(), ApplyAsync());
            using (responses[0]) using (responses[1])
            {
                Assert.Equal(HttpStatusCode.OK, responses[0].StatusCode);
                Assert.Equal(HttpStatusCode.OK, responses[1].StatusCode);
                var results = await Task.WhenAll(responses.Select(async response =>
                {
                    using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
                    return (Created: body.RootElement.GetProperty("created").GetInt32(), Skipped: body.RootElement.GetProperty("skipped").GetInt32());
                }));
                Assert.Equal(1, results.Sum(result => result.Created));
                Assert.Equal(1, results.Sum(result => result.Skipped));
            }
            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var item = await verifyDatabase.CatalogItems.SingleAsync(value => value.Code == code);
            Assert.Equal("Importação concorrente", item.Name);
            Assert.Equal(1, await verifyDatabase.AuditLogs.CountAsync(log => log.EntityType == "item" && log.EntityId == item.Id && log.Action == "create"));

            using var conflict = await ApplyAsync("Payload divergente");
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("Importação concorrente", await verifyDatabase.CatalogItems.AsNoTracking().Where(value => value.Code == code).Select(value => value.Name).SingleAsync());
            Assert.Equal(1, await verifyDatabase.AuditLogs.CountAsync(log => log.EntityType == "item" && log.EntityId == item.Id && log.Action == "create"));
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            var item = await database.CatalogItems.SingleOrDefaultAsync(value => value.Code == code);
            if (item is not null)
            {
                database.AuditLogs.RemoveRange(await database.AuditLogs.Where(log => log.EntityType == "item" && log.EntityId == item.Id).ToListAsync());
                database.Stocks.RemoveRange(await database.Stocks.Where(stock => stock.ItemId == item.Id).ToListAsync());
                database.CatalogItems.Remove(item);
                await database.SaveChangesAsync();
            }
            var category = await database.Categories.SingleOrDefaultAsync(value => value.Id == categoryId);
            if (category is not null)
            {
                database.Categories.Remove(category);
                await database.SaveChangesAsync();
            }
            if (userId is Guid id)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(id.ToString());
                if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            if (await roles.FindByNameAsync(roleName) is { } role)
                Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }

    [PostgreSqlFact]
    public async Task Stock_reconciliation_repairs_missing_position_and_opening_movement_once_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-stock-reconcile-{Guid.NewGuid():N}";
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        Guid? userId = null;
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "stock.adjust"))).Succeeded);
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = "PostgreSQL stock reconciliation"
                };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                userId = user.Id;
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);

                database.Categories.Add(new Category
                {
                    Id = categoryId,
                    Name = $"Reconcile {categoryId:N}",
                    NameKey = categoryId.ToString("N")
                });
                database.Locations.Add(new Location
                {
                    Id = locationId,
                    Name = $"Reconcile {locationId:N}",
                    NameKey = locationId.ToString("N"),
                    Kind = "cd"
                });
                database.CatalogItems.Add(new CatalogItem
                {
                    Id = itemId,
                    Code = $"PG-REC-{itemId:N}"[..20],
                    Name = "PostgreSQL reconciliation",
                    CategoryId = categoryId,
                    LocationId = locationId
                });
                database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 6 });
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using (var first = await SendAsync(client, HttpMethod.Post, "/api/v1/stock/reconcile", token))
            {
                var body = await first.Content.ReadAsStringAsync();
                Assert.True(first.StatusCode == HttpStatusCode.OK, body);
                using var json = JsonDocument.Parse(body);
                var result = json.RootElement.GetProperty("data");
                Assert.Equal(1, result.GetProperty("total_items").GetInt32());
                Assert.Equal(1, result.GetProperty("fixed_positions").GetInt32());
                Assert.Equal(1, result.GetProperty("fixed_movements").GetInt32());
            }

            using (var replay = await SendAsync(client, HttpMethod.Post, "/api/v1/stock/reconcile", token))
            {
                var body = await replay.Content.ReadAsStringAsync();
                Assert.True(replay.StatusCode == HttpStatusCode.OK, body);
                using var json = JsonDocument.Parse(body);
                var result = json.RootElement.GetProperty("data");
                Assert.Equal(1, result.GetProperty("total_items").GetInt32());
                Assert.Equal(0, result.GetProperty("fixed_positions").GetInt32());
                Assert.Equal(0, result.GetProperty("fixed_movements").GetInt32());
            }

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(6, (await verify.Stocks.SingleAsync(stock => stock.ItemId == itemId)).OnHand);
            Assert.Equal(6, await verify.StockPositions.Where(position => position.ItemId == itemId)
                .Select(position => position.Quantity).SingleAsync());
            var movement = await verify.StockMovements.SingleAsync(entry => entry.ItemId == itemId);
            Assert.Equal("entrada", movement.Type);
            Assert.Equal(6, movement.Quantity);
            Assert.Equal(6, movement.BalanceAfter);
            Assert.Equal("Saldo inicial", movement.Reason);
            Assert.Equal(userId, movement.UserId);
        }
        finally
        {
            await using var cleanup = factory.Services.CreateAsyncScope();
            var services = cleanup.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            database.StockPositions.RemoveRange(database.StockPositions.Where(position => position.ItemId == itemId));
            database.StockMovements.RemoveRange(database.StockMovements.Where(movement => movement.ItemId == itemId));
            database.Stocks.RemoveRange(database.Stocks.Where(stock => stock.ItemId == itemId));
            database.CatalogItems.RemoveRange(database.CatalogItems.Where(item => item.Id == itemId));
            database.Locations.RemoveRange(database.Locations.Where(location => location.Id == locationId));
            database.Categories.RemoveRange(database.Categories.Where(category => category.Id == categoryId));
            await database.SaveChangesAsync();
            if (userId is Guid id)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(id.ToString());
                if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync(roleName);
            if (role is not null) Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task<InventoryStock> GetStockAsync(PostgreSqlWebApplicationFactory factory, Guid itemId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await database.Stocks.SingleAsync(stock => stock.ItemId == itemId);
    }

    private async Task CleanupAsync(string roleName, Guid? userId, Guid? categoryId, Guid? itemId, Guid? requestId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var database = services.GetRequiredService<ApplicationDbContext>();
        if (requestId is not null)
        {
            var request = await database.Requests.SingleOrDefaultAsync(entity => entity.Id == requestId);
            if (request is not null)
            {
                database.Requests.Remove(request);
                await database.SaveChangesAsync();
            }
        }
        if (itemId is not null)
        {
            var item = await database.CatalogItems.SingleOrDefaultAsync(entity => entity.Id == itemId);
            if (item is not null)
            {
                database.StockExitOrders.RemoveRange(await database.StockExitOrders
                    .Where(order => order.ItemId == itemId).ToListAsync());
                database.StockMovements.RemoveRange(await database.StockMovements
                    .Where(movement => movement.ItemId == itemId).ToListAsync());
                database.Stocks.RemoveRange(await database.Stocks
                    .Where(stock => stock.ItemId == itemId).ToListAsync());
                database.CatalogItems.Remove(item);
                await database.SaveChangesAsync();
            }
        }
        if (categoryId is not null)
        {
            var category = await database.Categories.SingleOrDefaultAsync(entity => entity.Id == categoryId);
            if (category is not null)
            {
                database.Categories.Remove(category);
                await database.SaveChangesAsync();
            }
        }
        if (userId is not null)
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(userId.Value.ToString());
            if (user is not null)
                Assert.True((await users.DeleteAsync(user)).Succeeded);
        }
        var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = await roles.FindByNameAsync(roleName);
        if (role is not null)
            Assert.True((await roles.DeleteAsync(role)).Succeeded);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token, object body)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static string CreateSignatureDataUri()
    {
        using var bitmap = new SKBitmap(128, 64);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black, StrokeWidth = 4, IsStroke = true, IsAntialias = true };
        canvas.DrawLine(4, 36, 124, 20, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return $"data:image/png;base64,{Convert.ToBase64String(encoded.ToArray())}";
    }
}

public sealed class PostgreSqlWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _photoRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", $"gbrindes-postgres-photos-{Guid.NewGuid():N}");
    private readonly string _signatureRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", $"gbrindes-postgres-signatures-{Guid.NewGuid():N}");
    private readonly string _invoiceRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", $"gbrindes-postgres-invoices-{Guid.NewGuid():N}");
    private readonly string _brandingRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "cache", "scratch", $"gbrindes-postgres-branding-{Guid.NewGuid():N}");

    public string SignatureRoot => _signatureRoot;
    public string InvoiceRoot => _invoiceRoot;
    public string BrandingRoot => _brandingRoot;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Storage:ItemPhotos:RootPath", _photoRoot);
        builder.UseSetting("Storage:Signatures:RootPath", _signatureRoot);
        builder.UseSetting("Storage:Invoices:RootPath", _invoiceRoot);
        builder.UseSetting("Storage:Branding:RootPath", _brandingRoot);
        builder.UseSetting("DeliveryVerification:PublicBaseUrl", "https://gbrindes.example");
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            builder.ConfigureServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.RemoveAll<ApplicationDbContext>();
                services.AddScoped(_ => new ApplicationDbContext(
                    new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options));
            });
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_photoRoot))
            Directory.Delete(_photoRoot, recursive: true);
        if (disposing && Directory.Exists(_signatureRoot))
            Directory.Delete(_signatureRoot, recursive: true);
        if (disposing && Directory.Exists(_invoiceRoot))
            Directory.Delete(_invoiceRoot, recursive: true);
        if (disposing && Directory.Exists(_brandingRoot))
            Directory.Delete(_brandingRoot, recursive: true);
    }
}

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("GBRINDES_RUN_POSTGRES_TESTS"), "1", StringComparison.Ordinal))
            Skip = "Set GBRINDES_RUN_POSTGRES_TESTS=1 and configure ConnectionStrings__DefaultConnection to run local PostgreSQL integration tests.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")))
            Skip = "Set ConnectionStrings__DefaultConnection to a local PostgreSQL database before enabling PostgreSQL integration tests.";
    }
}
