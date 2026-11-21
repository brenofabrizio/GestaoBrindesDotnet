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

public sealed class RolePermissionEndpointsTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task Role_permissions_are_listed_and_updated_only_with_explicit_known_grants()
    {
        var client = factory.CreateClient();
        var managerEmail = $"{Guid.NewGuid():N}@example.invalid";
        var managerRoleName = $"roles-manager-{Guid.NewGuid():N}";
        var targetRoleName = $"roles-target-{Guid.NewGuid():N}";
        var readOnlyEmail = $"{Guid.NewGuid():N}@example.invalid";
        var readOnlyRoleName = $"roles-readonly-{Guid.NewGuid():N}";
        Guid managerId = Guid.Empty;
        Guid readOnlyId = Guid.Empty;
        Guid readOnlyRoleId = Guid.Empty;
        Guid targetRoleId = Guid.Empty;
        Guid adminRoleId = Guid.Empty;
        var createdAdminRole = false;
        Guid? auditId = null;
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var setupServices = scope.ServiceProvider;
                var setupRoleManager = setupServices.GetRequiredService<RoleManager<ApplicationRole>>();
                var userManager = setupServices.GetRequiredService<UserManager<ApplicationUser>>();
                var managerRole = new ApplicationRole(managerRoleName);
                var setupTargetRole = new ApplicationRole(targetRoleName);
                var readOnlyRole = new ApplicationRole(readOnlyRoleName);
                Assert.True((await setupRoleManager.CreateAsync(managerRole)).Succeeded);
                Assert.True((await setupRoleManager.AddClaimAsync(managerRole, new Claim("permission", "roles.manage"))).Succeeded);
                Assert.True((await setupRoleManager.CreateAsync(setupTargetRole)).Succeeded);
                Assert.True((await setupRoleManager.AddClaimAsync(setupTargetRole, new Claim("permission", "items.view"))).Succeeded);
                Assert.True((await setupRoleManager.CreateAsync(readOnlyRole)).Succeeded);
                var manager = new ApplicationUser
                {
                    UserName = managerEmail,
                    Email = managerEmail,
                    EmailConfirmed = true,
                    FullName = "Role permission manager"
                };
                var readOnlyUser = new ApplicationUser
                {
                    UserName = readOnlyEmail,
                    Email = readOnlyEmail,
                    EmailConfirmed = true,
                    FullName = "Role permission reader"
                };
                Assert.True((await userManager.CreateAsync(manager, "CorrectPassword1")).Succeeded);
                Assert.True((await userManager.AddToRoleAsync(manager, managerRoleName)).Succeeded);
                Assert.True((await userManager.CreateAsync(readOnlyUser, "CorrectPassword1")).Succeeded);
                Assert.True((await userManager.AddToRoleAsync(readOnlyUser, readOnlyRoleName)).Succeeded);
                managerId = manager.Id;
                readOnlyId = readOnlyUser.Id;
                readOnlyRoleId = readOnlyRole.Id;
                targetRoleId = setupTargetRole.Id;
                var adminRole = await setupRoleManager.FindByNameAsync("admin");
                if (adminRole is null)
                {
                    adminRole = new ApplicationRole("admin");
                    Assert.True((await setupRoleManager.CreateAsync(adminRole)).Succeeded);
                    createdAdminRole = true;
                }
                adminRoleId = adminRole.Id;
            }

            using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = managerEmail, password = "CorrectPassword1" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            var token = loginBody.RootElement.GetProperty("accessToken").GetString()!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var permissions = await client.GetAsync("/api/v1/permissions");
            Assert.Equal(HttpStatusCode.OK, permissions.StatusCode);
            using var permissionCatalog = JsonDocument.Parse(await permissions.Content.ReadAsStringAsync());
            Assert.Contains(permissionCatalog.RootElement.GetProperty("data").EnumerateArray()
                .SelectMany(group => group.GetProperty("permissions").EnumerateArray())
                .Select(permission => permission.GetProperty("slug").GetString()), slug => slug == "audit.view");
            Assert.Contains(permissionCatalog.RootElement.GetProperty("data").EnumerateArray()
                .SelectMany(group => group.GetProperty("permissions").EnumerateArray())
                .Select(permission => permission.GetProperty("slug").GetString()), slug => slug == "settings.manage");
            using var adminEdit = await client.PutAsJsonAsync($"/api/v1/roles/{adminRoleId}/permissions", new { permissions = Array.Empty<string>() });
            Assert.Equal(HttpStatusCode.Conflict, adminEdit.StatusCode);

            using var readOnlyLogin = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = readOnlyEmail, password = "CorrectPassword1" });
            Assert.Equal(HttpStatusCode.OK, readOnlyLogin.StatusCode);
            using var readOnlyBody = JsonDocument.Parse(await readOnlyLogin.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", readOnlyBody.RootElement.GetProperty("accessToken").GetString());
            using var forbidden = await client.GetAsync("/api/v1/roles");
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var listed = await client.GetAsync("/api/v1/roles");
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            using var listedBody = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
            var target = listedBody.RootElement.GetProperty("data").EnumerateArray()
                .Single(role => role.GetProperty("id").GetGuid() == targetRoleId);
            Assert.Contains("items.view", target.GetProperty("permissions").EnumerateArray().Select(value => value.GetString()));
            Assert.True(target.GetProperty("editable").GetBoolean());

            using var changed = await client.PutAsJsonAsync($"/api/v1/roles/{targetRoleId}/permissions", new
            {
                permissions = new[] { "requests.create", "items.view", "items.view" }
            });
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

            using var unknown = await client.PutAsJsonAsync($"/api/v1/roles/{targetRoleId}/permissions", new
            {
                permissions = new[] { "not.a.real.permission" }
            });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var services = verifyScope.ServiceProvider;
            var roleManager = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var targetRole = await roleManager.FindByIdAsync(targetRoleId.ToString());
            Assert.NotNull(targetRole);
            var grants = await roleManager.GetClaimsAsync(targetRole!);
            Assert.Equal(new[] { "items.view", "requests.create" }, grants
                .Where(claim => claim.Type == "permission").Select(claim => claim.Value).OrderBy(value => value).ToArray());
            var database = services.GetRequiredService<ApplicationDbContext>();
            var audit = await database.AuditLogs.SingleAsync(log => log.EntityType == "role" && log.EntityId == targetRoleId);
            auditId = audit.Id;
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
            if (auditId is not null || targetRoleId != Guid.Empty)
            {
                database.AuditLogs.RemoveRange(await database.AuditLogs
                    .Where(log => log.EntityType == "role" && log.EntityId == targetRoleId).ToListAsync());
                await database.SaveChangesAsync();
            }
            var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
            if (managerId != Guid.Empty)
            {
                var manager = await userManager.FindByIdAsync(managerId.ToString());
                if (manager is not null)
                    Assert.True((await userManager.DeleteAsync(manager)).Succeeded);
            }
            if (readOnlyId != Guid.Empty)
            {
                var readOnlyUser = await userManager.FindByIdAsync(readOnlyId.ToString());
                if (readOnlyUser is not null)
                    Assert.True((await userManager.DeleteAsync(readOnlyUser)).Succeeded);
            }
            var roleManager = services.GetRequiredService<RoleManager<ApplicationRole>>();
            foreach (var id in new[] { targetRoleId, readOnlyRoleId, await FindRoleIdAsync(roleManager, managerRoleName), await FindRoleIdAsync(roleManager, readOnlyRoleName), createdAdminRole ? adminRoleId : Guid.Empty })
            {
                if (id == Guid.Empty)
                    continue;
                var role = await roleManager.FindByIdAsync(id.ToString());
                if (role is not null)
                    Assert.True((await roleManager.DeleteAsync(role)).Succeeded);
            }
        }
    }

    private static async Task<Guid> FindRoleIdAsync(RoleManager<ApplicationRole> roles, string name)
    {
        var role = await roles.FindByNameAsync(name);
        return role?.Id ?? Guid.Empty;
    }
}

