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

public sealed class AuditEndpointsTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task Audit_viewer_can_filter_paginate_and_read_audit_entries()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"audit-viewer-{Guid.NewGuid():N}";
        var auditId = Guid.NewGuid();
        var entityId = Guid.NewGuid();
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
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "audit.view"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Auditor" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                userId = user.Id;
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
                database.AuditLogs.AddRange(
                    new AuditLog
                    {
                        Id = auditId,
                        UserId = user.Id,
                        UserName = user.FullName,
                        Action = "create",
                        EntityType = "event",
                        EntityId = entityId,
                        EntityLabel = "Feirão Sul",
                        BeforeJson = "{\"status\":null}",
                        AfterJson = "{\"status\":\"planejado\"}",
                        CreatedAt = new DateTimeOffset(2026, 1, 15, 12, 30, 0, TimeSpan.Zero)
                    },
                    new AuditLog
                    {
                        UserId = user.Id,
                        UserName = user.FullName,
                        Action = "create",
                        EntityType = "event",
                        EntityId = entityId,
                        EntityLabel = "Feirão Sul",
                        CreatedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)
                    },
                    new AuditLog
                    {
                        UserId = user.Id,
                        UserName = user.FullName,
                        Action = "update",
                        EntityType = "event",
                        EntityLabel = "Outro evento",
                        CreatedAt = new DateTimeOffset(2026, 1, 16, 12, 30, 0, TimeSpan.Zero)
                    });
                await database.SaveChangesAsync();
            }

            using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            var token = loginJson.RootElement.GetProperty("accessToken").GetString();
            Assert.False(string.IsNullOrWhiteSpace(token));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var filtered = await client.GetAsync($"/api/v1/audit?entity_type=event&entity_id={entityId}&user_id={userId}&action=create&from=2026-01-01&to=2026-01-31&q=Feir%C3%A3o&page=1&per_page=1");
            Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
            using var json = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
            var root = json.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.Equal(1, root.GetProperty("data").GetArrayLength());
            Assert.Equal(1, root.GetProperty("meta").GetProperty("page").GetInt32());
            Assert.Equal(1, root.GetProperty("meta").GetProperty("per_page").GetInt32());
            Assert.Equal(1, root.GetProperty("meta").GetProperty("total").GetInt32());
            Assert.Equal("Cadastro", root.GetProperty("meta").GetProperty("filters").GetProperty("actions").GetProperty("create").GetString());
            Assert.Equal("Evento", root.GetProperty("meta").GetProperty("filters").GetProperty("entities").GetProperty("event").GetString());
            var listEntry = root.GetProperty("data")[0];
            Assert.Equal("Feirão Sul", listEntry.GetProperty("entity_name").GetString());
            Assert.Equal("Auditor", listEntry.GetProperty("user").GetProperty("name").GetString());

            using var malformedIds = await client.GetAsync($"/api/v1/audit?entity_id=not-a-guid&user_id=not-a-guid&action=create&to=9999-12-31&q=Feir%C3%A3o");
            Assert.Equal(HttpStatusCode.OK, malformedIds.StatusCode);
            using var malformedJson = JsonDocument.Parse(await malformedIds.Content.ReadAsStringAsync());
            Assert.Equal(2, malformedJson.RootElement.GetProperty("meta").GetProperty("total").GetInt32());

            using var detail = await client.GetAsync($"/api/audit/{auditId}");
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            var entry = detailJson.RootElement.GetProperty("data");
            Assert.Equal(auditId, entry.GetProperty("id").GetGuid());
            Assert.Equal("create", entry.GetProperty("action").GetString());
            Assert.Equal("Cadastro", entry.GetProperty("action_label").GetString());
            Assert.Equal("Evento", entry.GetProperty("entity_label").GetString());
            Assert.Equal("Feirão Sul", entry.GetProperty("entity_name").GetString());
            Assert.Equal("planejado", entry.GetProperty("after").GetProperty("status").GetString());
            using var missing = await client.GetAsync($"/api/v1/audit/{Guid.NewGuid()}");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            var entries = userId is Guid cleanupUserId
                ? await database.AuditLogs.Where(log => log.Id == auditId || log.UserId == cleanupUserId).ToListAsync()
                : await database.AuditLogs.Where(log => log.Id == auditId).ToListAsync();
            database.AuditLogs.RemoveRange(entries);
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

    [Fact]
    public async Task Audit_list_and_detail_require_audit_view_permission()
    {
        var client = factory.CreateClient();
        using var anonymous = await client.GetAsync("/api/v1/audit");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"audit-denied-{Guid.NewGuid():N}";
        Guid? userId = null;
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                Assert.True((await roles.CreateAsync(new ApplicationRole(roleName))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "No audit permission" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                userId = user.Id;
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
            }
            using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginJson.RootElement.GetProperty("accessToken").GetString());
            using var list = await client.GetAsync("/api/v1/audit");
            Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
            using var detail = await client.GetAsync($"/api/audit/{Guid.NewGuid()}");
            Assert.Equal(HttpStatusCode.Forbidden, detail.StatusCode);
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
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
}
