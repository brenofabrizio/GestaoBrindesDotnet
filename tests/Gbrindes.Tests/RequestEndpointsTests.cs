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
using SkiaSharp;

using Microsoft.Extensions.DependencyInjection;

namespace Gbrindes.Tests;

public sealed class RequestEndpointsTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Any_authenticated_user_can_load_only_active_user_options_with_department_details()
    {
        var (token, ownerId, _) = await ArrangeAsync([]);
        Guid departmentId;
        Guid activeId;
        Guid inactiveId;
        Guid trashedId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await users.FindByIdAsync(ownerId.ToString());
            var department = new Department { Name = $"Options-{Guid.NewGuid():N}", NameKey = Guid.NewGuid().ToString("N") };
            database.Departments.Add(department);
            owner!.DepartmentId = department.Id;
            await users.UpdateAsync(owner);
            departmentId = department.Id;

            var active = new ApplicationUser
            {
                UserName = $"{Guid.NewGuid():N}@example.invalid",
                Email = $"{Guid.NewGuid():N}@example.invalid",
                EmailConfirmed = true,
                FullName = "Ativo selecionável"
            };
            Assert.True((await users.CreateAsync(active, "CorrectPassword1")).Succeeded);
            activeId = active.Id;

            var inactive = new ApplicationUser
            {
                UserName = $"{Guid.NewGuid():N}@example.invalid",
                Email = $"{Guid.NewGuid():N}@example.invalid",
                EmailConfirmed = true,
                FullName = "Inativo oculto",
                Active = false
            };
            Assert.True((await users.CreateAsync(inactive, "CorrectPassword1")).Succeeded);
            inactiveId = inactive.Id;

            var trashed = new ApplicationUser
            {
                UserName = $"{Guid.NewGuid():N}@example.invalid",
                Email = $"{Guid.NewGuid():N}@example.invalid",
                EmailConfirmed = true,
                FullName = "Excluído oculto",
                DeletedAt = DateTimeOffset.UtcNow
            };
            Assert.True((await users.CreateAsync(trashed, "CorrectPassword1")).Succeeded);
            trashedId = trashed.Id;
        }

        using var response = await SendAsync(HttpMethod.Get, "/api/v1/users/options", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        var options = json.RootElement.GetProperty("data").EnumerateArray().ToArray();
        Assert.Contains(options, option => option.GetProperty("id").GetGuid() == ownerId);
        Assert.Contains(options, option => option.GetProperty("id").GetGuid() == activeId);
        Assert.DoesNotContain(options, option => option.GetProperty("id").GetGuid() == inactiveId);
        Assert.DoesNotContain(options, option => option.GetProperty("id").GetGuid() == trashedId);
        var ownerOption = options.Single(option => option.GetProperty("id").GetGuid() == ownerId);
        Assert.Equal(departmentId, ownerOption.GetProperty("department").GetProperty("id").GetGuid());
        Assert.StartsWith("Options-", ownerOption.GetProperty("department").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Users_view_can_filter_and_page_directory_without_exposing_credentials()
    {
        var (token, _, _) = await ArrangeAsync(["users.view"]);
        var marker = $"Directory-{Guid.NewGuid():N}";
        var activeId = await CreateDirectoryUserAsync($"{marker} active", true, null);
        var inactiveId = await CreateDirectoryUserAsync($"{marker} inactive", false, null);
        var trashedId = await CreateDirectoryUserAsync($"{marker} trashed", true, DateTimeOffset.UtcNow);

        using var activeResponse = await SendAsync(HttpMethod.Get,
            $"/api/v1/users?q={marker}&active=1&page=1&per_page=10", token);
        Assert.Equal(HttpStatusCode.OK, activeResponse.StatusCode);
        using var activeJson = JsonDocument.Parse(await activeResponse.Content.ReadAsStringAsync());
        var activeMeta = activeJson.RootElement.GetProperty("meta");
        Assert.Equal(1, activeMeta.GetProperty("total").GetInt32());
        Assert.Equal(1, activeMeta.GetProperty("page").GetInt32());
        Assert.Equal(10, activeMeta.GetProperty("per_page").GetInt32());
        Assert.Equal(1, activeMeta.GetProperty("last_page").GetInt32());
        var user = Assert.Single(activeJson.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(activeId, user.GetProperty("id").GetGuid());
        Assert.True(DateTimeOffset.TryParse(user.GetProperty("created_at").GetString(), out _));
        Assert.True(DateTimeOffset.TryParse(user.GetProperty("updated_at").GetString(), out _));
        Assert.DoesNotContain(user.EnumerateObject(), property => property.Name is "password" or "password_hash" or "normalized_password");
        Assert.DoesNotContain(user.EnumerateObject(), property => property.Name is "security_stamp" or "normalized_email" or "normalized_user_name");

        using var trashResponse = await SendAsync(HttpMethod.Get, $"/api/v1/users?q={marker}&trashed=1", token);
        Assert.Equal(HttpStatusCode.OK, trashResponse.StatusCode);
        using var trashJson = JsonDocument.Parse(await trashResponse.Content.ReadAsStringAsync());
        var trashed = Assert.Single(trashJson.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(trashedId, trashed.GetProperty("id").GetGuid());
        Assert.NotEqual(inactiveId, trashed.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task User_directory_requires_users_view_except_for_reserved_admin()
    {
        var (userToken, _, _) = await ArrangeAsync([]);
        using var forbidden = await SendAsync(HttpMethod.Get, "/api/v1/users", userToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var (adminToken, _) = await ArrangeAdminAsync();
        using var adminView = await SendAsync(HttpMethod.Get, "/api/v1/users", adminToken);
        Assert.Equal(HttpStatusCode.OK, adminView.StatusCode);
    }

    [Fact]
    public async Task User_creation_role_options_are_scoped_to_user_managers_and_hide_grant_details()
    {
        var (managerToken, _, _) = await ArrangeAsync(["users.manage"]);
        var (ordinaryToken, _, _) = await ArrangeAsync([]);
        var role = new ApplicationRole($"creation-option-{Guid.NewGuid():N}");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            Assert.True((await roles.CreateAsync(role)).Succeeded);
            Assert.True((await roles.AddClaimAsync(role, new System.Security.Claims.Claim("permission", "users.manage"))).Succeeded);
        }

        using var response = await SendAsync(HttpMethod.Get, "/api/v1/users/create-options", managerToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var selected = Assert.Single(json.RootElement.GetProperty("data").EnumerateArray(),
            option => option.GetProperty("id").GetGuid() == role.Id);
        Assert.Equal(role.Name, selected.GetProperty("name").GetString());
        Assert.False(selected.TryGetProperty("permissions", out _));
        Assert.False(selected.TryGetProperty("claims", out _));

        using var forbidden = await SendAsync(HttpMethod.Get, "/api/v1/users/create-options", ordinaryToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task User_manager_scope_options_include_departments_and_industries_for_editing()
    {
        var (managerToken, _, _) = await ArrangeAsync(["users.manage"]);
        var department = new Department { Name = $"Scope-{Guid.NewGuid():N}", NameKey = Guid.NewGuid().ToString("N") };
        var industry = new Industry { Name = $"Scope-{Guid.NewGuid():N}", NameKey = Guid.NewGuid().ToString("N") };
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Departments.Add(department);
            database.Industries.Add(industry);
            await database.SaveChangesAsync();
        }

        using var response = await SendAsync(HttpMethod.Get, "/api/v1/users/create-options", managerToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(json.RootElement.GetProperty("departments").EnumerateArray(),
            option => option.GetProperty("id").GetGuid() == department.Id);
        Assert.Contains(json.RootElement.GetProperty("industries").EnumerateArray(),
            option => option.GetProperty("id").GetGuid() == industry.Id);
    }

    [Fact]
    public async Task User_manager_creates_account_with_only_the_explicitly_selected_role_and_audit()
    {
        var (adminToken, adminId) = await ArrangeAdminAsync();
        var roleName = $"explicit-role-{Guid.NewGuid():N}";
        var email = $"{Guid.NewGuid():N}@example.invalid";
        Guid roleId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = new ApplicationRole(roleName);
            Assert.True((await roles.CreateAsync(role)).Succeeded);
            roleId = role.Id;
        }

        using var response = await SendAsync(HttpMethod.Post, "/api/v1/users", adminToken, new
        {
            name = "Conta criada pelo administrador",
            email,
            password = "Correct#Password1",
            role_id = roleId,
            active = true,
            must_change_password = true
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        var data = json.RootElement.GetProperty("data");
        var userId = data.GetProperty("id").GetGuid();
        Assert.Equal(email, data.GetProperty("email").GetString());
        Assert.True(data.GetProperty("must_change_password").GetBoolean());
        Assert.Contains(data.GetProperty("roles").EnumerateArray(), role => role.GetProperty("id").GetGuid() == roleId);
        Assert.DoesNotContain(data.EnumerateObject(), property => property.Name is "password" or "password_hash");
        using var detailResponse = await SendAsync(HttpMethod.Get, $"/api/v1/users/{userId}", adminToken);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        using var detailJson = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        Assert.Equal(userId, detailJson.RootElement.GetProperty("data").GetProperty("id").GetGuid());

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var database = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await database.Users.SingleAsync(user => user.Id == userId);
        Assert.Equal("Conta criada pelo administrador", saved.FullName);
        var assignedRoles = await database.UserRoles.Where(userRole => userRole.UserId == userId).ToListAsync();
        Assert.Equal(roleId, Assert.Single(assignedRoles).RoleId);
        Assert.Empty(await database.UserClaims.Where(claim => claim.UserId == userId).ToListAsync());
        var audit = await database.AuditLogs.SingleAsync(log => log.EntityType == "user" && log.EntityId == userId && log.Action == "create");
        Assert.Equal(adminId, audit.UserId);
        Assert.Equal("Request admin", audit.UserName);
        using var auditAfter = JsonDocument.Parse(audit.AfterJson!);
        Assert.False(auditAfter.RootElement.TryGetProperty("password", out _));
        Assert.False(auditAfter.RootElement.TryGetProperty("password_hash", out _));
    }

    [Fact]
    public async Task User_creation_rejects_passwords_that_do_not_meet_the_php_admin_policy()
    {
        var (adminToken, _) = await ArrangeAdminAsync();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        Guid roleId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = new ApplicationRole($"weak-password-role-{Guid.NewGuid():N}");
            Assert.True((await roles.CreateAsync(role)).Succeeded);
            roleId = role.Id;
        }

        using var response = await SendAsync(HttpMethod.Post, "/api/v1/users", adminToken, new
        {
            name = "Rejected weak password",
            email,
            password = "CorrectPassword1",
            role_id = roleId
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var database = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await database.Users.AnyAsync(user => user.Email == email));
        Assert.False(await database.AuditLogs.AnyAsync(log => log.EntityType == "user" && log.Action == "create"
            && log.AfterJson != null && log.AfterJson.Contains(email)));
    }

    [Fact]
    public async Task User_manager_updates_profile_and_revokes_sessions_when_role_changes()
    {
        var (adminToken, _) = await ArrangeAdminAsync();
        var (targetToken, targetId, _) = await ArrangeAsync([]);
        var replacementRole = new ApplicationRole($"replacement-role-{Guid.NewGuid():N}");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            Assert.True((await roles.CreateAsync(replacementRole)).Succeeded);
        }

        using var response = await SendAsync(HttpMethod.Put, $"/api/v1/users/{targetId}", adminToken, new
        {
            name = "Updated request user",
            role_id = replacementRole.Id,
            must_change_password = true
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var expiredSession = await SendAsync(HttpMethod.Get, "/api/v1/auth/me", targetToken);
        Assert.Equal(HttpStatusCode.Unauthorized, expiredSession.StatusCode);

        using var updatedJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Updated request user", updatedJson.RootElement.GetProperty("data").GetProperty("name").GetString());
        Assert.Equal(replacementRole.Id, Assert.Single(updatedJson.RootElement.GetProperty("data").GetProperty("roles")
            .EnumerateArray()).GetProperty("id").GetGuid());

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var database = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var audit = await database.AuditLogs.SingleAsync(log => log.EntityType == "user" && log.EntityId == targetId && log.Action == "update");
        using var before = JsonDocument.Parse(audit.BeforeJson!);
        using var after = JsonDocument.Parse(audit.AfterJson!);
        Assert.Equal("Request test", before.RootElement.GetProperty("name").GetString());
        Assert.Equal("Updated request user", after.RootElement.GetProperty("name").GetString());
        Assert.False(after.RootElement.TryGetProperty("password_hash", out _));
    }

    [Fact]
    public async Task User_manager_resets_password_requires_change_and_revokes_existing_sessions()
    {
        var (adminToken, _) = await ArrangeAdminAsync();
        var (oldToken, targetId, _) = await ArrangeAsync([]);
        const string newPassword = "New#Password2";

        using var response = await SendAsync(HttpMethod.Put, $"/api/v1/users/{targetId}", adminToken, new
        {
            password = newPassword
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var oldSession = await SendAsync(HttpMethod.Get, "/api/v1/auth/me", oldToken);
        Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);

        var email = await GetUserEmailAsync(targetId);
        using var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = newPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        Assert.True(loginJson.RootElement.GetProperty("mustChangePassword").GetBoolean());

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var database = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await database.Users.SingleAsync(user => user.Id == targetId);
        Assert.NotNull(saved.PasswordChangedAt);
        var audit = await database.AuditLogs.SingleAsync(log => log.EntityType == "user" && log.EntityId == targetId && log.Action == "update");
        Assert.DoesNotContain(newPassword, audit.BeforeJson + audit.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task User_manager_deactivates_and_reactivates_accounts_with_audit_and_session_revocation()
    {
        var (adminToken, _) = await ArrangeAdminAsync();
        var (oldToken, targetId, _) = await ArrangeAsync([]);

        using var deactivated = await SendAsync(HttpMethod.Post, $"/api/v1/users/{targetId}/deactivate", adminToken);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        using var deactivatedJson = JsonDocument.Parse(await deactivated.Content.ReadAsStringAsync());
        Assert.False(deactivatedJson.RootElement.GetProperty("data").GetProperty("active").GetBoolean());

        using var expiredSession = await SendAsync(HttpMethod.Get, "/api/v1/auth/me", oldToken);
        Assert.Equal(HttpStatusCode.Unauthorized, expiredSession.StatusCode);

        using var activated = await SendAsync(HttpMethod.Post, $"/api/v1/users/{targetId}/activate", adminToken);
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        using var activatedJson = JsonDocument.Parse(await activated.Content.ReadAsStringAsync());
        Assert.True(activatedJson.RootElement.GetProperty("data").GetProperty("active").GetBoolean());

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var database = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var changes = await database.AuditLogs.Where(log => log.EntityType == "user" && log.EntityId == targetId
            && (log.Action == "deactivate" || log.Action == "activate")).OrderBy(log => log.Action).ToListAsync();
        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, log => log.Action == "deactivate");
        Assert.Contains(changes, log => log.Action == "activate");
    }

    [Fact]
    public async Task User_manager_trashes_and_restores_account_without_reactivating_old_sessions()
    {
        var (adminToken, _) = await ArrangeAdminAsync();
        var (oldToken, targetId, _) = await ArrangeAsync([]);

        using var trashed = await SendAsync(HttpMethod.Delete, $"/api/v1/users/{targetId}", adminToken);
        Assert.Equal(HttpStatusCode.OK, trashed.StatusCode);
        using var deletedSession = await SendAsync(HttpMethod.Get, "/api/v1/auth/me", oldToken);
        Assert.Equal(HttpStatusCode.Unauthorized, deletedSession.StatusCode);

        await using (var trashScope = factory.Services.CreateAsyncScope())
        {
            var database = trashScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var saved = await database.Users.SingleAsync(user => user.Id == targetId);
            Assert.NotNull(saved.DeletedAt);
        }

        using var restored = await SendAsync(HttpMethod.Post, $"/api/v1/users/{targetId}/restore", adminToken);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        using var restoredJson = JsonDocument.Parse(await restored.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, restoredJson.RootElement.GetProperty("data").GetProperty("deleted_at").ValueKind);
        using var stillExpired = await SendAsync(HttpMethod.Get, "/api/v1/auth/me", oldToken);
        Assert.Equal(HttpStatusCode.Unauthorized, stillExpired.StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var savedUser = await verifyDatabase.Users.SingleAsync(user => user.Id == targetId);
        Assert.Null(savedUser.DeletedAt);
        Assert.True(savedUser.Active);
        var actions = await verifyDatabase.AuditLogs.Where(log => log.EntityType == "user" && log.EntityId == targetId)
            .Select(log => log.Action).ToListAsync();
        Assert.Contains("delete", actions);
        Assert.Contains("restore", actions);
    }

    [Fact]
    public async Task User_purge_rejects_accounts_referenced_by_deliveries_and_stock_exit_orders()
    {
        var (adminToken, _) = await ArrangeAdminAsync();
        var (_, targetId, itemId) = await ArrangeAsync([]);
        var email = await GetUserEmailAsync(targetId);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Deliveries.Add(new Delivery
            {
                Code = $"PROT-{Guid.NewGuid():N}"[..24],
                Type = "dia_a_dia",
                DeliveredById = targetId,
                ReceivedByName = "Recebedor de teste",
                SignaturePath = "test/signature.png",
                IdempotencyKeyHash = Guid.NewGuid().ToString("N"),
                RequestHash = Guid.NewGuid().ToString("N")
            });
            database.StockExitOrders.Add(new StockExitOrder
            {
                Code = $"SAI-{Guid.NewGuid():N}"[..20],
                ItemId = itemId,
                Quantity = 1,
                Purpose = "Referência de teste",
                RequesterId = targetId,
                AuthorizedBy = targetId
            });
            await database.SaveChangesAsync();
        }

        using var trashed = await SendAsync(HttpMethod.Delete, $"/api/v1/users/{targetId}", adminToken);
        Assert.Equal(HttpStatusCode.OK, trashed.StatusCode);
        using var purge = await SendAsync(HttpMethod.Post, $"/api/v1/users/{targetId}/purge", adminToken, new { confirm = email });
        Assert.Equal(HttpStatusCode.Conflict, purge.StatusCode);
        using var purgeJson = JsonDocument.Parse(await purge.Content.ReadAsStringAsync());
        Assert.Equal("IN_USE", purgeJson.RootElement.GetProperty("error").GetString());
        var uses = purgeJson.RootElement.GetProperty("details").GetProperty("uses").EnumerateArray()
            .Select(value => value.GetString()).ToArray();
        Assert.Contains(uses, value => value!.Contains("protocolo", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(uses, value => value!.Contains("saída", StringComparison.OrdinalIgnoreCase));

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await verifyDatabase.Users.AnyAsync(user => user.Id == targetId));
    }

    [Fact]
    public async Task User_purge_rejects_accounts_referenced_by_request_history()
    {
        var (adminToken, _) = await ArrangeAdminAsync();
        var (targetToken, targetId, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        _ = await CreateRequestAsync(targetToken, itemId, "Keep requester history");
        var email = await GetUserEmailAsync(targetId);

        using var trashed = await SendAsync(HttpMethod.Delete, $"/api/v1/users/{targetId}", adminToken);
        Assert.Equal(HttpStatusCode.OK, trashed.StatusCode);
        using var purge = await SendAsync(HttpMethod.Post, $"/api/v1/users/{targetId}/purge", adminToken, new { confirm = email });
        Assert.Equal(HttpStatusCode.Conflict, purge.StatusCode);
        using var purgeJson = JsonDocument.Parse(await purge.Content.ReadAsStringAsync());
        Assert.Equal("IN_USE", purgeJson.RootElement.GetProperty("error").GetString());

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var database = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await database.Users.SingleAsync(user => user.Id == targetId);
        Assert.NotNull(saved.DeletedAt);
    }

    [Fact]
    public async Task User_purge_requires_typed_email_confirmation_and_removes_only_unused_trashed_accounts()
    {
        var (adminToken, _) = await ArrangeAdminAsync();
        var (_, targetId, _) = await ArrangeAsync([]);
        var email = await GetUserEmailAsync(targetId);
        using var trashed = await SendAsync(HttpMethod.Delete, $"/api/v1/users/{targetId}", adminToken);
        Assert.Equal(HttpStatusCode.OK, trashed.StatusCode);

        using var wrongConfirmation = await SendAsync(HttpMethod.Post, $"/api/v1/users/{targetId}/purge", adminToken,
            new { confirm = "not-the-user-email" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongConfirmation.StatusCode);

        using var purged = await SendAsync(HttpMethod.Post, $"/api/v1/users/{targetId}/purge", adminToken, new { confirm = email });
        Assert.Equal(HttpStatusCode.OK, purged.StatusCode);
        using var purgedJson = JsonDocument.Parse(await purged.Content.ReadAsStringAsync());
        Assert.True(purgedJson.RootElement.GetProperty("data").GetProperty("purged").GetBoolean());

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var database = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await database.Users.AnyAsync(user => user.Id == targetId));
        var purgeAudit = await database.AuditLogs.SingleAsync(log => log.EntityType == "user" && log.EntityId == targetId && log.Action == "purge");
        Assert.Null(purgeAudit.AfterJson);
    }

    [Fact]
    public async Task Requester_can_create_a_trade_draft_without_exposing_it_as_an_internal_request()
    {
        var (token, userId, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/trade-requests", token, new
        {
            purpose = "Campanha TRADE",
            recipient = "Equipe regional",
            items = new[] { new { item_id = itemId, qty_requested = 2 } }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("trade", data.GetProperty("flow").GetString());
        Assert.Equal("rascunho", data.GetProperty("status").GetString());
        Assert.Equal(userId, data.GetProperty("requester_id").GetGuid());

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await database.Requests.Include(request => request.Items).Include(request => request.History)
            .SingleAsync(request => request.Id == data.GetProperty("id").GetGuid());
        Assert.Equal("trade", saved.Flow);
        Assert.Single(saved.Items);
        Assert.Single(saved.History);

        using var internalList = await SendAsync(HttpMethod.Get, "/api/v1/requests", token);
        Assert.Equal(HttpStatusCode.OK, internalList.StatusCode);
        using var listJson = JsonDocument.Parse(await internalList.Content.ReadAsStringAsync());
        Assert.Equal(0, listJson.RootElement.GetProperty("meta").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Trade_requester_can_submit_their_draft_for_approval_and_persist_history()
    {
        var (token, userId, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/trade-requests", token, new
        {
            purpose = "Campanha para enviar",
            items = new[] { new { item_id = itemId, qty_requested = 2 } }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using var submitted = await SendAsync(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/submit", token);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var submittedJson = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());
        Assert.Equal("aguardando_aprovacao", submittedJson.RootElement.GetProperty("data").GetProperty("status").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await database.Requests.Include(request => request.History).SingleAsync(request => request.Id == requestId);
        Assert.Equal("trade", saved.Flow);
        Assert.Equal("aguardando_aprovacao", saved.Status);
        Assert.NotNull(saved.SubmittedAt);
        Assert.Equal(userId, saved.History.OrderBy(entry => entry.CreatedAt).Last().UserId);
        Assert.Equal("rascunho", saved.History.OrderBy(entry => entry.CreatedAt).Last().FromStatus);
    }

    [Fact]
    public async Task Trade_approver_can_record_approval_decision_and_justification()
    {
        var (requesterToken, _, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var (approverToken, approverId, _) = await ArrangeAsync(["requests.approve"]);
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/trade-requests", requesterToken, new
        {
            purpose = "Campanha aguardando decisão",
            items = new[] { new { item_id = itemId, qty_requested = 2 } }
        });
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        using var submitted = await SendAsync(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/submit", requesterToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        using var decision = await SendAsync(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/approve", approverToken,
            new { approved = true, justification = "Aprovada para compra." });
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        using var decisionJson = JsonDocument.Parse(await decision.Content.ReadAsStringAsync());
        Assert.Equal("aprovada", decisionJson.RootElement.GetProperty("data").GetProperty("status").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await database.Requests.Include(request => request.History).Include(request => request.Approvals)
            .SingleAsync(request => request.Id == requestId);
        Assert.Equal("trade", saved.Flow);
        Assert.Equal("aprovado", Assert.Single(saved.Approvals).Decision);
        Assert.Equal("Aprovada para compra.", saved.Approvals.Single().Justification);
        Assert.Equal(approverId, saved.Approvals.Single().DecidedById);
        Assert.Equal("aguardando_aprovacao", saved.History.OrderBy(entry => entry.CreatedAt).Last().FromStatus);
    }

    [Fact]
    public async Task Trade_approver_can_reject_a_submitted_request_with_a_recorded_reason()
    {
        var (requesterToken, _, itemId) = await ArrangeAsync(["requests.create"]);
        var (approverToken, _, _) = await ArrangeAsync(["requests.approve"]);
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/trade-requests", requesterToken, new
        {
            purpose = "Campanha não aprovada",
            items = new[] { new { item_id = itemId, qty_requested = 1 } }
        });
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        using var submitted = await SendAsync(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/submit", requesterToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        using var rejected = await SendAsync(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/approve", approverToken,
            new { approved = false, justification = "Fora do orçamento." });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        using var rejectedJson = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal("reprovada", rejectedJson.RootElement.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal("Fora do orçamento.", rejectedJson.RootElement.GetProperty("data").GetProperty("cancelReason").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await database.Requests.Include(request => request.Approvals).SingleAsync(request => request.Id == requestId);
        Assert.Equal("reprovado", Assert.Single(saved.Approvals).Decision);
        Assert.Equal("Fora do orçamento.", saved.Approvals.Single().Justification);
    }

    [Fact]
    public async Task Trade_qr_returns_private_png_only_to_users_with_a_trade_view_permission()
    {
        var requestIndustryId = Guid.NewGuid();
        var otherIndustryId = Guid.NewGuid();
        var requestDepartmentId = Guid.NewGuid();
        var otherDepartmentId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Industries.AddRange(
                new Industry { Id = requestIndustryId, Name = $"QR {requestIndustryId:N}", NameKey = requestIndustryId.ToString("N") },
                new Industry { Id = otherIndustryId, Name = $"QR other {otherIndustryId:N}", NameKey = otherIndustryId.ToString("N") });
            database.Departments.AddRange(
                new Department { Id = requestDepartmentId, Name = $"QR department {requestDepartmentId:N}", NameKey = requestDepartmentId.ToString("N") },
                new Department { Id = otherDepartmentId, Name = $"QR other department {otherDepartmentId:N}", NameKey = otherDepartmentId.ToString("N") });
            await database.SaveChangesAsync();
        }
        var (requesterToken, _, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"], requestIndustryId, requestDepartmentId);
        var deniedToken = (await ArrangeAsync(["requests.create"])).Token;
        var otherIndustryToken = (await ArrangeAsync(["requests.view_own"], otherIndustryId)).Token;
        var departmentViewerToken = (await ArrangeAsync(["requests.view_department"], departmentId: requestDepartmentId)).Token;
        var otherDepartmentToken = (await ArrangeAsync(["requests.view_department"], departmentId: otherDepartmentId)).Token;
        var allViewerToken = (await ArrangeAsync(["requests.view_all"])).Token;
        var (assignedApproverToken, assignedApproverId, _) = await ArrangeAsync(["requests.approve", "requests.view_own"]);
        var operationsToken = (await ArrangeAsync(["stock.receive"], roleName: $"CD/Estoque {Guid.NewGuid():N}")).Token;
        var exitConfirmToken = (await ArrangeAsync(["stock.exit_confirm"], roleName: $"CD/Estoque {Guid.NewGuid():N}")).Token;
        var adminToken = (await ArrangeAdminAsync()).Token;
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/trade-requests", requesterToken, new
        {
            purpose = "QR TRADE",
            items = new[] { new { item_id = itemId, qty_requested = 1 } }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        await using (var approvalScope = factory.Services.CreateAsyncScope())
        {
            var database = approvalScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var roleId = await database.UserRoles.Where(userRole => userRole.UserId == assignedApproverId)
                .Select(userRole => userRole.RoleId).SingleAsync();
            database.RequestApprovals.Add(new RequestApproval { RequestId = requestId, ApproverRoleId = roleId });
            await database.SaveChangesAsync();
        }

        using var denied = await SendAsync(HttpMethod.Get, $"/api/v1/trade-requests/{requestId}/qr", deniedToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var outOfScope = await SendAsync(HttpMethod.Get, $"/api/v1/trade-requests/{requestId}/qr", otherIndustryToken);
        Assert.Equal(HttpStatusCode.NotFound, outOfScope.StatusCode);
        using var departmentQr = await SendAsync(HttpMethod.Get, $"/api/v1/trade-requests/{requestId}/qr", departmentViewerToken);
        Assert.Equal(HttpStatusCode.OK, departmentQr.StatusCode);
        using var otherDepartmentQr = await SendAsync(HttpMethod.Get, $"/api/v1/trade-requests/{requestId}/qr", otherDepartmentToken);
        Assert.Equal(HttpStatusCode.NotFound, otherDepartmentQr.StatusCode);
        using var allViewQr = await SendAsync(HttpMethod.Get, $"/api/v1/trade-requests/{requestId}/qr", allViewerToken);
        Assert.Equal(HttpStatusCode.OK, allViewQr.StatusCode);
        using var assignedApproverQr = await SendAsync(HttpMethod.Get, $"/api/v1/trade-requests/{requestId}/qr", assignedApproverToken);
        Assert.Equal(HttpStatusCode.OK, assignedApproverQr.StatusCode);

        using var qr = await SendAsync(HttpMethod.Get, $"/api/v1/trade-requests/{requestId}/qr", requesterToken);
        Assert.Equal(HttpStatusCode.OK, qr.StatusCode);
        Assert.Equal("image/png", qr.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", qr.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(qr.Headers.CacheControl?.Private);
        var png = await qr.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        using var operationsQr = await SendAsync(HttpMethod.Get, $"/api/v1/trade-requests/{requestId}/qr", operationsToken);
        Assert.Equal(HttpStatusCode.OK, operationsQr.StatusCode);
        using var exitConfirmQr = await SendAsync(HttpMethod.Get, $"/api/v1/trade-requests/{requestId}/qr", exitConfirmToken);
        Assert.Equal(HttpStatusCode.OK, exitConfirmQr.StatusCode);
        using var adminQr = await SendAsync(HttpMethod.Get, $"/api/v1/trade-requests/{requestId}/qr", adminToken);
        Assert.Equal(HttpStatusCode.OK, adminQr.StatusCode);
    }

    [Fact]
    public async Task Trade_qr_does_not_disclose_an_unscoped_request_to_another_requester()
    {
        var (requesterToken, _, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var otherRequesterToken = (await ArrangeAsync(["requests.view_own"])).Token;
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/trade-requests", requesterToken, new
        {
            purpose = "QR restrito ao dono",
            items = new[] { new { item_id = itemId, qty_requested = 1 } }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using var qr = await SendAsync(HttpMethod.Get, $"/api/v1/trade-requests/{requestId}/qr", otherRequesterToken);
        Assert.Equal(HttpStatusCode.NotFound, qr.StatusCode);
    }

    [Fact]
    public async Task Trade_qr_info_preserves_submitted_item_order()
    {
        var (requesterToken, _, firstItemId) = await ArrangeAsync(["requests.create"]);
        var readerToken = (await ArrangeAsync(["requests.view_all"])).Token;
        var secondItemId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var categoryId = await database.CatalogItems.Where(item => item.Id == firstItemId)
                .Select(item => item.CategoryId).SingleAsync();
            database.CatalogItems.Add(new CatalogItem
            {
                Id = secondItemId,
                Code = $"QR-{Guid.NewGuid():N}"[..12],
                Name = "Caneca segunda",
                CategoryId = categoryId
            });
            await database.SaveChangesAsync();
        }

        using var created = await SendAsync(HttpMethod.Post, "/api/v1/trade-requests", requesterToken, new
        {
            purpose = "Ordem dos itens no QR",
            items = new[]
            {
                new { item_id = firstItemId, qty_requested = 1 },
                new { item_id = secondItemId, qty_requested = 1 }
            }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        var publicCode = createdJson.RootElement.GetProperty("data").GetProperty("public_code").GetString();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var persistedLines = await database.RequestItems.Where(line => line.RequestId == requestId).ToListAsync();
            Assert.Equal(2, persistedLines.Count);
            Assert.Equal(0, persistedLines.Single(line => line.ItemId == firstItemId).Position);
            Assert.Equal(1, persistedLines.Single(line => line.ItemId == secondItemId).Position);
            database.RequestItems.RemoveRange(persistedLines);
            await database.SaveChangesAsync();
            database.RequestItems.AddRange(
                new InternalRequestItem
                {
                    Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
                    RequestId = requestId,
                    ItemId = firstItemId,
                    Position = 0,
                    QuantityRequested = 1
                },
                new InternalRequestItem
                {
                    Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                    RequestId = requestId,
                    ItemId = secondItemId,
                    Position = 1,
                    QuantityRequested = 1
                });
            await database.SaveChangesAsync();
        }

        using var response = await SendAsync(HttpMethod.Get,
            $"/api/v1/trade-requests/qr-info?code={Uri.EscapeDataString(publicCode!)}", readerToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var products = json.RootElement.GetProperty("data").GetProperty("products").EnumerateArray().ToArray();
        Assert.Collection(products,
            first => Assert.Equal("Caneca", first.GetProperty("name").GetString()),
            second => Assert.Equal("Caneca segunda", second.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Trade_qr_info_returns_minimal_industry_stock_and_hides_out_of_scope_requests()
    {
        var industryId = Guid.NewGuid();
        var otherIndustryId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Industries.AddRange(
                new Industry { Id = industryId, Name = $"QR info {industryId:N}", NameKey = industryId.ToString("N") },
                new Industry { Id = otherIndustryId, Name = $"QR info other {otherIndustryId:N}", NameKey = otherIndustryId.ToString("N") });
            await database.SaveChangesAsync();
        }

        var (requesterToken, _, itemId) = await ArrangeAsync(["requests.create"], industryId);
        var readerToken = (await ArrangeAsync(["requests.view_all"])).Token;
        var deniedToken = (await ArrangeAsync([])).Token;
        var otherIndustryToken = (await ArrangeAsync(["requests.view_own"], otherIndustryId)).Token;
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/trade-requests", requesterToken, new
        {
            purpose = "Informação mínima do QR",
            recipient = "Dado pessoal que não deve sair",
            notes = "Observação privada",
            items = new[] { new { item_id = itemId, qty_requested = 1 } }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestData = createdJson.RootElement.GetProperty("data");
        var requestId = requestData.GetProperty("id").GetGuid();
        var requestCode = requestData.GetProperty("code").GetString()!;
        var publicCode = requestData.GetProperty("public_code").GetString()!;
        Assert.Equal(requestCode, publicCode);
        Assert.Matches("^EME-[0-9]{4}-[0-9]{6}$", publicCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.StockMovements.AddRange(
                new StockMovement { ItemId = itemId, IndustryId = industryId, Type = "entrada", Quantity = 10, BalanceAfter = 10 },
                new StockMovement { ItemId = itemId, IndustryId = industryId, Type = "saida", Quantity = -3, BalanceAfter = 7 },
                new StockMovement { ItemId = itemId, IndustryId = industryId, Type = "ajuste", Quantity = 2, BalanceAfter = 9 },
                new StockMovement { ItemId = itemId, IndustryId = industryId, Type = "estorno", Quantity = 4, BalanceAfter = 13 },
                new StockMovement { ItemId = itemId, IndustryId = industryId, Type = "transferencia", Quantity = 99, BalanceAfter = 112 },
                new StockMovement { ItemId = itemId, IndustryId = otherIndustryId, Type = "entrada", Quantity = 100, BalanceAfter = 100 });
            await database.SaveChangesAsync();
        }

        var legacyPublicCode = $"EME-LEGACY-{Guid.NewGuid():N}"[..36];
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var request = await database.Requests.SingleAsync(value => value.Id == requestId);
            request.PublicCode = legacyPublicCode;
            await database.SaveChangesAsync();
        }
        var path = $"/api/v1/trade-requests/qr-info?code={Uri.EscapeDataString(legacyPublicCode)}";
        using var response = await SendAsync(HttpMethod.Get, path, readerToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(legacyPublicCode, data.GetProperty("code").GetString());
        Assert.Equal($"QR info {industryId:N}", data.GetProperty("industry").GetString());
        var product = Assert.Single(data.GetProperty("products").EnumerateArray());
        Assert.Equal("Caneca", product.GetProperty("name").GetString());
        Assert.Equal(13, product.GetProperty("saldo").GetInt32());
        Assert.False(data.TryGetProperty("purpose", out _));
        Assert.False(data.TryGetProperty("recipient", out _));
        Assert.False(data.TryGetProperty("requester_id", out _));

        using var denied = await SendAsync(HttpMethod.Get, path, deniedToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var outOfScope = await SendAsync(HttpMethod.Get, path, otherIndustryToken);
        Assert.Equal(HttpStatusCode.NotFound, outOfScope.StatusCode);
    }

    [Fact]
    public async Task Php_trade_qr_info_route_preserves_legacy_path_contract_and_authorization()
    {
        var industryId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Industries.Add(new Industry
            {
                Id = industryId,
                Name = $"Legacy QR {industryId:N}",
                NameKey = industryId.ToString("N")
            });
            await database.SaveChangesAsync();
        }

        var (requesterToken, _, itemId) = await ArrangeAsync(["requests.create"], industryId);
        var readerToken = (await ArrangeAsync(["requests.view_all"])).Token;
        var deniedToken = (await ArrangeAsync([])).Token;
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/trade-requests", requesterToken, new
        {
            purpose = "Fluxo QR legado",
            recipient = "Não deve aparecer",
            items = new[] { new { item_id = itemId, qty_requested = 1 } }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestData = createdJson.RootElement.GetProperty("data");
        var code = requestData.GetProperty("public_code").GetString()!;

        using var response = await SendAsync(HttpMethod.Get,
            $"/api/trade/qr-info?code={Uri.EscapeDataString(code)}", readerToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(code, data.GetProperty("code").GetString());
        Assert.Equal($"Legacy QR {industryId:N}", data.GetProperty("industry").GetString());
        Assert.Equal("Caneca", Assert.Single(data.GetProperty("products").EnumerateArray())
            .GetProperty("name").GetString());
        Assert.False(data.TryGetProperty("recipient", out _));
        Assert.False(data.TryGetProperty("purpose", out _));

        using var denied = await SendAsync(HttpMethod.Get,
            $"/api/trade/qr-info?code={Uri.EscapeDataString(code)}", deniedToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Trade_qr_info_reports_missing_code_with_legacy_validation_fields()
    {
        var readerToken = (await ArrangeAsync(["requests.view_all"])).Token;
        using var response = await SendAsync(HttpMethod.Get, "/api/v1/trade-requests/qr-info", readerToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = json.RootElement.GetProperty("error");
        Assert.Equal("VALIDATION_ERROR", error.GetProperty("code").GetString());
        Assert.Equal("Verifique os campos destacados.", error.GetProperty("message").GetString());
        Assert.Equal("Informe o código da solicitação.", error.GetProperty("fields").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Trade_decision_requires_the_approved_boolean()
    {
        var (requesterToken, _, itemId) = await ArrangeAsync(["requests.create"]);
        var approverToken = (await ArrangeAsync(["requests.approve"])).Token;
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/trade-requests", requesterToken, new
        {
            purpose = "Decisão sem resultado",
            items = new[] { new { item_id = itemId, qty_requested = 1 } }
        });
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        using var submitted = await SendAsync(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/submit", requesterToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        using var decision = await SendAsync(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/approve", approverToken,
            new { justification = "Decisão sem booleano." });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, decision.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await database.Requests.Include(request => request.Approvals).SingleAsync(request => request.Id == requestId);
        Assert.Equal("aguardando_aprovacao", saved.Status);
        Assert.Empty(saved.Approvals);
    }

    [Fact]
    public async Task Trade_cd_receives_uploaded_invoice_privately_and_serves_it_to_authorized_users()
    {
        var (requesterToken, _, itemId) = await ArrangeAsync(["requests.create"]);
        var approverToken = (await ArrangeAsync(["requests.approve"])).Token;
        var receiverToken = (await ArrangeAsync(["stock.receive"], roleName: $"CD/Estoque {Guid.NewGuid():N}")).Token;
        var unrelatedViewerToken = (await ArrangeAsync(["requests.view_own"])).Token;
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/trade-requests", requesterToken, new
        {
            purpose = "Recebimento de campanha",
            items = new[] { new { item_id = itemId, qty_requested = 3 } }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        using var submitted = await SendAsync(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/submit", requesterToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var approved = await SendAsync(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/approve", approverToken,
            new { approved = true, justification = "Compra autorizada." });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        using var purchased = await SendAsync(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/purchase", approverToken,
            new { purchase_ticket_no = "CH-RECEIPT-12" });
        Assert.Equal(HttpStatusCode.OK, purchased.StatusCode);

        var invoiceBytes = Convert.FromBase64String(CreateSignatureDataUri().Split(',')[1]);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(JsonSerializer.Serialize(new[] { new { item_id = itemId, qty = 3 } })), "items");
        using var invoiceContent = new ByteArrayContent(invoiceBytes);
        invoiceContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        multipart.Add(invoiceContent, "invoice", "nf.png");
        using var receiveRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/receive")
        {
            Content = multipart
        };
        receiveRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", receiverToken);
        receiveRequest.Headers.Add("Idempotency-Key", "trade-receive-0001");
        using var received = await _client.SendAsync(receiveRequest);
        Assert.Equal(HttpStatusCode.Created, received.StatusCode);
        using var receivedJson = JsonDocument.Parse(await received.Content.ReadAsStringAsync());
        var data = receivedJson.RootElement.GetProperty("data");
        Assert.Equal("pronta", data.GetProperty("status").GetString());
        Assert.Null(data.GetProperty("invoice_no").GetString());
        Assert.Equal(3, data.GetProperty("items")[0].GetProperty("qty_received").GetInt32());
        var invoiceUrl = data.GetProperty("invoice_url").GetString();
        Assert.Equal($"/api/v1/trade-requests/{requestId}/invoice", invoiceUrl);
        Assert.False(data.TryGetProperty("invoice_path", out _));

        using var invoiceDownload = await SendAsync(HttpMethod.Get, invoiceUrl!, receiverToken);
        Assert.Equal(HttpStatusCode.OK, invoiceDownload.StatusCode);
        Assert.Equal("image/png", invoiceDownload.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", invoiceDownload.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(invoiceDownload.Headers.CacheControl?.Private);
        Assert.True(invoiceDownload.Headers.CacheControl?.NoStore);
        Assert.Equal("attachment", invoiceDownload.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(invoiceBytes, await invoiceDownload.Content.ReadAsByteArrayAsync());
        using var unrelatedInvoice = await SendAsync(HttpMethod.Get, invoiceUrl!, unrelatedViewerToken);
        Assert.Equal(HttpStatusCode.NotFound, unrelatedInvoice.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(3, (await database.Stocks.SingleAsync(stock => stock.ItemId == itemId)).OnHand);
        var movement = await database.StockMovements.SingleAsync(value => value.ItemId == itemId && value.Type == "entrada");
        Assert.Equal(3, movement.Quantity);
        Assert.Null(movement.DocumentRef);
        Assert.Equal("CH-RECEIPT-12", movement.PurchaseTicketNo);
        Assert.Contains(await database.AuditLogs.Where(log => log.EntityId == requestId && log.EntityType == "request")
            .Select(log => log.Action).ToArrayAsync(), action => action == "receive");

        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/receive")
        {
            Content = CreateReceiptForm(itemId, invoiceBytes, 3)
        };
        replayRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", receiverToken);
        replayRequest.Headers.Add("Idempotency-Key", "trade-receive-0001");
        using var replay = await _client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var changedRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/receive")
        {
            Content = CreateReceiptForm(itemId, invoiceBytes, 2)
        };
        changedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", receiverToken);
        changedRequest.Headers.Add("Idempotency-Key", "trade-receive-0001");
        using var changed = await _client.SendAsync(changedRequest);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);

        using var invalidForm = new MultipartFormDataContent();
        invalidForm.Add(new StringContent(JsonSerializer.Serialize(new[] { new { item_id = itemId, qty = 1 } })), "items");
        using var invalidInvoice = new ByteArrayContent("not an invoice"u8.ToArray());
        invalidInvoice.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        invalidForm.Add(invalidInvoice, "invoice", "fake.png");
        using var invalidRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/receive")
        {
            Content = invalidForm
        };
        invalidRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", receiverToken);
        invalidRequest.Headers.Add("Idempotency-Key", "trade-receive-invalid-01");
        using var invalid = await _client.SendAsync(invalidRequest);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal(3, (await database.Stocks.SingleAsync(stock => stock.ItemId == itemId)).OnHand);
        Assert.Single(await database.StockMovements.Where(value => value.ItemId == itemId && value.Type == "entrada").ToListAsync());

        static MultipartFormDataContent CreateReceiptForm(Guid id, byte[] fileBytes, int quantity)
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(JsonSerializer.Serialize(new[] { new { item_id = id, qty = quantity } })), "items");
            var content = new ByteArrayContent(fileBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(content, "invoice", "nf.png");
            return form;
        }
    }

    [Fact]
    public async Task Trade_withdrawal_creates_protocol_debits_stock_and_replays_idempotently()
    {
        var (_, requesterId, itemId) = await ArrangeAsync([]);
        var (operatorToken, _, _) = await ArrangeAsync(["stock.exit"]);
        var requestId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var item = await database.CatalogItems.SingleAsync(value => value.Id == itemId);
            var location = new Location
            {
                Name = $"Withdrawal CD {Guid.NewGuid():N}",
                NameKey = Guid.NewGuid().ToString("N"),
                Kind = "cd"
            };
            database.Locations.Add(location);
            item.LocationId = location.Id;
            database.Requests.Add(new InternalRequest
            {
                Id = requestId,
                Code = $"TRD-{Guid.NewGuid():N}"[..20],
                RequesterId = requesterId,
                Flow = "trade",
                Status = "pronta",
                Purpose = "Retirada de campanha"
            });
            database.RequestItems.Add(new InternalRequestItem
            {
                RequestId = requestId,
                ItemId = itemId,
                QuantityRequested = 3,
                QuantityReceived = 3
            });
            database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 3 });
            database.StockPositions.Add(new StockPosition { ItemId = itemId, LocationId = location.Id, Quantity = 3 });
            await database.SaveChangesAsync();
        }

        var signature = CreateSignatureDataUri();
        using var withdrawal = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/withdraw")
        {
            Content = JsonContent.Create(new
            {
                received_by_name = "Pessoa que retira",
                received_by_document = "123.456.789-00",
                recipient = "Equipe de vendas",
                signature,
                items = new[] { new { item_id = itemId, qty = 1 } }
            })
        };
        withdrawal.Headers.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
        withdrawal.Headers.Add("Idempotency-Key", "trade-withdraw-0001");
        using var created = await _client.SendAsync(withdrawal);
        var createdBody = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, createdBody);
        using var createdJson = JsonDocument.Parse(createdBody);
        var delivery = createdJson.RootElement.GetProperty("data");
        Assert.StartsWith("PROT-", delivery.GetProperty("code").GetString());
        Assert.Equal("retirado", delivery.GetProperty("request_status").GetString());
        Assert.False(delivery.TryGetProperty("signature_path", out _));
        var deliveryId = delivery.GetProperty("id").GetGuid();

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(2, (await verifyDatabase.Stocks.AsNoTracking().SingleAsync(stock => stock.ItemId == itemId)).OnHand);
        Assert.Equal(1, (await verifyDatabase.RequestItems.AsNoTracking().SingleAsync(line => line.RequestId == requestId)).QuantityDelivered);
        var movement = await verifyDatabase.StockMovements.AsNoTracking().SingleAsync(value => value.ItemId == itemId && value.Type == "saida");
        Assert.Equal(deliveryId, movement.DeliveryId);
        var savedDelivery = await verifyDatabase.Deliveries.AsNoTracking().Include(value => value.Items)
            .SingleAsync(value => value.Id == deliveryId);
        Assert.Equal("Pessoa que retira", savedDelivery.ReceivedByName);
        Assert.Equal(1, Assert.Single(savedDelivery.Items).Quantity);
        Assert.True(File.Exists(Path.Combine(factory.SignatureRoot, savedDelivery.SignaturePath.Replace('/', Path.DirectorySeparatorChar))));

        using var replay = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/withdraw")
        {
            Content = JsonContent.Create(new
            {
                received_by_name = "Pessoa que retira",
                received_by_document = "123.456.789-00",
                recipient = "Equipe de vendas",
                signature,
                items = new[] { new { item_id = itemId, qty = 1 } }
            })
        };
        replay.Headers.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
        replay.Headers.Add("Idempotency-Key", "trade-withdraw-0001");
        using var replayResponse = await _client.SendAsync(replay);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.Equal(2, (await verifyDatabase.Stocks.AsNoTracking().SingleAsync(stock => stock.ItemId == itemId)).OnHand);
        Assert.Single(await verifyDatabase.StockMovements.AsNoTracking().Where(movement => movement.ItemId == itemId && movement.Type == "saida").ToListAsync());
        Assert.Single(await verifyDatabase.Deliveries.AsNoTracking().Where(value => value.RequestId == requestId).ToListAsync());

        using var changedRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/withdraw")
        {
            Content = JsonContent.Create(new
            {
                received_by_name = "Pessoa que retira",
                received_by_document = "123.456.789-00",
                recipient = "Equipe de vendas",
                signature,
                items = new[] { new { item_id = itemId, qty = 2 } }
            })
        };
        changedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
        changedRequest.Headers.Add("Idempotency-Key", "trade-withdraw-0001");
        using var conflict = await _client.SendAsync(changedRequest);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Trade_approver_records_purchase_ticket_and_moves_approved_request_to_receipt()
    {
        var (requesterToken, _, itemId) = await ArrangeAsync(["requests.create"]);
        var approverToken = (await ArrangeAsync(["requests.approve"])).Token;
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/trade-requests", requesterToken, new
        {
            purpose = "Compra de campanha",
            items = new[] { new { item_id = itemId, qty_requested = 2 } }
        });
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        using var submitted = await SendAsync(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/submit", requesterToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var approved = await SendAsync(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/approve", approverToken,
            new { approved = true, justification = "Aprovada." });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        using var purchased = await SendAsync(HttpMethod.Post, $"/api/v1/trade-requests/{requestId}/purchase", approverToken,
            new { purchase_ticket_no = "CH-2026-0789" });
        Assert.Equal(HttpStatusCode.OK, purchased.StatusCode);
        using var purchasedJson = JsonDocument.Parse(await purchased.Content.ReadAsStringAsync());
        var data = purchasedJson.RootElement.GetProperty("data");
        Assert.Equal("aguardando_recebimento", data.GetProperty("status").GetString());
        Assert.Equal("CH-2026-0789", data.GetProperty("purchase_ticket_no").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await database.Requests.Include(request => request.History).SingleAsync(request => request.Id == requestId);
        Assert.Equal("CH-2026-0789", saved.PurchaseTicketNo);
        Assert.Equal("aguardando_recebimento", saved.Status);
        Assert.Contains(saved.History, entry => entry.FromStatus == "aprovada" && entry.ToStatus == "compra_realizada");
        Assert.Contains(saved.History, entry => entry.FromStatus == "compra_realizada" && entry.ToStatus == "aguardando_recebimento");
    }

    [Fact]
    public async Task Requester_can_create_a_persisted_draft_with_catalog_item_snapshot()
    {
        var (token, userId, itemId) = await ArrangeAsync(["requests.create"]);
        using var response = await SendAsync(token, new
        {
            department_id = (Guid?)null,
            purpose = "Evento interno",
            recipient = "Equipe de vendas",
            needed_date = new DateOnly(2027, 2, 10),
            items = new[] { new { item_id = itemId, qty_requested = 3, notes = "Cor azul" } }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        var requestId = data.GetProperty("id").GetGuid();
        Assert.Equal("rascunho", data.GetProperty("status").GetString());
        Assert.Equal(userId, data.GetProperty("requester_id").GetGuid());
        Assert.Equal("Evento interno", data.GetProperty("purpose").GetString());
        Assert.Equal(3, data.GetProperty("items")[0].GetProperty("qty_requested").GetInt32());
        Assert.Equal(12.51m, data.GetProperty("total_value").GetDecimal());

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await database.Requests.Include(x => x.Items).Include(x => x.History).SingleAsync(x => x.Id == requestId);
        Assert.Equal("rascunho", saved.Status);
        Assert.Single(saved.Items);
        Assert.Equal(3, saved.Items.Single().QuantityRequested);
        Assert.Equal(12.51m, saved.TotalValue);
        Assert.Single(saved.History);
    }

    [Fact]
    public async Task Creating_a_request_requires_requests_create_permission()
    {
        var (token, _, itemId) = await ArrangeAsync(["items.view"]);
        using var response = await SendAsync(token, new
        {
            purpose = "No permission",
            items = new[] { new { item_id = itemId, qty_requested = 1 } }
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Request_draft_rejects_missing_items_and_duplicate_catalog_items()
    {
        var (token, _, itemId) = await ArrangeAsync(["requests.create"]);
        using var missing = await SendAsync(token, new { purpose = "Empty", items = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);

        using var duplicate = await SendAsync(token, new
        {
            purpose = "Duplicate",
            items = new[] { new { item_id = itemId, qty_requested = 1 }, new { item_id = itemId, qty_requested = 2 } }
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);

        using var nullLine = await SendAsync(token, new { purpose = "Null line", items = new object?[] { null } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, nullLine.StatusCode);
    }

    [Fact]
    public async Task Request_detail_is_visible_to_its_requester_and_view_all_but_hidden_from_other_requesters()
    {
        var (ownerToken, _, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        using var created = await SendAsync(ownerToken, new
        {
            purpose = "Visibility check",
            items = new[] { new { item_id = itemId, qty_requested = 2 } }
        });
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using var ownerDetail = await SendAsync(HttpMethod.Get, $"/api/v1/requests/{requestId}", ownerToken);
        Assert.Equal(HttpStatusCode.OK, ownerDetail.StatusCode);
        using var ownerJson = JsonDocument.Parse(await ownerDetail.Content.ReadAsStringAsync());
        Assert.Equal("Visibility check", ownerJson.RootElement.GetProperty("data").GetProperty("purpose").GetString());
        Assert.Single(ownerJson.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());
        Assert.Single(ownerJson.RootElement.GetProperty("data").GetProperty("history").EnumerateArray());

        var (otherOwnerToken, _, _) = await ArrangeAsync(["requests.view_own"]);
        using var hidden = await SendAsync(HttpMethod.Get, $"/api/v1/requests/{requestId}", otherOwnerToken);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);

        var (allToken, _, _) = await ArrangeAsync(["requests.view_all"]);
        using var allView = await SendAsync(HttpMethod.Get, $"/api/v1/requests/{requestId}", allToken);
        Assert.Equal(HttpStatusCode.OK, allView.StatusCode);
    }

    [Fact]
    public async Task Requester_can_edit_only_their_draft_and_replacing_items_recalculates_total()
    {
        var (ownerToken, _, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        using var created = await SendAsync(ownerToken, new
        {
            purpose = "Before edit",
            items = new[] { new { item_id = itemId, qty_requested = 2 } }
        });
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using var updated = await SendAsync(HttpMethod.Put, $"/api/v1/requests/{requestId}", ownerToken, new
        {
            purpose = "After edit",
            items = new[] { new { item_id = itemId, qty_requested = 5, notes = "Revised" } }
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var updatedJson = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        var data = updatedJson.RootElement.GetProperty("data");
        Assert.Equal("After edit", data.GetProperty("purpose").GetString());
        Assert.Equal(20.85m, data.GetProperty("total_value").GetDecimal());
        Assert.Equal(5, data.GetProperty("items")[0].GetProperty("qty_requested").GetInt32());

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await database.Requests.Include(x => x.Items).SingleAsync(x => x.Id == requestId);
        Assert.Equal("After edit", saved.Purpose);
        Assert.Single(saved.Items);
        Assert.Equal(5, saved.Items.Single().QuantityRequested);
        Assert.Equal(20.85m, saved.TotalValue);

        var otherToken = (await ArrangeAsync(["requests.create", "requests.view_own"])).Token;
        using var forbidden = await SendAsync(HttpMethod.Put, $"/api/v1/requests/{requestId}", otherToken, new { purpose = "Not owner" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Requester_can_submit_draft_and_autoapproval_reserves_available_stock()
    {
        var (token, _, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var (_, processorId, _) = await ArrangeAsync(["requests.process"]);
        var requestId = await CreateRequestAsync(token, itemId, "Submit and reserve", 2);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 1 });
            await database.SaveChangesAsync();
        }

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/submit", token);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, responseBody);
        using var json = JsonDocument.Parse(responseBody);
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("aprovada", data.GetProperty("status").GetString());
        Assert.True(data.GetProperty("needs_purchase").GetBoolean());
        Assert.True(data.GetProperty("submitted_at").GetDateTimeOffset() <= DateTimeOffset.UtcNow);
        Assert.True(data.GetProperty("approved_at").GetDateTimeOffset() <= DateTimeOffset.UtcNow);
        var line = Assert.Single(data.GetProperty("items").EnumerateArray());
        Assert.Equal(2, line.GetProperty("qty_approved").GetInt32());
        Assert.Equal(1, line.GetProperty("qty_reserved").GetInt32());
        Assert.Equal(2, data.GetProperty("history").GetArrayLength());

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stock = await verifyDatabase.Stocks.SingleAsync(entity => entity.ItemId == itemId);
        Assert.Equal(1, stock.Reserved);
        var requestCode = await verifyDatabase.Requests.Where(request => request.Id == requestId)
            .Select(request => request.Code).SingleAsync();
        var notification = await verifyDatabase.Notifications.SingleOrDefaultAsync(value => value.UserId == processorId
            && value.RelatedType == "request" && value.RelatedId == requestId);
        Assert.NotNull(notification);
        Assert.Equal("Nova solicitação", notification.Subject);
        Assert.Equal($"A solicitação {requestCode} foi enviada.", notification.Body);
    }

    [Fact]
    public async Task Matching_approval_rule_routes_submission_to_its_approver_without_reserving_stock()
    {
        var (token, _, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var requestId = await CreateRequestAsync(token, itemId, "Approval required", 12);
        Guid approverRoleId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var approverRole = new ApplicationRole($"quantity-approver-{Guid.NewGuid():N}");
            Assert.True((await roles.CreateAsync(approverRole)).Succeeded);
            approverRoleId = approverRole.Id;
            database.ApprovalRules.Add(new ApprovalRule
            {
                Name = "High quantity",
                Criterion = "quantidade",
                Operator = ">=",
                Value = "10",
                ApproverRoleId = approverRoleId,
                Priority = 10
            });
            database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 20 });
            await database.SaveChangesAsync();
        }

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/submit", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("aguardando_aprovacao", data.GetProperty("status").GetString());
        Assert.False(data.GetProperty("needs_purchase").GetBoolean());

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await verifyDatabase.Requests.Include(request => request.Items).Include(request => request.Approvals).SingleAsync(request => request.Id == requestId);
        Assert.Single(saved.Approvals);
        Assert.Equal("pendente", saved.Approvals.Single().Decision);
        Assert.Equal(approverRoleId, saved.Approvals.Single().ApproverRoleId);
        Assert.Equal(0, saved.Items.Single().QuantityReserved);
        Assert.Equal(0, (await verifyDatabase.Stocks.SingleAsync(stock => stock.ItemId == itemId)).Reserved);
    }

    [Fact]
    public async Task Approval_value_rule_parses_comma_decimal_threshold_like_the_php_engine()
    {
        var (requesterToken, _, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var (_, approverId, _) = await ArrangeAsync(["requests.approve"]);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var item = await database.CatalogItems.SingleAsync(value => value.Id == itemId);
            item.UnitValue = 500000.25m;
            database.ApprovalRules.Add(new ApprovalRule
            {
                Name = "Comma decimal value",
                Criterion = "valor",
                Operator = ">=",
                Value = "1000000,5",
                ApproverUserId = approverId
            });
            await database.SaveChangesAsync();
        }

        var requestId = await CreateRequestAsync(requesterToken, itemId, "Comma decimal approval", 2);
        using var submitted = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/submit", requesterToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var json = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());
        Assert.Equal("aguardando_aprovacao", json.RootElement.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(1000000.50m, json.RootElement.GetProperty("data").GetProperty("total_value").GetDecimal());
    }

    [Fact]
    public async Task Approval_quantity_in_operator_matches_comma_separated_values_like_php_engine()
    {
        var (requesterToken, _, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var (_, approverId, _) = await ArrangeAsync(["requests.approve"]);
        var requestId = await CreateRequestAsync(requesterToken, itemId, "Quantity membership approval", 987654);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.ApprovalRules.Add(new ApprovalRule
            {
                Name = "Quantity membership",
                Criterion = "quantidade",
                Operator = "in",
                Value = "987654,42",
                ApproverUserId = approverId
            });
            await database.SaveChangesAsync();
        }

        using var submitted = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/submit", requesterToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var json = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());
        Assert.Equal("aguardando_aprovacao", json.RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Approval_value_equality_uses_php_float_tolerance()
    {
        var (requesterToken, _, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var (_, approverId, _) = await ArrangeAsync(["requests.approve"]);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var item = await database.CatalogItems.SingleAsync(value => value.Id == itemId);
            item.UnitValue = 500000.75m;
            database.ApprovalRules.Add(new ApprovalRule
            {
                Name = "Float tolerance value",
                Criterion = "valor",
                Operator = "=",
                Value = "1000001.50005",
                ApproverUserId = approverId
            });
            await database.SaveChangesAsync();
        }
        var requestId = await CreateRequestAsync(requesterToken, itemId, "Value equality tolerance", 2);

        using var submitted = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/submit", requesterToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var json = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());
        Assert.Equal(1000001.50m, json.RootElement.GetProperty("data").GetProperty("total_value").GetDecimal());
        Assert.Equal("aguardando_aprovacao", json.RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Assigned_approver_can_approve_and_reserve_stock_with_justification()
    {
        var (requesterToken, _, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var (approverToken, approverId, _) = await ArrangeAsync(["requests.approve"]);
        var requestId = await CreateRequestAsync(requesterToken, itemId, "Approval decision", 2);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.ApprovalRules.Add(new ApprovalRule
            {
                Name = "Assigned approver",
                Criterion = "quantidade",
                Operator = ">=",
                Value = "1",
                ApproverUserId = approverId
            });
            database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 3 });
            await database.SaveChangesAsync();
        }

        using var submitted = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/submit", requesterToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var selfApproval = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/approve", requesterToken,
            new { justification = "Não posso aprovar minha solicitação." });
        Assert.Equal(HttpStatusCode.Forbidden, selfApproval.StatusCode);
        using var missingJustification = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/approve", approverToken,
            new { justification = " " });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingJustification.StatusCode);
        using var approved = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/approve", approverToken,
            new
            {
                justification = "Conferido conforme política.",
                items = new[] { new { item_id = itemId, qty_approved = 1 } }
            });
        var body = await approved.Content.ReadAsStringAsync();
        Assert.True(approved.StatusCode == HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("aprovada", json.RootElement.GetProperty("data").GetProperty("status").GetString());

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await verifyDatabase.Requests.Include(request => request.Items).Include(request => request.Approvals)
            .SingleAsync(request => request.Id == requestId);
        Assert.Equal("aprovado", saved.Approvals.Single().Decision);
        Assert.Equal("Conferido conforme política.", saved.Approvals.Single().Justification);
        Assert.Equal(1, saved.Items.Single().QuantityApproved);
        Assert.Equal(1, saved.Items.Single().QuantityReserved);
        Assert.Equal(1, (await verifyDatabase.Stocks.SingleAsync(stock => stock.ItemId == itemId)).Reserved);
    }

    [Fact]
    public async Task Empty_approval_items_array_uses_requested_quantities_like_php_service()
    {
        var (requesterToken, _, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var (approverToken, approverId, _) = await ArrangeAsync(["requests.approve"]);
        var requestId = await CreateRequestAsync(requesterToken, itemId, "Empty approval quantity list", 2);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var categoryId = await database.CatalogItems.Where(item => item.Id == itemId)
                .Select(item => item.CategoryId).SingleAsync();
            database.ApprovalRules.Add(new ApprovalRule
            {
                Name = "Empty-list approval assignment",
                Criterion = "categoria",
                Operator = "=",
                Value = categoryId.ToString(),
                ApproverUserId = approverId
            });
            database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 3 });
            await database.SaveChangesAsync();
        }

        using var submitted = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/submit", requesterToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var approved = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/approve", approverToken,
            new { justification = "Aprovado sem detalhamento por item.", items = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await verifyDatabase.Requests.Include(request => request.Items).SingleAsync(request => request.Id == requestId);
        Assert.Equal(2, saved.Items.Single().QuantityApproved);
        Assert.Equal(2, saved.Items.Single().QuantityReserved);
        Assert.Equal(2, (await verifyDatabase.Stocks.SingleAsync(stock => stock.ItemId == itemId)).Reserved);
    }

    [Fact]
    public async Task Submitting_for_approval_notifies_users_who_can_process_requests()
    {
        var (requesterToken, _, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var (_, approverId, _) = await ArrangeAsync(["requests.approve"]);
        var (_, processorId, _) = await ArrangeAsync(["requests.process"]);
        var requestId = await CreateRequestAsync(requesterToken, itemId, "Notify request processors", 2);
        Guid categoryId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            categoryId = await database.CatalogItems.Where(item => item.Id == itemId)
                .Select(item => item.CategoryId).SingleAsync();
            database.ApprovalRules.Add(new ApprovalRule
            {
                Name = "Processor notification assignment",
                Criterion = "categoria",
                Operator = "=",
                Value = categoryId.ToString(),
                ApproverUserId = approverId
            });
            await database.SaveChangesAsync();
        }

        using var submitted = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/submit", requesterToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var requestCode = await verifyDatabase.Requests.Where(request => request.Id == requestId)
            .Select(request => request.Code).SingleAsync();
        var notification = await verifyDatabase.Notifications.SingleOrDefaultAsync(value => value.UserId == processorId
            && value.RelatedType == "request" && value.RelatedId == requestId);
        Assert.NotNull(notification);
        Assert.Equal("Nova solicitação", notification.Subject);
        Assert.Equal($"A solicitação {requestCode} foi enviada.", notification.Body);
        Assert.Equal($"/solicitacoes/{requestId}", notification.LinkUrl);
        Assert.Equal("enviado", notification.Status);
    }

    [Fact]
    public async Task Rejection_persists_justification_as_request_cancel_reason()
    {
        var (requesterToken, _, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var (approverToken, approverId, _) = await ArrangeAsync(["requests.approve"]);
        var requestId = await CreateRequestAsync(requesterToken, itemId, "Rejection reason", 2);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var categoryId = await database.CatalogItems.Where(item => item.Id == itemId)
                .Select(item => item.CategoryId).SingleAsync();
            database.ApprovalRules.Add(new ApprovalRule
            {
                Name = "Rejection assignment",
                Criterion = "categoria",
                Operator = "=",
                Value = categoryId.ToString(),
                ApproverUserId = approverId
            });
            await database.SaveChangesAsync();
        }

        using var submitted = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/submit", requesterToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var rejected = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/reject", approverToken,
            new { justification = "Solicitação fora da política." });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await verifyDatabase.Requests.Include(request => request.Approvals).SingleAsync(request => request.Id == requestId);
        Assert.Equal("reprovada", saved.Status);
        Assert.Equal("Solicitação fora da política.", saved.CancelReason);
        Assert.Equal("Solicitação fora da política.", saved.Approvals.Single().Justification);
    }

    [Fact]
    public async Task Assigned_approver_can_list_and_view_only_requests_assigned_to_them()
    {
        var (requesterToken, _, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var (approverToken, approverId, _) = await ArrangeAsync(["requests.approve"]);
        var (unassignedToken, _, _) = await ArrangeAsync(["requests.approve"]);
        var requestId = await CreateRequestAsync(requesterToken, itemId, "Approver visibility", 100);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.ApprovalRules.Add(new ApprovalRule
            {
                Name = "Visibility assignment",
                Criterion = "quantidade",
                Operator = ">=",
                Value = "100",
                ApproverUserId = approverId
            });
            await database.SaveChangesAsync();
        }

        using var submitted = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/submit", requesterToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        using var listed = await SendAsync(HttpMethod.Get, "/api/v1/requests", approverToken);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using var listedJson = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        Assert.Equal(1, listedJson.RootElement.GetProperty("meta").GetProperty("total").GetInt32());
        Assert.Equal(requestId, listedJson.RootElement.GetProperty("data")[0].GetProperty("id").GetGuid());

        using var detail = await SendAsync(HttpMethod.Get, $"/api/v1/requests/{requestId}", approverToken);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);

        using var unassignedList = await SendAsync(HttpMethod.Get, "/api/v1/requests", unassignedToken);
        Assert.Equal(HttpStatusCode.OK, unassignedList.StatusCode);
        using var unassignedJson = JsonDocument.Parse(await unassignedList.Content.ReadAsStringAsync());
        Assert.Equal(0, unassignedJson.RootElement.GetProperty("meta").GetProperty("total").GetInt32());
        using var hiddenDetail = await SendAsync(HttpMethod.Get, $"/api/v1/requests/{requestId}", unassignedToken);
        Assert.Equal(HttpStatusCode.NotFound, hiddenDetail.StatusCode);
    }

    [Fact]
    public async Task Legacy_admin_role_can_decide_a_pending_request_without_an_explicit_approval_claim()
    {
        var (requesterToken, _, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var (_, assignedApproverId, _) = await ArrangeAsync(["requests.approve"]);
        var (adminToken, _) = await ArrangeAdminAsync();
        var requestId = await CreateRequestAsync(requesterToken, itemId, "Administrator approval");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var categoryId = await database.CatalogItems.Where(item => item.Id == itemId).Select(item => item.CategoryId).SingleAsync();
            database.ApprovalRules.Add(new ApprovalRule
            {
                Name = "Admin bypass test",
                Criterion = "categoria",
                Operator = "=",
                Value = categoryId.ToString(),
                ApproverUserId = assignedApproverId
            });
            await database.SaveChangesAsync();
        }

        using var submitted = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/submit", requesterToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var approved = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/approve", adminToken,
            new { justification = "Aprovado pelo administrador." });
        var body = await approved.Content.ReadAsStringAsync();
        Assert.True(approved.StatusCode == HttpStatusCode.OK, body);
        using var approvedJson = JsonDocument.Parse(body);
        Assert.Equal("aprovada", approvedJson.RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Legacy_admin_role_can_list_and_view_requests_without_view_claims()
    {
        var (requesterToken, _, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var (adminToken, _) = await ArrangeAdminAsync();
        var requestId = await CreateRequestAsync(requesterToken, itemId, "Admin list visibility");
        await SetStatusAsync(requestId, "aprovada");

        using var listed = await SendAsync(HttpMethod.Get, "/api/v1/requests?q=Admin%20list%20visibility", adminToken);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using var listedJson = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        Assert.Equal(1, listedJson.RootElement.GetProperty("meta").GetProperty("total").GetInt32());
        Assert.Equal(requestId, listedJson.RootElement.GetProperty("data")[0].GetProperty("id").GetGuid());

        using var detail = await SendAsync(HttpMethod.Get, $"/api/v1/requests/{requestId}", adminToken);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
    }

    [Fact]
    public async Task Legacy_admin_role_can_process_an_approved_request_without_a_process_claim()
    {
        var (requesterToken, requesterId, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var (adminToken, _) = await ArrangeAdminAsync();
        var requestId = await CreateRequestAsync(requesterToken, itemId, "Admin processing");
        using var submitted = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/submit", requesterToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var started = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/start-picking", adminToken);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var ready = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/ready", adminToken);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await verifyDatabase.Requests.SingleAsync(request => request.Id == requestId);
        var notification = await verifyDatabase.Notifications.SingleOrDefaultAsync(value => value.UserId == requesterId
            && value.RelatedType == "request" && value.RelatedId == requestId
            && value.Subject == $"Brindes prontos para retirada — {saved.Code}");
        Assert.NotNull(notification);
        Assert.Equal($"A solicitação {saved.Code} está pronta para retirada/entrega.", notification.Body);
        Assert.Equal($"/solicitacoes/{requestId}", notification.LinkUrl);
        Assert.Equal("enviado", notification.Status);
    }

    [Fact]
    public async Task Legacy_admin_role_can_cancel_another_users_approved_request_and_release_stock()
    {
        var (requesterToken, _, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var (adminToken, _) = await ArrangeAdminAsync();
        var requestId = await CreateRequestAsync(requesterToken, itemId, "Admin cancellation", 2);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 2 });
            await database.SaveChangesAsync();
        }
        using var submitted = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/submit", requesterToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        using var cancelled = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/cancel", adminToken,
            new { reason = "Cancelamento administrativo." });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(0, (await verifyDatabase.Stocks.SingleAsync(stock => stock.ItemId == itemId)).Reserved);
    }

    [Fact]
    public async Task Cancelling_approved_request_requires_privileged_permission_and_releases_reserved_stock()
    {
        var (requesterToken, _, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var requestId = await CreateRequestAsync(requesterToken, itemId, "Cancel approved request", 2);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 2 });
            await database.SaveChangesAsync();
        }
        using var submitted = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/submit", requesterToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        using var ownerDenied = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/cancel", requesterToken,
            new { reason = "Cancelamento solicitado." });
        Assert.Equal(HttpStatusCode.Forbidden, ownerDenied.StatusCode);

        var cancelToken = (await ArrangeAsync(["requests.cancel_any"])).Token;
        using var cancelled = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/cancel", cancelToken,
            new { reason = "Cancelamento autorizado." });
        var body = await cancelled.Content.ReadAsStringAsync();
        Assert.True(cancelled.StatusCode == HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("cancelada", data.GetProperty("status").GetString());
        Assert.Equal("Cancelamento autorizado.", data.GetProperty("cancel_reason").GetString());
        Assert.True(data.GetProperty("cancelled_at").GetDateTimeOffset() <= DateTimeOffset.UtcNow);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await verifyDatabase.Requests.Include(request => request.Items).Include(request => request.History)
            .SingleAsync(request => request.Id == requestId);
        Assert.Equal(0, saved.Items.Single().QuantityReserved);
        Assert.Equal("Cancelamento autorizado.", saved.History.OrderBy(history => history.CreatedAt).Last().Comment);
        Assert.Equal(0, (await verifyDatabase.Stocks.SingleAsync(stock => stock.ItemId == itemId)).Reserved);
    }

    [Fact]
    public async Task Requester_can_cancel_their_own_draft_with_a_default_reason()
    {
        var (token, _, itemId) = await ArrangeAsync(["requests.create"]);
        var requestId = await CreateRequestAsync(token, itemId, "Cancel draft");

        using var cancelled = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/cancel", token, new { reason = " " });
        var body = await cancelled.Content.ReadAsStringAsync();
        Assert.True(cancelled.StatusCode == HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("cancelada", data.GetProperty("status").GetString());
        Assert.Equal("Cancelada", data.GetProperty("cancel_reason").GetString());
        Assert.True(data.GetProperty("cancelled_at").GetDateTimeOffset() <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Request_list_requires_view_permission_and_respects_owner_and_global_scopes()
    {
        var (ownerToken, _, ownerItemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var ownerRequestId = await CreateRequestAsync(ownerToken, ownerItemId, "Owner request");
        await SetStatusAsync(ownerRequestId, "aprovada");

        var (otherToken, _, otherItemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var otherRequestId = await CreateRequestAsync(otherToken, otherItemId, "Other request");
        await SetStatusAsync(otherRequestId, "aprovada");

        using var own = await SendAsync(HttpMethod.Get, "/api/v1/requests", ownerToken);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        using var ownJson = JsonDocument.Parse(await own.Content.ReadAsStringAsync());
        Assert.Equal(1, ownJson.RootElement.GetProperty("meta").GetProperty("total").GetInt32());
        Assert.Equal(ownerRequestId, ownJson.RootElement.GetProperty("data")[0].GetProperty("id").GetGuid());

        using var other = await SendAsync(HttpMethod.Get, "/api/v1/requests", otherToken);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        using var otherJson = JsonDocument.Parse(await other.Content.ReadAsStringAsync());
        Assert.Equal(1, otherJson.RootElement.GetProperty("meta").GetProperty("total").GetInt32());
        Assert.Equal(otherRequestId, otherJson.RootElement.GetProperty("data")[0].GetProperty("id").GetGuid());

        var noViewToken = (await ArrangeAsync(["requests.create"])).Token;
        using var denied = await SendAsync(HttpMethod.Get, "/api/v1/requests", noViewToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var allToken = (await ArrangeAsync(["requests.view_all"])).Token;
        using var all = await SendAsync(HttpMethod.Get, "/api/v1/requests", allToken);
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        using var allJson = JsonDocument.Parse(await all.Content.ReadAsStringAsync());
        Assert.True(allJson.RootElement.GetProperty("meta").GetProperty("total").GetInt32() >= 2);
        var visibleIds = allJson.RootElement.GetProperty("data").EnumerateArray()
            .Select(request => request.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Contains(ownerRequestId, visibleIds);
        Assert.Contains(otherRequestId, visibleIds);
    }

    [Fact]
    public async Task Industry_scoped_requesters_can_only_list_and_read_requests_from_their_industry()
    {
        var sharedIndustryId = Guid.NewGuid();
        var otherIndustryId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Industries.AddRange(
                new Industry { Id = sharedIndustryId, Name = $"Industry-{Guid.NewGuid():N}", NameKey = sharedIndustryId.ToString("N") },
                new Industry { Id = otherIndustryId, Name = $"Industry-{Guid.NewGuid():N}", NameKey = otherIndustryId.ToString("N") });
            await database.SaveChangesAsync();
        }

        var (industryToken, _, industryItemId) = await ArrangeAsync(["requests.create", "requests.view_own"], sharedIndustryId);
        using var crossIndustryCreate = await SendAsync(industryToken, new
        {
            purpose = "Cross-industry attempt",
            industry_id = otherIndustryId,
            items = new[] { new { item_id = industryItemId, qty_requested = 1 } }
        });
        Assert.Equal(HttpStatusCode.Forbidden, crossIndustryCreate.StatusCode);
        var firstIndustryRequestId = await CreateRequestAsync(industryToken, industryItemId, "Industry request one");
        await SetStatusAsync(firstIndustryRequestId, "aprovada");

        var (sameIndustryToken, _, sameIndustryItemId) = await ArrangeAsync(["requests.create", "requests.view_own"], sharedIndustryId);
        var sameIndustryRequestId = await CreateRequestAsync(sameIndustryToken, sameIndustryItemId, "Industry request two");
        await SetStatusAsync(sameIndustryRequestId, "aprovada");

        var (otherIndustryToken, _, otherIndustryItemId) = await ArrangeAsync(["requests.create", "requests.view_own"], otherIndustryId);
        var otherIndustryRequestId = await CreateRequestAsync(otherIndustryToken, otherIndustryItemId, "Other industry request");
        await SetStatusAsync(otherIndustryRequestId, "aprovada");

        using var list = await SendAsync(HttpMethod.Get, "/api/v1/requests", industryToken);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Equal(2, listJson.RootElement.GetProperty("meta").GetProperty("total").GetInt32());
        var visibleIds = listJson.RootElement.GetProperty("data").EnumerateArray()
            .Select(request => request.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Contains(firstIndustryRequestId, visibleIds);
        Assert.Contains(sameIndustryRequestId, visibleIds);
        Assert.DoesNotContain(otherIndustryRequestId, visibleIds);

        using var sameIndustryDetail = await SendAsync(HttpMethod.Get, $"/api/v1/requests/{sameIndustryRequestId}", industryToken);
        Assert.Equal(HttpStatusCode.OK, sameIndustryDetail.StatusCode);
        using var otherIndustryDetail = await SendAsync(HttpMethod.Get, $"/api/v1/requests/{otherIndustryRequestId}", industryToken);
        Assert.Equal(HttpStatusCode.NotFound, otherIndustryDetail.StatusCode);
    }

    [Fact]
    public async Task Request_list_filters_by_search_and_status_and_returns_pagination_counts()
    {
        var (token, _, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var firstId = await CreateRequestAsync(token, itemId, "Search alpha one");
        var secondId = await CreateRequestAsync(token, itemId, "Search alpha two");
        var thirdId = await CreateRequestAsync(token, itemId, "Search beta");
        await SetStatusAsync(firstId, "aprovada");
        await SetStatusAsync(secondId, "aprovada");
        await SetStatusAsync(thirdId, "aguardando_aprovacao");

        using var response = await SendAsync(HttpMethod.Get, "/api/v1/requests?q=Search%20alpha&status=aprovada&page=1&per_page=1", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Single(root.GetProperty("data").EnumerateArray());
        Assert.Equal(2, root.GetProperty("meta").GetProperty("total").GetInt32());
        Assert.Equal(1, root.GetProperty("meta").GetProperty("page").GetInt32());
        Assert.Equal(1, root.GetProperty("meta").GetProperty("per_page").GetInt32());
        Assert.Equal(3, root.GetProperty("meta").GetProperty("counts").GetProperty("all").GetInt32());
        Assert.Equal(2, root.GetProperty("meta").GetProperty("counts").GetProperty("aprovada").GetInt32());

        using var maximumDate = await SendAsync(HttpMethod.Get, "/api/v1/requests?to=9999-12-31", token);
        Assert.Equal(HttpStatusCode.OK, maximumDate.StatusCode);
    }

    [Fact]
    public async Task Only_request_processors_can_start_picking_an_approved_request()
    {
        var (ownerToken, _, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var requestId = await CreateRequestAsync(ownerToken, itemId, "Picking permission");
        var processorToken = (await ArrangeAsync(["requests.process"])).Token;
        using var premature = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/start-picking", processorToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, premature.StatusCode);

        using var submitted = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/submit", ownerToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);

        var requesterToken = (await ArrangeAsync(["requests.view_own"])).Token;
        using var forbidden = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/start-picking", requesterToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var started = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/start-picking", processorToken);
        var startedBody = await started.Content.ReadAsStringAsync();
        Assert.True(started.StatusCode == HttpStatusCode.OK, startedBody);
        using var json = JsonDocument.Parse(startedBody);
        Assert.Equal("em_separacao", json.RootElement.GetProperty("data").GetProperty("status").GetString());

        using var ready = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/ready", processorToken);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var readyJson = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.Equal("pronta", readyJson.RootElement.GetProperty("data").GetProperty("status").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await database.Requests.Include(request => request.History).SingleAsync(request => request.Id == requestId);
        Assert.Equal("pronta", saved.Status);
        Assert.Equal(4, saved.History.Count);
        var finalHistory = saved.History.OrderBy(history => history.CreatedAt).Last();
        Assert.Equal("em_separacao", finalHistory.FromStatus);
        Assert.Equal("pronta", finalHistory.ToStatus);
    }

    [Fact]
    public async Task Delivering_a_ready_internal_request_consumes_reserved_stock_and_replays_the_same_protocol()
    {
        var (requesterToken, requesterId, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var (processorToken, processorId, _) = await ArrangeAsync(["requests.process"]);
        var requestId = await CreateRequestAsync(requesterToken, itemId, "Internal request delivery", 2);
        Guid locationId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var location = new Location { Name = $"Delivery-{Guid.NewGuid():N}", NameKey = Guid.NewGuid().ToString("N"), Kind = "cd" };
            database.Locations.Add(location);
            var item = await database.CatalogItems.SingleAsync(value => value.Id == itemId);
            item.LocationId = location.Id;
            database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 3 });
            database.StockPositions.Add(new StockPosition { ItemId = itemId, LocationId = location.Id, Quantity = 3 });
            await database.SaveChangesAsync();
            locationId = location.Id;
        }

        using var submitted = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/submit", requesterToken);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var started = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/start-picking", processorToken);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var ready = await SendAsync(HttpMethod.Post, $"/api/v1/requests/{requestId}/ready", processorToken);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        var signature = CreateSignatureDataUri();
        using var delivered = await SendDeliveryAsync(requestId, processorToken, "internal-delivery-001", signature);
        var deliveredBody = await delivered.Content.ReadAsStringAsync();
        Assert.True(delivered.StatusCode == HttpStatusCode.Created, deliveredBody);
        using var deliveredJson = JsonDocument.Parse(deliveredBody);
        var deliveryId = deliveredJson.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        var protocolCode = deliveredJson.RootElement.GetProperty("data").GetProperty("code").GetString();

        using var replay = await SendDeliveryAsync(requestId, processorToken, "internal-delivery-001", signature);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(deliveryId, replayJson.RootElement.GetProperty("data").GetProperty("id").GetGuid());

        using var changedPayload = await SendDeliveryAsync(requestId, processorToken, "internal-delivery-001", signature, "Notas diferentes.");
        Assert.Equal(HttpStatusCode.Conflict, changedPayload.StatusCode);
        using var differentKey = await SendDeliveryAsync(requestId, processorToken, "internal-delivery-002", signature);
        Assert.Equal(HttpStatusCode.Conflict, differentKey.StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var delivery = await verify.Deliveries.Include(value => value.Items).SingleAsync(value => value.Id == deliveryId);
        Assert.Equal(requestId, delivery.RequestId);
        Assert.Equal(processorId, delivery.DeliveredById);
        Assert.Equal("Recebedor da solicitação", delivery.ReceivedByName);
        Assert.Equal(2, Assert.Single(delivery.Items).Quantity);
        Assert.True(File.Exists(Path.Combine(factory.SignatureRoot, delivery.SignaturePath.Replace('/', Path.DirectorySeparatorChar))));
        var request = await verify.Requests.Include(value => value.Items).Include(value => value.History).SingleAsync(value => value.Id == requestId);
        Assert.Equal("finalizada", request.Status);
        Assert.Equal(2, Assert.Single(request.Items).QuantityDelivered);
        Assert.Contains(request.History, history => history.FromStatus == "pronta" && history.ToStatus == "finalizada" && history.Comment == $"Protocolo {protocolCode}");
        var stock = await verify.Stocks.SingleAsync(value => value.ItemId == itemId);
        Assert.Equal(1, stock.OnHand);
        Assert.Equal(0, stock.Reserved);
        Assert.Equal(1, (await verify.StockPositions.SingleAsync(value => value.ItemId == itemId && value.LocationId == locationId)).Quantity);
        var movement = await verify.StockMovements.SingleAsync(value => value.DeliveryId == deliveryId);
        Assert.Equal(-2, movement.Quantity);
        Assert.Equal(1, movement.BalanceAfter);
        Assert.Equal(processorId, movement.UserId);
        Assert.Equal(requesterId, request.RequesterId);
        Assert.Single(await verify.Deliveries.Where(value => value.RequestId == requestId).ToListAsync());
        Assert.Single(await verify.AuditLogs.Where(value => value.EntityType == "delivery" && value.EntityId == deliveryId && value.Action == "deliver").ToListAsync());
    }

    [Fact]
    public async Task Internal_request_delivery_requires_process_permission_and_ready_status()
    {
        var (requesterToken, requesterId, itemId) = await ArrangeAsync(["requests.create", "requests.view_own"]);
        var (processorToken, processorId, _) = await ArrangeAsync(["requests.process"]);
        var requestId = await CreateRequestAsync(requesterToken, itemId, "Delivery transition guard");
        var signature = CreateSignatureDataUri();
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var deliveryAuditCount = await verify.AuditLogs.CountAsync(value => value.EntityType == "delivery" && value.Action == "deliver");

        using var denied = await SendDeliveryAsync(requestId, requesterToken, "internal-delivery-denied", signature);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var notReady = await SendDeliveryAsync(requestId, processorToken, "internal-delivery-early", signature);
        Assert.Equal(HttpStatusCode.Conflict, notReady.StatusCode);

        var request = await verify.Requests.SingleAsync(value => value.Id == requestId);
        Assert.Equal("rascunho", request.Status);
        Assert.Empty(await verify.Deliveries.Where(value => value.RequestId == requestId).ToListAsync());
        Assert.Empty(await verify.StockMovements.Where(value => value.ItemId == itemId).ToListAsync());
        Assert.Equal(deliveryAuditCount, await verify.AuditLogs.CountAsync(value => value.EntityType == "delivery" && value.Action == "deliver"));
        Assert.NotEqual(requesterId, processorId);
    }

    [Fact]
    public async Task Legacy_delivery_signature_requires_permission_and_industry_scope_and_uses_private_image_headers()
    {
        var industryId = Guid.NewGuid();
        var otherIndustryId = Guid.NewGuid();
        await using (var industryScope = factory.Services.CreateAsyncScope())
        {
            var database = industryScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Industries.AddRange(
                new Industry { Id = industryId, Name = $"Signature {industryId:N}", NameKey = industryId.ToString("N") },
                new Industry { Id = otherIndustryId, Name = $"Signature other {otherIndustryId:N}", NameKey = otherIndustryId.ToString("N") });
            await database.SaveChangesAsync();
        }

        var (industryToken, industryUserId, _) = await ArrangeAsync(["deliveries.view"], industryId, roleName: "industry");
        var signature = CreateSignatureDataUri();
        var deliveryId = Guid.NewGuid();
        var unsafePathDeliveryId = Guid.NewGuid();
        string otherIndustryEmail;
        await using (var identityScope = factory.Services.CreateAsyncScope())
        {
            var services = identityScope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var industryRole = await roles.FindByNameAsync("industry");
            Assert.NotNull(industryRole);
            industryRole.Slug = "industry";
            Assert.True((await roles.UpdateAsync(industryRole)).Succeeded);

            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            otherIndustryEmail = $"{Guid.NewGuid():N}@example.invalid";
            var otherIndustryUser = new ApplicationUser
            {
                UserName = otherIndustryEmail,
                Email = otherIndustryEmail,
                EmailConfirmed = true,
                FullName = "Outra indústria",
                IndustryId = otherIndustryId
            };
            Assert.True((await users.CreateAsync(otherIndustryUser, "CorrectPassword1")).Succeeded);
            Assert.True((await users.AddToRoleAsync(otherIndustryUser, industryRole.Name!)).Succeeded);

            var storedSignature = services.GetRequiredService<DigitalSignatureStorage>().Store(Guid.NewGuid(), signature);
            database.Deliveries.Add(new Delivery
            {
                Id = deliveryId,
                Code = $"PROT-{Guid.NewGuid():N}"[..20],
                Type = "dia_a_dia",
                IndustryId = industryId,
                DeliveredById = industryUserId,
                ReceivedByName = "Recebedor",
                SignaturePath = storedSignature.Path,
                IdempotencyKeyHash = Guid.NewGuid().ToString("N") + new string('a', 32),
                RequestHash = Guid.NewGuid().ToString("N") + new string('b', 32)
            });
            database.Deliveries.Add(new Delivery
            {
                Id = unsafePathDeliveryId,
                Code = $"PROT-{Guid.NewGuid():N}"[..20],
                Type = "dia_a_dia",
                IndustryId = industryId,
                ReceivedByName = "Caminho inválido",
                SignaturePath = "../outside.png",
                IdempotencyKeyHash = Guid.NewGuid().ToString("N") + new string('a', 32),
                RequestHash = Guid.NewGuid().ToString("N") + new string('b', 32)
            });
            await database.SaveChangesAsync();
        }

        using var otherIndustryLogin = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = otherIndustryEmail, password = "CorrectPassword1" });
        Assert.Equal(HttpStatusCode.OK, otherIndustryLogin.StatusCode);
        using var otherIndustryBody = JsonDocument.Parse(await otherIndustryLogin.Content.ReadAsStringAsync());
        var otherIndustryToken = otherIndustryBody.RootElement.GetProperty("accessToken").GetString()!;
        var deniedToken = (await ArrangeAsync([])).Token;
        var adminToken = (await ArrangeAdminAsync()).Token;

        using var signatureResponse = await SendAsync(HttpMethod.Get, $"/api/deliveries/{deliveryId}/signature", industryToken);
        Assert.Equal(HttpStatusCode.OK, signatureResponse.StatusCode);
        Assert.Equal("image/png", signatureResponse.Content.Headers.ContentType?.MediaType);
        Assert.True(signatureResponse.Headers.CacheControl?.Private);
        Assert.Equal(TimeSpan.FromSeconds(86400), signatureResponse.Headers.CacheControl?.MaxAge);
        Assert.Equal("nosniff", signatureResponse.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(Convert.FromBase64String(signature[(signature.IndexOf(',') + 1)..]), await signatureResponse.Content.ReadAsByteArrayAsync());

        using var denied = await SendAsync(HttpMethod.Get, $"/api/deliveries/{deliveryId}/signature", deniedToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var outOfScope = await SendAsync(HttpMethod.Get, $"/api/deliveries/{deliveryId}/signature", otherIndustryToken);
        Assert.Equal(HttpStatusCode.NotFound, outOfScope.StatusCode);
        using var admin = await SendAsync(HttpMethod.Get, $"/api/deliveries/{deliveryId}/signature", adminToken);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        using var missing = await SendAsync(HttpMethod.Get, $"/api/deliveries/{Guid.NewGuid()}/signature", industryToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var unsafePath = await SendAsync(HttpMethod.Get, $"/api/deliveries/{unsafePathDeliveryId}/signature", industryToken);
        Assert.Equal(HttpStatusCode.NotFound, unsafePath.StatusCode);
    }

    [Fact]
    public async Task Delivery_pdf_is_authorized_scoped_and_returned_as_a_private_download()
    {
        var industryId = Guid.NewGuid();
        var otherIndustryId = Guid.NewGuid();
        await using (var industryScope = factory.Services.CreateAsyncScope())
        {
            var database = industryScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Industries.AddRange(
                new Industry { Id = industryId, Name = $"PDF {industryId:N}", NameKey = industryId.ToString("N") },
                new Industry { Id = otherIndustryId, Name = $"PDF other {otherIndustryId:N}", NameKey = otherIndustryId.ToString("N") });
            await database.SaveChangesAsync();
        }

        var (viewerToken, viewerId, itemId) = await ArrangeAsync(["deliveries.view"], industryId);
        var (otherIndustryToken, _, _) = await ArrangeAsync(["deliveries.view"], otherIndustryId);
        var deniedToken = (await ArrangeAsync([])).Token;
        var deliveryId = Guid.NewGuid();
        var code = $"PROT-{deliveryId:N}"[..20];
        var storedSignature = factory.Services.GetRequiredService<DigitalSignatureStorage>()
            .Store(deliveryId, CreateSignatureDataUri());
        await using (var deliveryScope = factory.Services.CreateAsyncScope())
        {
            var database = deliveryScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Deliveries.Add(new Delivery
            {
                Id = deliveryId,
                Code = code,
                Type = "dia_a_dia",
                IndustryId = industryId,
                DeliveredById = viewerId,
                ReceivedByName = "Recebedor do PDF",
                SignaturePath = storedSignature.Path,
                CreatedAt = DateTimeOffset.UtcNow,
                IdempotencyKeyHash = Guid.NewGuid().ToString("N") + new string('a', 32),
                RequestHash = Guid.NewGuid().ToString("N") + new string('b', 32)
            });
            database.DeliveryItems.Add(new DeliveryItem
            {
                DeliveryId = deliveryId,
                ItemId = itemId,
                Quantity = 2,
                BalanceAfter = 3
            });
            await database.SaveChangesAsync();
        }

        using var pdf = await SendAsync(HttpMethod.Get, $"/api/v1/deliveries/{deliveryId}/pdf", viewerToken);
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", pdf.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal($"{code}.pdf", pdf.Content.Headers.ContentDisposition?.FileNameStar
            ?? pdf.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.True(pdf.Headers.CacheControl?.Private);
        Assert.True(pdf.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", pdf.Headers.GetValues("X-Content-Type-Options").Single());
        var bytes = await pdf.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 8);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
        var pdfStructure = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.True(pdfStructure.Split("/Subtype /Image", StringSplitOptions.None).Length - 1 >= 2,
            "The PDF should embed both the stored signature and the signed verification QR.");

        using var qr = await SendAsync(HttpMethod.Get, $"/api/v1/deliveries/{deliveryId}/qr", viewerToken);
        Assert.Equal(HttpStatusCode.OK, qr.StatusCode);
        using var qrJson = JsonDocument.Parse(await qr.Content.ReadAsStringAsync());
        Assert.Equal(code, qrJson.RootElement.GetProperty("code").GetString());
        Assert.Equal("svg", qrJson.RootElement.GetProperty("format").GetString());
        var qrDataUri = qrJson.RootElement.GetProperty("data_uri").GetString()!;
        Assert.StartsWith("data:image/svg+xml;base64,", qrDataUri);
        var svgBytes = Convert.FromBase64String(qrDataUri["data:image/svg+xml;base64,".Length..]);
        Assert.Contains("<svg", System.Text.Encoding.UTF8.GetString(svgBytes), StringComparison.Ordinal);
        var verificationUri = new Uri(qrJson.RootElement.GetProperty("url").GetString()!);
        Assert.StartsWith("https://gbrindes.example/api/v1/verify/", verificationUri.AbsoluteUri);
        Assert.Contains("expires=", verificationUri.Query, StringComparison.Ordinal);
        Assert.Contains("signature=", verificationUri.Query, StringComparison.Ordinal);
        var expiresText = verificationUri.Query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Single(part => part.StartsWith("?expires=", StringComparison.Ordinal))["?expires=".Length..];
        var expires = long.Parse(expiresText, System.Globalization.CultureInfo.InvariantCulture);
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expires);
        var remaining = expiresAt - DateTimeOffset.UtcNow;
        Assert.InRange(remaining, TimeSpan.FromDays(7) - TimeSpan.FromSeconds(5), TimeSpan.FromDays(7));
        Assert.Equal(expires, qrJson.RootElement.GetProperty("expires_at").GetDateTimeOffset().ToUnixTimeSeconds());

        using var verified = await _client.GetAsync(verificationUri.PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        using var verifiedJson = JsonDocument.Parse(await verified.Content.ReadAsStringAsync());
        Assert.True(verifiedJson.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal(code, verifiedJson.RootElement.GetProperty("protocol").GetString());
        Assert.Equal("dia_a_dia", verifiedJson.RootElement.GetProperty("type").GetString());
        Assert.False(verifiedJson.RootElement.TryGetProperty("requester", out _));
        Assert.False(verifiedJson.RootElement.TryGetProperty("items", out _));
        Assert.False(verifiedJson.RootElement.TryGetProperty("signature_hash", out _));

        var expiredUri = new Uri(verificationUri.AbsoluteUri.Replace($"expires={expires}", "expires=1", StringComparison.Ordinal));
        using var expired = await _client.GetAsync(expiredUri.PathAndQuery);
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);

        using var tampered = await _client.GetAsync(verificationUri.PathAndQuery
            .Replace("signature=", "signature=invalid", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Forbidden, tampered.StatusCode);
        using var alteredCode = await _client.GetAsync(verificationUri.PathAndQuery.Replace(code, $"{code}X", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Forbidden, alteredCode.StatusCode);

        using var denied = await SendAsync(HttpMethod.Get, $"/api/v1/deliveries/{deliveryId}/pdf", deniedToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var qrDenied = await SendAsync(HttpMethod.Get, $"/api/v1/deliveries/{deliveryId}/qr", deniedToken);
        Assert.Equal(HttpStatusCode.Forbidden, qrDenied.StatusCode);
        using var outOfScope = await SendAsync(HttpMethod.Get, $"/api/v1/deliveries/{deliveryId}/pdf", otherIndustryToken);
        Assert.Equal(HttpStatusCode.NotFound, outOfScope.StatusCode);
        using var qrOutOfScope = await SendAsync(HttpMethod.Get, $"/api/v1/deliveries/{deliveryId}/qr", otherIndustryToken);
        Assert.Equal(HttpStatusCode.NotFound, qrOutOfScope.StatusCode);
        using var missing = await SendAsync(HttpMethod.Get, $"/api/v1/deliveries/{Guid.NewGuid()}/pdf", viewerToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Delivery_list_preserves_laravel_pagination_and_assigned_industry_scope()
    {
        var industryId = Guid.NewGuid();
        var otherIndustryId = Guid.NewGuid();
        await using (var industryScope = factory.Services.CreateAsyncScope())
        {
            var database = industryScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Industries.AddRange(
                new Industry { Id = industryId, Name = $"Delivery list {industryId:N}", NameKey = industryId.ToString("N") },
                new Industry { Id = otherIndustryId, Name = $"Delivery list other {otherIndustryId:N}", NameKey = otherIndustryId.ToString("N") });
            await database.SaveChangesAsync();
        }

        var (viewerToken, viewerId, itemId) = await ArrangeAsync(["deliveries.view"], industryId);
        var (otherIndustryToken, otherViewerId, otherItemId) = await ArrangeAsync(["deliveries.view"], otherIndustryId);
        var unscopedViewerToken = (await ArrangeAsync(["deliveries.view"])).Token;
        var deniedToken = (await ArrangeAsync([])).Token;
        var olderId = Guid.NewGuid();
        var newestId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var olderCode = $"DLY-{olderId:N}"[..20];
        var newestCode = $"DLY-{newestId:N}"[..20];
        var otherCode = $"DLY-{otherId:N}"[..20];
        var now = DateTimeOffset.UtcNow;

        Delivery CreateDelivery(Guid id, string code, Guid ownerIndustryId, Guid deliveredById, DateTimeOffset createdAt) => new()
        {
            Id = id,
            Code = code,
            Type = "dia_a_dia",
            IndustryId = ownerIndustryId,
            DeliveredById = deliveredById,
            ReceivedByName = code,
            SignaturePath = $"trade/{id:N}/private.png",
            IdempotencyKeyHash = Guid.NewGuid().ToString("N") + new string('a', 32),
            RequestHash = Guid.NewGuid().ToString("N") + new string('b', 32),
            CreatedAt = createdAt
        };

        await using (var deliveryScope = factory.Services.CreateAsyncScope())
        {
            var database = deliveryScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Deliveries.AddRange(
                CreateDelivery(olderId, olderCode, industryId, viewerId, now.AddDays(-1)),
                CreateDelivery(newestId, newestCode, industryId, viewerId, now),
                CreateDelivery(otherId, otherCode, otherIndustryId, otherViewerId, now.AddMinutes(1)));
            database.DeliveryItems.AddRange(
                new DeliveryItem { DeliveryId = olderId, ItemId = itemId, Quantity = 1, BalanceAfter = 4 },
                new DeliveryItem { DeliveryId = newestId, ItemId = itemId, Quantity = 2, BalanceAfter = 2 },
                new DeliveryItem { DeliveryId = otherId, ItemId = otherItemId, Quantity = 1, BalanceAfter = 5 });
            await database.SaveChangesAsync();
        }

        using var firstPage = await SendAsync(HttpMethod.Get, "/api/v1/deliveries?per_page=1", viewerToken);
        Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
        using var firstJson = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
        var firstBody = firstJson.RootElement;
        Assert.Equal(1, firstBody.GetProperty("current_page").GetInt32());
        Assert.Equal(1, firstBody.GetProperty("per_page").GetInt32());
        Assert.Equal(2, firstBody.GetProperty("total").GetInt32());
        Assert.Equal(2, firstBody.GetProperty("last_page").GetInt32());
        var firstRow = Assert.Single(firstBody.GetProperty("data").EnumerateArray());
        Assert.Equal(newestCode, firstRow.GetProperty("code").GetString());
        Assert.False(firstRow.TryGetProperty("signature_path", out _));
        Assert.Equal(itemId, firstRow.GetProperty("items")[0].GetProperty("item_id").GetGuid());
        Assert.Contains("page=2&per_page=1", firstBody.GetProperty("next_page_url").GetString());

        using var secondPage = await SendAsync(HttpMethod.Get, "/api/v1/deliveries?page=2&per_page=1", viewerToken);
        Assert.Equal(HttpStatusCode.OK, secondPage.StatusCode);
        using var secondJson = JsonDocument.Parse(await secondPage.Content.ReadAsStringAsync());
        Assert.Equal(olderCode, Assert.Single(secondJson.RootElement.GetProperty("data").EnumerateArray()).GetProperty("code").GetString());

        using var defaults = await SendAsync(HttpMethod.Get, "/api/v1/deliveries", viewerToken);
        using var defaultsJson = JsonDocument.Parse(await defaults.Content.ReadAsStringAsync());
        Assert.Equal(25, defaultsJson.RootElement.GetProperty("per_page").GetInt32());
        Assert.Equal(2, defaultsJson.RootElement.GetProperty("data").GetArrayLength());
        using var zeroPageSize = await SendAsync(HttpMethod.Get, "/api/v1/deliveries?per_page=0", viewerToken);
        using var zeroPageSizeJson = JsonDocument.Parse(await zeroPageSize.Content.ReadAsStringAsync());
        Assert.Equal(25, zeroPageSizeJson.RootElement.GetProperty("per_page").GetInt32());
        using var negativePageSize = await SendAsync(HttpMethod.Get, "/api/v1/deliveries?per_page=-2", viewerToken);
        using var negativePageSizeJson = JsonDocument.Parse(await negativePageSize.Content.ReadAsStringAsync());
        Assert.Equal(1, negativePageSizeJson.RootElement.GetProperty("per_page").GetInt32());
        using var oversizedPageSize = await SendAsync(HttpMethod.Get, "/api/v1/deliveries?per_page=200", viewerToken);
        using var oversizedPageSizeJson = JsonDocument.Parse(await oversizedPageSize.Content.ReadAsStringAsync());
        Assert.Equal(100, oversizedPageSizeJson.RootElement.GetProperty("per_page").GetInt32());

        using var denied = await SendAsync(HttpMethod.Get, "/api/v1/deliveries", deniedToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var unscoped = await SendAsync(HttpMethod.Get, "/api/v1/deliveries", unscopedViewerToken);
        Assert.Equal(HttpStatusCode.OK, unscoped.StatusCode);
        using var unscopedJson = JsonDocument.Parse(await unscoped.Content.ReadAsStringAsync());
        Assert.True(unscopedJson.RootElement.GetProperty("total").GetInt32() >= 3);
        var unscopedCodes = unscopedJson.RootElement.GetProperty("data")
            .EnumerateArray().Select(row => row.GetProperty("code").GetString()).ToArray();
        Assert.Contains(olderCode, unscopedCodes);
        Assert.Contains(newestCode, unscopedCodes);
        Assert.Contains(otherCode, unscopedCodes);
        using var otherIndustry = await SendAsync(HttpMethod.Get, "/api/v1/deliveries", otherIndustryToken);
        Assert.Equal(HttpStatusCode.OK, otherIndustry.StatusCode);
        using var otherIndustryJson = JsonDocument.Parse(await otherIndustry.Content.ReadAsStringAsync());
        Assert.Equal(otherCode, Assert.Single(otherIndustryJson.RootElement.GetProperty("data").EnumerateArray()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Delivery_detail_requires_view_permission_and_hides_records_outside_the_assigned_industry()
    {
        var industryId = Guid.NewGuid();
        var otherIndustryId = Guid.NewGuid();
        await using (var industryScope = factory.Services.CreateAsyncScope())
        {
            var database = industryScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Industries.AddRange(
                new Industry { Id = industryId, Name = $"Delivery {industryId:N}", NameKey = industryId.ToString("N") },
                new Industry { Id = otherIndustryId, Name = $"Delivery other {otherIndustryId:N}", NameKey = otherIndustryId.ToString("N") });
            await database.SaveChangesAsync();
        }

        var (viewerToken, viewerId, itemId) = await ArrangeAsync(["deliveries.view"], industryId);
        var otherIndustryToken = (await ArrangeAsync(["deliveries.view"], otherIndustryId)).Token;
        var unscopedViewerToken = (await ArrangeAsync(["deliveries.view"])).Token;
        var deniedToken = (await ArrangeAsync([])).Token;
        var adminToken = (await ArrangeAdminAsync()).Token;
        var permissionManagerToken = (await ArrangeAsync(["roles.manage"])).Token;
        var deliveryId = Guid.NewGuid();
        await using (var deliveryScope = factory.Services.CreateAsyncScope())
        {
            var database = deliveryScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Deliveries.Add(new Delivery
            {
                Id = deliveryId,
                Code = $"PROT-{Guid.NewGuid():N}"[..20],
                Type = "dia_a_dia",
                IndustryId = industryId,
                DeliveredById = viewerId,
                ReceivedByName = "Recebedor autorizado",
                ReceivedByEmail = "receiver@example.invalid",
                ReceivedByDocument = "123456789",
                ReceivedByPhone = "21999999999",
                SignaturePath = $"trade/{deliveryId:N}/private.png",
                Notes = "Conferido",
                IdempotencyKeyHash = Guid.NewGuid().ToString("N") + new string('a', 32),
                RequestHash = Guid.NewGuid().ToString("N") + new string('b', 32)
            });
            database.DeliveryItems.Add(new DeliveryItem
            {
                DeliveryId = deliveryId,
                ItemId = itemId,
                Quantity = 2,
                BalanceAfter = 3
            });
            await database.SaveChangesAsync();
        }

        using var detail = await SendAsync(HttpMethod.Get, $"/api/v1/deliveries/{deliveryId}", viewerToken);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var json = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var body = json.RootElement;
        Assert.Equal(deliveryId, body.GetProperty("id").GetGuid());
        Assert.Equal("dia_a_dia", body.GetProperty("type").GetString());
        Assert.Equal("Recebedor autorizado", body.GetProperty("received_by_name").GetString());
        Assert.False(body.TryGetProperty("signature_path", out _));
        Assert.False(body.TryGetProperty("signaturePath", out _));
        Assert.False(body.TryGetProperty("idempotency_key_hash", out _));
        Assert.False(body.TryGetProperty("request_hash", out _));
        var line = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal(2, line.GetProperty("qty").GetInt32());
        Assert.Equal(3, line.GetProperty("balance_after").GetInt32());
        Assert.Equal(itemId, line.GetProperty("item_id").GetGuid());

        using var denied = await SendAsync(HttpMethod.Get, $"/api/v1/deliveries/{deliveryId}", deniedToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var outOfScope = await SendAsync(HttpMethod.Get, $"/api/v1/deliveries/{deliveryId}", otherIndustryToken);
        Assert.Equal(HttpStatusCode.NotFound, outOfScope.StatusCode);
        using var unscoped = await SendAsync(HttpMethod.Get, $"/api/v1/deliveries/{deliveryId}", unscopedViewerToken);
        Assert.Equal(HttpStatusCode.OK, unscoped.StatusCode);
        using var missing = await SendAsync(HttpMethod.Get, $"/api/v1/deliveries/{Guid.NewGuid()}", viewerToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var admin = await SendAsync(HttpMethod.Get, $"/api/v1/deliveries/{deliveryId}", adminToken);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);

        using var catalogResponse = await SendAsync(HttpMethod.Get, "/api/v1/permissions", permissionManagerToken);
        Assert.Equal(HttpStatusCode.OK, catalogResponse.StatusCode);
        using var catalogJson = JsonDocument.Parse(await catalogResponse.Content.ReadAsStringAsync());
        var permissionSlugs = catalogJson.RootElement.GetProperty("data").EnumerateArray()
            .SelectMany(group => group.GetProperty("permissions").EnumerateArray())
            .Select(permission => permission.GetProperty("slug").GetString());
        Assert.Contains("deliveries.view", permissionSlugs);
    }

    private async Task<Guid> CreateRequestAsync(string token, Guid itemId, string purpose, int quantity = 1)
    {
        using var response = await SendAsync(token, new
        {
            purpose,
            items = new[] { new { item_id = itemId, qty_requested = quantity } }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("data").GetProperty("id").GetGuid();
    }

    private async Task SetStatusAsync(Guid requestId, string status)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var request = await database.Requests.SingleAsync(entity => entity.Id == requestId);
        request.Status = status;
        await database.SaveChangesAsync();
    }

    private async Task<(string Token, Guid UserId, Guid ItemId)> ArrangeAsync(string[] permissions, Guid? industryId = null,
        Guid? departmentId = null, string? roleName = null)
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        Guid userId;
        Guid itemId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var role = new ApplicationRole(roleName ?? $"request-{Guid.NewGuid():N}");
            Assert.True((await roles.CreateAsync(role)).Succeeded);
            foreach (var permission in permissions)
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", permission))).Succeeded);
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Request test", IndustryId = industryId, DepartmentId = departmentId };
            Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role.Name!)).Succeeded);
            userId = user.Id;

            var category = new Category { Name = $"Request-{Guid.NewGuid():N}", NameKey = Guid.NewGuid().ToString("N") };
            var item = new CatalogItem { Code = $"REQ-{Guid.NewGuid():N}"[..12], Name = "Caneca", CategoryId = category.Id, UnitValue = 4.17m };
            database.Categories.Add(category);
            database.CatalogItems.Add(item);
            await database.SaveChangesAsync();
            itemId = item.Id;
        }

        using var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return (body.RootElement.GetProperty("accessToken").GetString()!, userId, itemId);
    }

    private async Task<string> GetUserEmailAsync(Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (await database.Users.SingleAsync(user => user.Id == userId)).Email!;
    }

    private async Task<Guid> CreateDirectoryUserAsync(string name, bool active, DateTimeOffset? deletedAt)
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = name,
            Active = active,
            DeletedAt = deletedAt
        };
        Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
        return user.Id;
    }

    private async Task<(string Token, Guid UserId)> ArrangeAdminAsync()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        Guid userId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var adminRole = await roles.FindByNameAsync("admin");
            if (adminRole is null)
            {
                adminRole = new ApplicationRole("admin");
                Assert.True((await roles.CreateAsync(adminRole)).Succeeded);
            }
            Assert.DoesNotContain(await roles.GetClaimsAsync(adminRole), claim => claim.Type == "permission" && claim.Value == "requests.approve");
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var admin = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Request admin" };
            Assert.True((await users.CreateAsync(admin, "CorrectPassword1")).Succeeded);
            Assert.True((await users.AddToRoleAsync(admin, adminRole.Name!)).Succeeded);
            userId = admin.Id;
        }

        using var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return (body.RootElement.GetProperty("accessToken").GetString()!, userId);
    }

    private Task<HttpResponseMessage> SendAsync(string token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/requests") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object body)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> SendDeliveryAsync(Guid requestId, string token, string idempotencyKey, string signature,
        string notes = "Entrega conferida.")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/requests/{requestId}/deliver")
        {
            Content = JsonContent.Create(new
            {
                received_by_name = "Recebedor da solicitação",
                received_by_email = "recebedor@example.invalid",
                received_by_document = "123456789",
                received_by_phone = "21999999999",
                signature,
                notes
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return _client.SendAsync(request);
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
