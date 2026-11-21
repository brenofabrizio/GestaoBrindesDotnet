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

public sealed class ApprovalRuleEndpointTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task Approval_rule_management_requires_authentication()
    {
        using var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/approval-rules", new
        {
            name = "Aprovação por quantidade",
            criterion = "quantidade",
            @operator = ">=",
            value = "10",
            approver_role_id = Guid.NewGuid()
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Approval_rule_manager_can_create_a_rule_assigned_to_an_existing_role()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        Guid approverRoleId;
        Guid managerId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var roleManager = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var managerRole = new ApplicationRole($"rule-manager-{Guid.NewGuid():N}");
            Assert.True((await roleManager.CreateAsync(managerRole)).Succeeded);
            Assert.True((await roleManager.AddClaimAsync(managerRole, new Claim("permission", "approval_rules.manage"))).Succeeded);
            var approverRole = new ApplicationRole($"rule-approver-{Guid.NewGuid():N}");
            Assert.True((await roleManager.CreateAsync(approverRole)).Succeeded);
            approverRoleId = approverRole.Id;
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Rule manager" };
            Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, managerRole.Name!)).Succeeded);
            managerId = user.Id;
        }

        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginJson.RootElement.GetProperty("accessToken").GetString());
        using var created = await client.PostAsJsonAsync("/api/v1/approval-rules", new
        {
            name = "Quantidade acima de dez",
            criterion = "quantidade",
            @operator = ">=",
            value = "10",
            approver_role_id = approverRoleId,
            approver_user_id = managerId,
            priority = 20,
            active = true
        });

        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var ruleId = createdJson.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var database = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var saved = await database.ApprovalRules.SingleAsync(rule => rule.Id == ruleId);
        Assert.Equal("quantidade", saved.Criterion);
        Assert.Equal(approverRoleId, saved.ApproverRoleId);
        Assert.Equal(managerId, saved.ApproverUserId);
        Assert.Equal(20, saved.Priority);
        var audit = await database.AuditLogs.SingleAsync(log => log.EntityType == "approval_rule" && log.EntityId == ruleId && log.Action == "create");
        Assert.Equal(managerId, audit.UserId);
        Assert.Equal("Quantidade acima de dez", audit.EntityLabel);
        Assert.Null(audit.BeforeJson);
        using var after = JsonDocument.Parse(audit.AfterJson!);
        Assert.Equal("quantidade", after.RootElement.GetProperty("criterion").GetString());
    }

    [Fact]
    public async Task Approval_rule_manager_can_update_and_soft_delete_a_rule()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        Guid ruleId;
        Guid managerId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var managerRole = new ApplicationRole($"rule-editor-{Guid.NewGuid():N}");
            Assert.True((await roles.CreateAsync(managerRole)).Succeeded);
            Assert.True((await roles.AddClaimAsync(managerRole, new Claim("permission", "approval_rules.manage"))).Succeeded);
            var approverRole = new ApplicationRole($"rule-approver-{Guid.NewGuid():N}");
            Assert.True((await roles.CreateAsync(approverRole)).Succeeded);
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Rule editor" };
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
            managerId = user.Id;
            Assert.True((await users.AddToRoleAsync(user, managerRole.Name!)).Succeeded);
            var database = services.GetRequiredService<ApplicationDbContext>();
            var rule = new ApprovalRule
            {
                Name = "Original rule",
                Criterion = "quantidade",
                Operator = ">=",
                Value = "10",
                ApproverRoleId = approverRole.Id
            };
            database.ApprovalRules.Add(rule);
            await database.SaveChangesAsync();
            ruleId = rule.Id;
        }

        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginJson.RootElement.GetProperty("accessToken").GetString());

        using var updated = await client.PutAsJsonAsync($"/api/v1/approval-rules/{ruleId}", new { name = "Updated rule", priority = 50, active = false });
        Assert.True(updated.StatusCode == HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());
        await using (var verifyScope = factory.Services.CreateAsyncScope())
        {
            var database = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var saved = await database.ApprovalRules.SingleAsync(rule => rule.Id == ruleId);
            Assert.Equal("Updated rule", saved.Name);
            Assert.Equal(50, saved.Priority);
            Assert.False(saved.Active);
            Assert.Equal("quantidade", saved.Criterion);
            var audit = await database.AuditLogs.SingleAsync(log => log.EntityType == "approval_rule" && log.EntityId == ruleId && log.Action == "update");
            Assert.Equal(managerId, audit.UserId);
            using var before = JsonDocument.Parse(audit.BeforeJson!);
            using var after = JsonDocument.Parse(audit.AfterJson!);
            Assert.Equal("Original rule", before.RootElement.GetProperty("name").GetString());
            Assert.Equal("Updated rule", after.RootElement.GetProperty("name").GetString());
            Assert.Equal(50, after.RootElement.GetProperty("priority").GetInt32());
        }

        using var deleted = await client.DeleteAsync($"/api/v1/approval-rules/{ruleId}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        using var listed = await client.GetAsync("/api/v1/approval-rules");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using var listedJson = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        Assert.DoesNotContain(listedJson.RootElement.GetProperty("data").EnumerateArray(), rule => rule.GetProperty("id").GetGuid() == ruleId);
        using var secondDelete = await client.DeleteAsync($"/api/v1/approval-rules/{ruleId}");
        Assert.Equal(HttpStatusCode.NotFound, secondDelete.StatusCode);

        await using var deletedScope = factory.Services.CreateAsyncScope();
        var deletedDatabase = deletedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var trashed = await deletedDatabase.ApprovalRules.SingleAsync(rule => rule.Id == ruleId);
        Assert.NotNull(trashed.DeletedAt);
        Assert.False(trashed.Active);
        var deleteAudit = await deletedDatabase.AuditLogs.SingleAsync(log => log.EntityType == "approval_rule" && log.EntityId == ruleId && log.Action == "delete");
        Assert.Equal(managerId, deleteAudit.UserId);
        Assert.Null(deleteAudit.AfterJson);
        using var deletedBefore = JsonDocument.Parse(deleteAudit.BeforeJson!);
        Assert.Equal("Updated rule", deletedBefore.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Legacy_admin_role_can_manage_approval_rules_without_an_explicit_permission_claim()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        Guid approverRoleId;
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
            Assert.DoesNotContain(await roles.GetClaimsAsync(adminRole), claim => claim.Type == "permission" && claim.Value == "approval_rules.manage");

            var approverRole = new ApplicationRole($"admin-rule-approver-{Guid.NewGuid():N}");
            Assert.True((await roles.CreateAsync(approverRole)).Succeeded);
            approverRoleId = approverRole.Id;

            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var admin = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Approval rule admin" };
            Assert.True((await users.CreateAsync(admin, "CorrectPassword1")).Succeeded);
            Assert.True((await users.AddToRoleAsync(admin, adminRole.Name!)).Succeeded);
        }

        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginJson.RootElement.GetProperty("accessToken").GetString());

        using var created = await client.PostAsJsonAsync("/api/v1/approval-rules", new
        {
            name = "Admin-managed rule",
            criterion = "quantidade",
            @operator = ">=",
            value = "50",
            approver_role_id = approverRoleId
        });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var ruleId = createdJson.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using var updated = await client.PutAsJsonAsync($"/api/v1/approval-rules/{ruleId}", new { name = "Admin-updated rule" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var deleted = await client.DeleteAsync($"/api/v1/approval-rules/{ruleId}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
    }
}