public sealed class RoleMetadataEndpointTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task Role_metadata_can_be_updated_without_changing_explicit_permission_grants()
    {
        var client = factory.CreateClient();
        var managerEmail = $"{Guid.NewGuid():N}@example.invalid";
        var managerRoleName = $"metadata-manager-{Guid.NewGuid():N}";
        var targetRoleName = $"metadata-target-{Guid.NewGuid():N}";
        var slug = $"target-{Guid.NewGuid():N}";
        Guid managerId = Guid.Empty;
        Guid managerRoleId = Guid.Empty;
        Guid targetRoleId = Guid.Empty;
        Guid adminRoleId = Guid.Empty;
        var createdAdminRole = false;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var managerRole = new ApplicationRole(managerRoleName);
                var targetRole = new ApplicationRole(targetRoleName);
                Assert.True((await roles.CreateAsync(managerRole)).Succeeded);
                Assert.True((await roles.AddClaimAsync(managerRole, new Claim("permission", "roles.manage"))).Succeeded);
                Assert.True((await roles.CreateAsync(targetRole)).Succeeded);
                Assert.True((await roles.AddClaimAsync(targetRole, new Claim("permission", "items.view"))).Succeeded);
                var adminRole = await roles.FindByNameAsync("admin");
                if (adminRole is null)
                {
                    adminRole = new ApplicationRole("admin");
                    Assert.True((await roles.CreateAsync(adminRole)).Succeeded);
                    createdAdminRole = true;
                }
                var manager = new ApplicationUser
                {
                    UserName = managerEmail,
                    Email = managerEmail,
                    EmailConfirmed = true,
                    FullName = "Metadata manager"
                };
                Assert.True((await users.CreateAsync(manager, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(manager, managerRoleName)).Succeeded);
                Assert.True((await users.AddToRoleAsync(manager, "admin")).Succeeded);
                managerId = manager.Id;
                managerRoleId = managerRole.Id;
                targetRoleId = targetRole.Id;
                adminRoleId = adminRole.Id;
            }

            using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = managerEmail, password = "CorrectPassword1" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginBody.RootElement.GetProperty("accessToken").GetString());

            using var updated = await client.PutAsJsonAsync($"/api/v1/roles/{targetRoleId}/metadata", new { slug, description = "Perfil configurado manualmente" });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            using var updatedBody = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
            var data = updatedBody.RootElement.GetProperty("data");
            Assert.Equal(slug, data.GetProperty("slug").GetString());
            Assert.Equal("Perfil configurado manualmente", data.GetProperty("description").GetString());
            Assert.Contains("items.view", data.GetProperty("permissions").EnumerateArray().Select(value => value.GetString()));

            using var listed = await client.GetAsync("/api/v1/roles");
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            using var listedBody = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
            var listedRole = listedBody.RootElement.GetProperty("data").EnumerateArray()
                .Single(role => role.GetProperty("id").GetGuid() == targetRoleId);
            Assert.Equal(slug, listedRole.GetProperty("slug").GetString());
            Assert.Equal("Perfil configurado manualmente", listedRole.GetProperty("description").GetString());

            foreach (var invalidSlug in new[] { "../admin", new string('a', 41) })
            {
                using var invalid = await client.PutAsJsonAsync($"/api/v1/roles/{targetRoleId}/metadata", new { slug = invalidSlug, description = "Inválido" });
                Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
            }
            using var invalidDescription = await client.PutAsJsonAsync($"/api/v1/roles/{targetRoleId}/metadata", new { slug, description = new string('x', 256) });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidDescription.StatusCode);

            using var duplicateSlug = await client.PutAsJsonAsync($"/api/v1/roles/{managerRoleId}/metadata", new { slug, description = "Duplicado" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicateSlug.StatusCode);
            using var protectedAdmin = await client.PutAsJsonAsync($"/api/v1/roles/{adminRoleId}/metadata", new { slug = "admin", description = "Não editável" });
            Assert.Equal(HttpStatusCode.Conflict, protectedAdmin.StatusCode);

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var roleManager = verifyScope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            var persistedRole = await roleManager.FindByIdAsync(targetRoleId.ToString());
            Assert.NotNull(persistedRole);
            Assert.Contains("items.view", (await roleManager.GetClaimsAsync(persistedRole!)).Select(claim => claim.Value));
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            database.AuditLogs.RemoveRange(await database.AuditLogs
                .Where(log => log.EntityType == "role" && log.EntityId == targetRoleId).ToListAsync());
            await database.SaveChangesAsync();
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var manager = await users.FindByIdAsync(managerId.ToString());
            if (manager is not null) Assert.True((await users.DeleteAsync(manager)).Succeeded);
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            foreach (var id in new[] { targetRoleId, managerRoleId, createdAdminRole ? adminRoleId : Guid.Empty })
            {
                var role = await roles.FindByIdAsync(id.ToString());
                if (role is not null) Assert.True((await roles.DeleteAsync(role)).Succeeded);
            }
        }
    }
}

