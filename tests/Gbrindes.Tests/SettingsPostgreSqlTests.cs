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

[Collection("PostgreSQL integration")]
public sealed class SettingsPostgreSqlTests(PostgreSqlWebApplicationFactory factory) : IClassFixture<PostgreSqlWebApplicationFactory>
{
    [PostgreSqlFact]
    public async Task Branding_logo_file_and_setting_are_persisted_with_audit_in_postgres()
    {
        using var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-logo-{Guid.NewGuid():N}";
        Guid? userId = null;
        ApplicationSetting? previousLogoSetting = null;
        var snapshotCaptured = false;
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                Assert.False(string.IsNullOrWhiteSpace(database.Database.GetConnectionString()));
                if (await database.ApplicationSettings.SingleOrDefaultAsync(row => row.Key == "logo_path") is { } logoSetting)
                {
                    previousLogoSetting = new ApplicationSetting
                    {
                        Key = logoSetting.Key,
                        Value = logoSetting.Value,
                        UpdatedBy = logoSetting.UpdatedBy,
                        UpdatedAt = logoSetting.UpdatedAt
                    };
                    logoSetting.Value = string.Empty;
                }
                else
                {
                    database.ApplicationSettings.Add(new ApplicationSetting { Key = "logo_path", Value = string.Empty });
                }
                await database.SaveChangesAsync();
                snapshotCaptured = true;

                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "settings.manage"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "PostgreSQL logo manager" };
                Assert.True((await services.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(user, "CorrectPassword1")).Succeeded);
                userId = user.Id;
                Assert.True((await services.GetRequiredService<UserManager<ApplicationUser>>().AddToRoleAsync(user, roleName)).Succeeded);
            }

            using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            using var multipart = new MultipartFormDataContent();
            using var content = new ByteArrayContent(png);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            multipart.Add(content, "logo", "logo.png");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/settings/logo") { Content = multipart };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", loginJson.RootElement.GetProperty("accessToken").GetString());
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var responseJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.NotNull(responseJson.RootElement.GetProperty("data").GetProperty("logo_url").GetString());

            await using (var verifyScope = factory.Services.CreateAsyncScope())
            {
                var database = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var storedPath = await database.ApplicationSettings.AsNoTracking()
                    .Where(row => row.Key == "logo_path").Select(row => row.Value).SingleAsync();
                Assert.StartsWith("branding/", storedPath);
                Assert.True(File.Exists(Path.Combine(factory.BrandingRoot, storedPath.Replace('/', Path.DirectorySeparatorChar))));
                var audit = await database.AuditLogs.AsNoTracking().SingleAsync(log =>
                    log.EntityType == "settings" && log.Action == "settings_update" && log.UserId == userId);
                using var after = JsonDocument.Parse(audit.AfterJson!);
                Assert.Equal(storedPath, after.RootElement.GetProperty("logo_path").GetString());
            }
        }
        finally
        {
            await using var cleanupScope = factory.Services.CreateAsyncScope();
            var services = cleanupScope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            if (snapshotCaptured)
            {
                if (userId is Guid auditUserId)
                    database.AuditLogs.RemoveRange(await database.AuditLogs.Where(log => log.EntityType == "settings" && log.UserId == auditUserId).ToListAsync());
                database.ApplicationSettings.RemoveRange(await database.ApplicationSettings.Where(row => row.Key == "logo_path").ToListAsync());
                if (previousLogoSetting is not null) database.ApplicationSettings.Add(previousLogoSetting);
                await database.SaveChangesAsync();
            }
            if (userId is Guid id)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                if (await users.FindByIdAsync(id.ToString()) is { } user)
                    Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            if (await roles.FindByNameAsync(roleName) is { } role)
                Assert.True((await roles.DeleteAsync(role)).Succeeded);
            if (Directory.Exists(factory.BrandingRoot)) Directory.Delete(factory.BrandingRoot, recursive: true);
        }
    }

    [PostgreSqlFact]
    public async Task Branding_settings_and_audit_persist_atomically_in_postgres()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-settings-{Guid.NewGuid():N}";
        Guid? userId = null;
        var previous = new Dictionary<string, ApplicationSetting>(StringComparer.Ordinal);
        var snapshotCaptured = false;
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                foreach (var setting in await database.ApplicationSettings.Where(row => row.Key == "company_name" || row.Key == "primary_color").ToListAsync())
                    previous[setting.Key] = new ApplicationSetting { Key = setting.Key, Value = setting.Value, UpdatedBy = setting.UpdatedBy, UpdatedAt = setting.UpdatedAt };
                snapshotCaptured = true;
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                Assert.True((await roles.AddClaimAsync(role, new Claim("permission", "settings.manage"))).Succeeded);
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "PostgreSQL settings admin" };
                Assert.True((await services.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(user, "CorrectPassword1")).Succeeded);
                userId = user.Id;
                Assert.True((await services.GetRequiredService<UserManager<ApplicationUser>>().AddToRoleAsync(user, roleName)).Succeeded);
            }

            using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            var token = loginJson.RootElement.GetProperty("accessToken").GetString()!;
            using var request = new HttpRequestMessage(HttpMethod.Put, "/api/settings")
            {
                Content = JsonContent.Create(new { company_name = "Postgres Gbrindes", primary_color = "#ABC123" })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var publicResponse = await client.GetAsync("/api/settings/public");
            Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
            using var publicJson = JsonDocument.Parse(await publicResponse.Content.ReadAsStringAsync());
            var branding = publicJson.RootElement.GetProperty("data");
            Assert.Equal("Postgres Gbrindes", branding.GetProperty("company_name").GetString());
            Assert.Equal("#ABC123", branding.GetProperty("primary_color").GetString());

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal("Postgres Gbrindes", await verifyDatabase.ApplicationSettings.AsNoTracking().Where(row => row.Key == "company_name").Select(row => row.Value).SingleAsync());
            var audit = await verifyDatabase.AuditLogs.AsNoTracking().SingleAsync(log => log.EntityType == "settings" && log.Action == "settings_update" && log.UserId == userId);
            using var before = JsonDocument.Parse(audit.BeforeJson!);
            using var after = JsonDocument.Parse(audit.AfterJson!);
            Assert.Equal("Controle de Brindes", before.RootElement.GetProperty("company_name").GetString());
            Assert.Equal("Postgres Gbrindes", after.RootElement.GetProperty("company_name").GetString());
            Assert.Equal("#ABC123", after.RootElement.GetProperty("primary_color").GetString());
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            if (snapshotCaptured)
            {
                if (userId is Guid auditUserId)
                    database.AuditLogs.RemoveRange(await database.AuditLogs.Where(log => log.EntityType == "settings" && log.UserId == auditUserId).ToListAsync());
                database.ApplicationSettings.RemoveRange(await database.ApplicationSettings.Where(row => row.Key == "company_name" || row.Key == "primary_color").ToListAsync());
                database.ApplicationSettings.AddRange(previous.Values);
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
}
