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
using Microsoft.Extensions.DependencyInjection;

namespace Gbrindes.Tests;

public sealed class SettingsEndpointsTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Public_settings_returns_safe_default_branding_without_authentication()
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.ApplicationSettings.RemoveRange(database.ApplicationSettings);
            database.AuditLogs.RemoveRange(database.AuditLogs.Where(log => log.EntityType == "settings"));
            await database.SaveChangesAsync();
        }
        using var response = await _client.GetAsync("/api/v1/settings/public");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        var data = body.RootElement.GetProperty("data");
        Assert.Equal("Controle de Brindes", data.GetProperty("company_name").GetString());
        Assert.Equal("#2563EB", data.GetProperty("primary_color").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("logo_url").ValueKind);
        Assert.DoesNotContain("lecom", body.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", body.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Settings_manage_can_update_branding_and_persist_audit()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"settings-{Guid.NewGuid():N}";
        Guid userId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = new ApplicationRole(roleName);
            Assert.True((await roles.CreateAsync(role)).Succeeded);
            Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "settings.manage"))).Succeeded);
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Settings admin" };
            Assert.True((await services.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(user, "CorrectPassword1")).Succeeded);
            userId = user.Id;
            Assert.True((await services.GetRequiredService<UserManager<ApplicationUser>>().AddToRoleAsync(user, roleName)).Succeeded);
        }

        using var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = loginBody.RootElement.GetProperty("accessToken").GetString()!;
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/settings")
        {
            Content = JsonContent.Create(new { company_name = "Gbrindes Norte", primary_color = "#A1B2C3" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var publicResponse = await _client.GetAsync("/api/v1/settings/public");
        using var publicBody = JsonDocument.Parse(await publicResponse.Content.ReadAsStringAsync());
        var branding = publicBody.RootElement.GetProperty("data");
        Assert.Equal("Gbrindes Norte", branding.GetProperty("company_name").GetString());
        Assert.Equal("#A1B2C3", branding.GetProperty("primary_color").GetString());
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var database = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var audit = await database.AuditLogs.SingleAsync(log => log.EntityType == "settings" && log.Action == "settings_update" && log.UserId == userId);
        using var before = JsonDocument.Parse(audit.BeforeJson!);
        using var after = JsonDocument.Parse(audit.AfterJson!);
        Assert.Equal("Controle de Brindes", before.RootElement.GetProperty("company_name").GetString());
        Assert.Equal("Gbrindes Norte", after.RootElement.GetProperty("company_name").GetString());
        Assert.Equal("#A1B2C3", after.RootElement.GetProperty("primary_color").GetString());

        using var noOp = new HttpRequestMessage(HttpMethod.Put, "/api/v1/settings")
        {
            Content = JsonContent.Create(new { company_name = "Gbrindes Norte", primary_color = "#A1B2C3" })
        };
        noOp.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var noOpResponse = await _client.SendAsync(noOp);
        Assert.Equal(HttpStatusCode.OK, noOpResponse.StatusCode);
        Assert.Equal(1, await database.AuditLogs.CountAsync(log => log.EntityType == "settings" && log.Action == "settings_update" && log.UserId == userId));

        using var unsupported = new HttpRequestMessage(HttpMethod.Put, "/api/v1/settings")
        {
            Content = JsonContent.Create(new Dictionary<string, string> { ["lecom_api_token"] = "test-placeholder" })
        };
        unsupported.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var unsupportedResponse = await _client.SendAsync(unsupported);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unsupportedResponse.StatusCode);
        Assert.Equal(1, await database.AuditLogs.CountAsync(log => log.EntityType == "settings" && log.Action == "settings_update" && log.UserId == userId));

        using var invalidColor = new HttpRequestMessage(HttpMethod.Put, "/api/v1/settings")
        {
            Content = JsonContent.Create(new { primary_color = "url(javascript:alert(1))" })
        };
        invalidColor.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var invalidColorResponse = await _client.SendAsync(invalidColor);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidColorResponse.StatusCode);
        Assert.Equal("#A1B2C3", await database.ApplicationSettings.AsNoTracking().Where(row => row.Key == "primary_color").Select(row => row.Value).SingleAsync());
        Assert.Equal(1, await database.AuditLogs.CountAsync(log => log.EntityType == "settings" && log.Action == "settings_update" && log.UserId == userId));
    }

    [Fact]
    public async Task Settings_management_denies_authenticated_users_without_the_permission()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"settings-reader-{Guid.NewGuid():N}";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            Assert.True((await roles.CreateAsync(new ApplicationRole(roleName))).Succeeded);
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Settings reader" };
            Assert.True((await services.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(user, "CorrectPassword1")).Succeeded);
            Assert.True((await services.GetRequiredService<UserManager<ApplicationUser>>().AddToRoleAsync(user, roleName)).Succeeded);
        }
        using var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = loginJson.RootElement.GetProperty("accessToken").GetString()!;
        using var get = new HttpRequestMessage(HttpMethod.Get, "/api/settings")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        };
        using var getResponse = await _client.SendAsync(get);
        Assert.Equal(HttpStatusCode.Forbidden, getResponse.StatusCode);
        using var update = new HttpRequestMessage(HttpMethod.Put, "/api/v1/settings")
        {
            Content = JsonContent.Create(new { company_name = "Alteração negada" }),
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        };
        using var updateResponse = await _client.SendAsync(update);
        Assert.Equal(HttpStatusCode.Forbidden, updateResponse.StatusCode);
        using var deniedLogoUpload = new MultipartFormDataContent();
        deniedLogoUpload.Add(new ByteArrayContent("not-an-image"u8.ToArray()), "logo", "logo.png");
        using var deniedLogoRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/settings/logo") { Content = deniedLogoUpload };
        deniedLogoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var deniedLogoResponse = await _client.SendAsync(deniedLogoRequest);
        Assert.Equal(HttpStatusCode.Forbidden, deniedLogoResponse.StatusCode);
        using var deniedLogoDelete = new HttpRequestMessage(HttpMethod.Delete, "/api/settings/logo");
        deniedLogoDelete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var deniedDeleteResponse = await _client.SendAsync(deniedLogoDelete);
        Assert.Equal(HttpStatusCode.Forbidden, deniedDeleteResponse.StatusCode);
    }

    [Fact]
    public async Task Admin_role_keeps_the_legacy_settings_management_bypass()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = await roles.FindByNameAsync("admin");
            if (role is null)
            {
                role = new ApplicationRole("admin");
                Assert.True((await roles.CreateAsync(role)).Succeeded);
            }
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Settings admin role" };
            Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, "admin")).Succeeded);
        }
        using var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/settings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", loginJson.RootElement.GetProperty("accessToken").GetString());
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Settings_logo_upload_is_publicly_served_replaceable_and_deletable()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"settings-logo-{Guid.NewGuid():N}";
        Guid? userId = null;
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = new ApplicationRole(roleName);
            Assert.True((await roles.CreateAsync(role)).Succeeded);
            Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "settings.manage"))).Succeeded);
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Logo manager" };
            Assert.True((await services.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(user, "CorrectPassword1")).Succeeded);
            userId = user.Id;
            Assert.True((await services.GetRequiredService<UserManager<ApplicationUser>>().AddToRoleAsync(user, roleName)).Succeeded);
        }

        var root = factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()
            .GetSection("Storage:Branding:RootPath").Value!;
        string? firstPath = null;
        string? replacementPath = null;
        try
        {
            using var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            var token = loginJson.RootElement.GetProperty("accessToken").GetString()!;

            using var unauthorizedMultipart = new MultipartFormDataContent();
            unauthorizedMultipart.Add(new ByteArrayContent(image), "logo", "logo.png");
            using var unauthorized = await _client.PostAsync("/api/v1/settings/logo", unauthorizedMultipart);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

            using var multipart = new MultipartFormDataContent();
            using var content = new ByteArrayContent(image);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            multipart.Add(content, "logo", "logo.png");
            using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, "/api/settings/logo") { Content = multipart };
            uploadRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var uploaded = await _client.SendAsync(uploadRequest);
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
            using var uploadedJson = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
            var firstUrl = uploadedJson.RootElement.GetProperty("data").GetProperty("logo_url").GetString();
            Assert.False(string.IsNullOrWhiteSpace(firstUrl));

            using var publicSettings = await _client.GetAsync("/api/v1/settings/public");
            using var publicJson = JsonDocument.Parse(await publicSettings.Content.ReadAsStringAsync());
            Assert.Equal(firstUrl, publicJson.RootElement.GetProperty("data").GetProperty("logo_url").GetString());
            var logoRequestUri = firstUrl!.StartsWith("/", StringComparison.Ordinal)
                ? new Uri(firstUrl, UriKind.Relative)
                : new Uri(firstUrl, UriKind.Absolute);
            using var publicLogo = await _client.GetAsync(logoRequestUri);
            Assert.True(publicLogo.StatusCode == HttpStatusCode.OK, $"GET {logoRequestUri} returned {publicLogo.StatusCode}");
            Assert.Equal("image/png", publicLogo.Content.Headers.ContentType?.MediaType);
            Assert.Equal(TimeSpan.FromHours(24), publicLogo.Headers.CacheControl?.MaxAge);
            var servedBytes = await publicLogo.Content.ReadAsByteArrayAsync();
            Assert.Equal([137, 80, 78, 71, 13, 10, 26, 10], servedBytes[..8]);

            await using (var verifyScope = factory.Services.CreateAsyncScope())
            {
                var database = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                firstPath = await database.ApplicationSettings.AsNoTracking().Where(row => row.Key == "logo_path").Select(row => row.Value).SingleAsync();
                Assert.True(File.Exists(Path.Combine(root, firstPath.Replace('/', Path.DirectorySeparatorChar))));
            }

            using var replacementMultipart = new MultipartFormDataContent();
            using var replacementContent = new ByteArrayContent(image);
            replacementContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            replacementMultipart.Add(replacementContent, "logo", "replacement.png");
            using var replacementRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/settings/logo") { Content = replacementMultipart };
            replacementRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var replaced = await _client.SendAsync(replacementRequest);
            Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
            using var replacedJson = JsonDocument.Parse(await replaced.Content.ReadAsStringAsync());
            var replacementUrl = replacedJson.RootElement.GetProperty("data").GetProperty("logo_url").GetString();
            Assert.NotEqual(firstUrl, replacementUrl);
            await using (var verifyScope = factory.Services.CreateAsyncScope())
            {
                var database = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                replacementPath = await database.ApplicationSettings.AsNoTracking().Where(row => row.Key == "logo_path").Select(row => row.Value).SingleAsync();
                Assert.True(File.Exists(Path.Combine(root, replacementPath.Replace('/', Path.DirectorySeparatorChar))));
                Assert.False(File.Exists(Path.Combine(root, firstPath!.Replace('/', Path.DirectorySeparatorChar))));
            }

            using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/settings/logo");
            delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var deleted = await _client.SendAsync(delete);
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            using var deletedJson = JsonDocument.Parse(await deleted.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Null, deletedJson.RootElement.GetProperty("data").GetProperty("logo_url").ValueKind);
            Assert.False(File.Exists(Path.Combine(root, replacementPath!.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/v1/settings/logo")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/settings/logo")).StatusCode);

            using var corruptMultipart = new MultipartFormDataContent();
            using var corruptContent = new ByteArrayContent("not-an-image"u8.ToArray());
            corruptContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            corruptMultipart.Add(corruptContent, "logo", "corrupt.png");
            using var corruptRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/settings/logo") { Content = corruptMultipart };
            corruptRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var corruptResponse = await _client.SendAsync(corruptRequest);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, corruptResponse.StatusCode);

            using var oversizedMultipart = new MultipartFormDataContent();
            using var oversizedContent = new ByteArrayContent(new byte[checked((int)BrandingLogoStorage.MaximumUploadBytes + 1)]);
            oversizedContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            oversizedMultipart.Add(oversizedContent, "logo", "oversized.png");
            using var oversizedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/settings/logo") { Content = oversizedMultipart };
            oversizedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var oversizedResponse = await _client.SendAsync(oversizedRequest);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, oversizedResponse.StatusCode);

            await using var auditScope = factory.Services.CreateAsyncScope();
            var auditDatabase = auditScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var logoAudits = await auditDatabase.AuditLogs.Where(log => log.EntityType == "settings" && log.UserId == userId && log.Action == "settings_update").ToListAsync();
            Assert.Equal(3, logoAudits.Count);
            Assert.All(logoAudits, audit => Assert.Contains("logo_path", audit.AfterJson));
        }
        finally
        {
            await using var cleanupScope = factory.Services.CreateAsyncScope();
            var database = cleanupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (userId is Guid createdUserId)
            {
                database.AuditLogs.RemoveRange(await database.AuditLogs.Where(log => log.EntityType == "settings" && log.UserId == createdUserId).ToListAsync());
                var users = cleanupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                if (await users.FindByIdAsync(createdUserId.ToString()) is { } user)
                    Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            database.ApplicationSettings.RemoveRange(await database.ApplicationSettings.Where(row => row.Key == "logo_path").ToListAsync());
            await database.SaveChangesAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            var roles = cleanupScope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
            if (await roles.FindByNameAsync(roleName) is { } role)
                Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }
}