[Collection("PostgreSQL integration")]
public sealed class PostgreSqlRoleMetadataEndpointTests(PostgreSqlWebApplicationFactory factory)
    : IClassFixture<PostgreSqlWebApplicationFactory>
{
    [PostgreSqlFact]
    public async Task Role_metadata_and_audit_persist_without_modifying_grants_in_postgres()
    {
        var client = factory.CreateClient();
        var managerEmail = $"{Guid.NewGuid():N}@example.invalid";
        var managerRoleName = $"metadata-manager-{Guid.NewGuid():N}";
        var targetRoleName = $"metadata-target-{Guid.NewGuid():N}";
        var slug = $"target-{Guid.NewGuid():N}";
        Guid managerId = Guid.Empty;
        Guid managerRoleId = Guid.Empty;
        Guid targetRoleId = Guid.Empty;
        Guid adminRoleId = Guid.Empty;
        var createdAdminRole = false;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var setupServices = scope.ServiceProvider;
                var roles = setupServices.GetRequiredService<RoleManager<ApplicationRole>>();
                var users = setupServices.GetRequiredService<UserManager<ApplicationUser>>();
                var managerRole = new ApplicationRole(managerRoleName);
                var setupTargetRole = new ApplicationRole(targetRoleName);
                Assert.True((await roles.CreateAsync(managerRole)).Succeeded);
                Assert.True((await roles.AddClaimAsync(managerRole, new Claim("permission", "roles.manage"))).Succeeded);
                Assert.True((await roles.CreateAsync(setupTargetRole)).Succeeded);
                Assert.True((await roles.AddClaimAsync(setupTargetRole, new Claim("permission", "items.view"))).Succeeded);
                var adminRole = await roles.FindByNameAsync("admin");
                if (adminRole is null)
                {
                    adminRole = new ApplicationRole("admin");
                    Assert.True((await roles.CreateAsync(adminRole)).Succeeded);
                    createdAdminRole = true;
                }
                var manager = new ApplicationUser
                {
                    UserName = managerEmail,
                    Email = managerEmail,
                    EmailConfirmed = true,
                    FullName = "PostgreSQL metadata manager"
                };
                Assert.True((await users.CreateAsync(manager, "CorrectPassword1")).Succeeded);
                Assert.True((await users.AddToRoleAsync(manager, managerRoleName)).Succeeded);
                Assert.True((await users.AddToRoleAsync(manager, "admin")).Succeeded);
                managerId = manager.Id;
                managerRoleId = managerRole.Id;
                targetRoleId = setupTargetRole.Id;
                adminRoleId = adminRole.Id;
            }

            using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = managerEmail, password = "CorrectPassword1" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginBody.RootElement.GetProperty("accessToken").GetString());
            using var updated = await client.PutAsJsonAsync($"/api/v1/roles/{targetRoleId}/metadata", new { slug, description = "Persisted metadata" });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            using var protectedAdmin = await client.PutAsJsonAsync($"/api/v1/roles/{adminRoleId}/metadata", new { slug = "admin", description = "Not editable" });
            Assert.Equal(HttpStatusCode.Conflict, protectedAdmin.StatusCode);

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var services = verifyScope.ServiceProvider;
            var rolesManager = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var targetRole = await rolesManager.FindByIdAsync(targetRoleId.ToString());
            Assert.NotNull(targetRole);
            Assert.Equal(slug, targetRole!.Slug);
            Assert.Equal("Persisted metadata", targetRole.Description);
            Assert.Contains("items.view", (await rolesManager.GetClaimsAsync(targetRole)).Select(claim => claim.Value));
            var database = services.GetRequiredService<ApplicationDbContext>();
            var audit = await database.AuditLogs.SingleAsync(log => log.EntityType == "role" && log.EntityId == targetRoleId);
            Assert.Equal("metadata_update", audit.Action);
            Assert.Equal(managerId, audit.UserId);
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            database.AuditLogs.RemoveRange(await database.AuditLogs
                .Where(log => log.EntityType == "role" && log.EntityId == targetRoleId).ToListAsync());
            await database.SaveChangesAsync();
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var manager = await users.FindByIdAsync(managerId.ToString());
            if (manager is not null) Assert.True((await users.DeleteAsync(manager)).Succeeded);
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            foreach (var id in new[] { targetRoleId, managerRoleId, createdAdminRole ? adminRoleId : Guid.Empty })
            {
                var role = await roles.FindByIdAsync(id.ToString());
                if (role is not null) Assert.True((await roles.DeleteAsync(role)).Succeeded);
            }
        }
    }
}
