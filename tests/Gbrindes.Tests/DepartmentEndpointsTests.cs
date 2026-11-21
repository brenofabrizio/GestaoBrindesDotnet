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

public sealed class DepartmentEndpointsTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Department_routes_require_lookup_permissions()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/departments")).StatusCode);
        var token = await CreateTokenAsync(["items.view"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/v1/departments", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/api/v1/departments", token, new { name = "Blocked" })).StatusCode);
    }

    [Fact]
    public async Task Department_crud_filters_soft_delete_and_restore_work()
    {
        var token = await CreateTokenAsync(["lookups.view", "lookups.manage", "lookups.delete"]);
        var name = $"Operações-{Guid.NewGuid():N}";
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/departments", token, new { name, description = "Equipe" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdJson.RootElement.GetProperty("id").GetGuid();

        using var get = await SendAsync(HttpMethod.Get, $"/api/v1/departments/{id}", token);
        Assert.Equal(name, JsonDocument.Parse(await get.Content.ReadAsStringAsync()).RootElement.GetProperty("name").GetString());
        using var update = await SendAsync(HttpMethod.Put, $"/api/v1/departments/{id}", token, new { description = "Equipe atualizada" });
        Assert.Equal("Equipe atualizada", JsonDocument.Parse(await update.Content.ReadAsStringAsync()).RootElement.GetProperty("description").GetString());
        using var deactivate = await SendAsync(HttpMethod.Post, $"/api/v1/departments/{id}/deactivate", token);
        Assert.False(JsonDocument.Parse(await deactivate.Content.ReadAsStringAsync()).RootElement.GetProperty("active").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/api/v1/departments/{id}/deactivate", token)).StatusCode);
        using var activate = await SendAsync(HttpMethod.Post, $"/api/v1/departments/{id}/activate", token);
        Assert.True(JsonDocument.Parse(await activate.Content.ReadAsStringAsync()).RootElement.GetProperty("active").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/api/v1/departments/{id}/activate", token)).StatusCode);

        using var delete = await SendAsync(HttpMethod.Delete, $"/api/v1/departments/{id}", token);
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        using var liveList = await SendAsync(HttpMethod.Get, $"/api/v1/departments?q={Uri.EscapeDataString(name)}", token);
        Assert.Empty(JsonDocument.Parse(await liveList.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray());
        using var trash = await SendAsync(HttpMethod.Get, $"/api/v1/departments?trashed=1&q={Uri.EscapeDataString(name)}", token);
        Assert.Single(JsonDocument.Parse(await trash.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray());
        using var restore = await SendAsync(HttpMethod.Post, $"/api/v1/departments/{id}/restore", token);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        using var restoredList = await SendAsync(HttpMethod.Get, $"/api/v1/departments?q={Uri.EscapeDataString(name)}", token);
        Assert.Single(JsonDocument.Parse(await restoredList.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray());

        await using var auditScope = factory.Services.CreateAsyncScope();
        var auditDatabase = auditScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var events = await auditDatabase.AuditLogs.Where(log => log.EntityType == "department" && log.EntityId == id)
            .OrderBy(log => log.CreatedAt).ThenBy(log => log.Id).ToListAsync();
        Assert.Equal(["activate", "create", "deactivate", "delete", "restore", "update"], events.Select(log => log.Action).OrderBy(action => action));
        Assert.All(events, log => Assert.NotNull(log.UserId));
        var persistedDepartment = await auditDatabase.Departments.SingleAsync(value => value.Id == id);
        Assert.Equal(events.Single(log => log.Action == "create").UserId, typeof(Department).GetProperty("CreatedBy")?.GetValue(persistedDepartment));
        Assert.Equal(events.Single(log => log.Action == "activate").UserId, typeof(Department).GetProperty("UpdatedBy")?.GetValue(persistedDepartment));
        Assert.False(createdJson.RootElement.TryGetProperty("createdBy", out _));
        var updateEvent = events.Single(log => log.Action == "update");
        using var updateBefore = JsonDocument.Parse(updateEvent.BeforeJson!);
        using var updateAfter = JsonDocument.Parse(updateEvent.AfterJson!);
        Assert.Equal("Equipe", updateBefore.RootElement.GetProperty("description").GetString());
        Assert.Equal("Equipe atualizada", updateAfter.RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Department_names_are_required_and_case_insensitively_unique()
    {
        var token = await CreateTokenAsync(["lookups.view", "lookups.manage"]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await SendAsync(HttpMethod.Post, "/api/v1/departments", token, new { name = " " })).StatusCode);
        var name = $"Finance-{Guid.NewGuid():N}";
        Assert.Equal(HttpStatusCode.Created,
            (await SendAsync(HttpMethod.Post, "/api/v1/departments", token, new { name })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await SendAsync(HttpMethod.Post, "/api/v1/departments", token, new { name = name.ToUpperInvariant() })).StatusCode);
    }

    [Fact]
    public async Task Department_purge_requires_trash_and_confirmation_then_audits_removal()
    {
        var token = await CreateTokenAsync(["lookups.view", "lookups.manage", "lookups.delete", "lookups.purge"]);
        var name = $"Purge department {Guid.NewGuid():N}";
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/departments", token, new { name });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var active = await SendAsync(HttpMethod.Post, $"/api/v1/departments/{id}/purge", token, new { confirm = name });
        Assert.Equal(HttpStatusCode.Conflict, active.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/departments/{id}", token)).StatusCode);
        using var wrong = await SendAsync(HttpMethod.Post, $"/api/v1/departments/{id}/purge", token, new { confirm = "wrong" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);
        using var purged = await SendAsync(HttpMethod.Post, $"/api/v1/departments/{id}/purge", token, new { confirm = name });
        Assert.Equal(HttpStatusCode.OK, purged.StatusCode);
        Assert.True(JsonDocument.Parse(await purged.Content.ReadAsStringAsync()).RootElement.GetProperty("purged").GetBoolean());

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await database.Departments.AnyAsync(department => department.Id == id));
        var audit = await database.AuditLogs.SingleAsync(log => log.Action == "purge" && log.EntityType == "department" && log.EntityId == id);
        Assert.Equal(name, JsonDocument.Parse(audit.BeforeJson!).RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Department_purge_is_blocked_while_users_reference_it()
    {
        var token = await CreateTokenAsync(["lookups.view", "lookups.manage", "lookups.delete", "lookups.purge"]);
        var name = $"Used department {Guid.NewGuid():N}";
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/departments", token, new { name });
        var departmentId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var reference = new ApplicationUser
            {
                UserName = $"{Guid.NewGuid():N}@example.invalid",
                Email = $"{Guid.NewGuid():N}@example.invalid",
                EmailConfirmed = true,
                FullName = "Department reference",
                DepartmentId = departmentId
            };
            Assert.True((await users.CreateAsync(reference, "CorrectPassword1")).Succeeded);
        }
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/departments/{departmentId}", token)).StatusCode);

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/departments/{departmentId}/purge", token, new { confirm = name });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("IN_USE", await response.Content.ReadAsStringAsync());
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await verify.Departments.AnyAsync(department => department.Id == departmentId));
        Assert.Empty(await verify.AuditLogs.Where(log => log.Action == "purge" && log.EntityType == "department" && log.EntityId == departmentId).ToListAsync());
    }

    [Fact]
    public async Task Department_purge_is_blocked_by_confirmed_stock_movement_without_exit_order()
    {
        var token = await CreateTokenAsync(["lookups.view", "lookups.manage", "lookups.delete", "lookups.purge",
            "items.view", "items.manage", "stock.view", "stock.entry", "stock.exit"]);
        var confirmerToken = await CreateTokenAsync(["stock.exit_confirm"]);
        var departmentName = $"Movement department {Guid.NewGuid():N}";
        using var departmentResponse = await SendAsync(HttpMethod.Post, "/api/v1/departments", token, new { name = departmentName });
        Assert.Equal(HttpStatusCode.Created, departmentResponse.StatusCode);
        var departmentId = JsonDocument.Parse(await departmentResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var categoryResponse = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Category-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, categoryResponse.StatusCode);
        var categoryId = JsonDocument.Parse(await categoryResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var itemResponse = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { code = $"DM-{Guid.NewGuid():N}"[..10], name = "Department movement item", categoryId });
        Assert.Equal(HttpStatusCode.Created, itemResponse.StatusCode);
        var itemId = JsonDocument.Parse(await itemResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var entryResponse = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", token,
            new { quantity = 1, idempotency_key = $"dept-entry-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, entryResponse.StatusCode);

        using var createOrderRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/exits")
        {
            Content = JsonContent.Create(new { item_id = itemId, quantity = 1, purpose = "Department purge reference", department_id = departmentId })
        };
        createOrderRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        createOrderRequest.Headers.Add("Idempotency-Key", $"dept-exit-{Guid.NewGuid():N}");
        using var orderResponse = await _client.SendAsync(createOrderRequest);
        Assert.Equal(HttpStatusCode.Created, orderResponse.StatusCode);
        var orderId = JsonDocument.Parse(await orderResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("data")
            .GetProperty("order").GetProperty("id").GetGuid();

        using var confirmRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/stock/exit-orders/{orderId}/confirm");
        confirmRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", confirmerToken);
        confirmRequest.Headers.Add("Idempotency-Key", $"dept-confirm-{Guid.NewGuid():N}");
        using var confirmation = await _client.SendAsync(confirmRequest);
        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);

        await using (var cleanupScope = factory.Services.CreateAsyncScope())
        {
            var database = cleanupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.StockExitOrders.Remove(await database.StockExitOrders.SingleAsync(order => order.Id == orderId));
            await database.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/departments/{departmentId}", token)).StatusCode);
        using var purge = await SendAsync(HttpMethod.Post, $"/api/v1/departments/{departmentId}/purge", token, new { confirm = departmentName });
        Assert.Equal(HttpStatusCode.Conflict, purge.StatusCode);
        Assert.Contains("IN_USE", await purge.Content.ReadAsStringAsync());
    }

    private async Task<string> CreateTokenAsync(string[] permissions)
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = new ApplicationRole($"department-{Guid.NewGuid():N}");
        Assert.True((await roles.CreateAsync(role)).Succeeded);
        foreach (var permission in permissions)
            Assert.True((await roles.AddClaimAsync(role, new("permission", permission))).Succeeded);
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Department test" };
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
