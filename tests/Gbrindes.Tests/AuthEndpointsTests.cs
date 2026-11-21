using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Gbrindes.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Gbrindes.Tests;

public sealed class AuthEndpointsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    public AuthEndpointsTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Current_user_requires_authentication() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/auth/me")).StatusCode);

    [Fact]
    public async Task Change_password_requires_authentication() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsync("/api/v1/auth/change-password", null)).StatusCode);

    [Fact]
    public async Task Login_rejects_invalid_credentials_without_disclosing_account_state()
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email = "absent@example.invalid", password = "WrongPassword1" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_returns_revocable_bearer_token_for_current_user()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, "CorrectPassword1");

        using var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = loginJson.RootElement.GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.Equal("Bearer", loginJson.RootElement.GetProperty("tokenType").GetString());

        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var me = await _client.SendAsync(meRequest);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logoutRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var logout = await _client.SendAsync(logoutRequest);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        using var revokedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        revokedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var revoked = await _client.SendAsync(revokedRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task Password_change_clears_required_flag_and_keeps_current_token_valid()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, "TemporaryPass1", mustChangePassword: true);
        var token = await LoginAndGetTokenAsync(email, "TemporaryPass1");

        using var changeRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/change-password")
        {
            Content = JsonContent.Create(new
            {
                currentPassword = "TemporaryPass1",
                newPassword = "NewSecurePass123",
                newPasswordConfirmation = "NewSecurePass123"
            })
        };
        changeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var change = await _client.SendAsync(changeRequest);
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);

        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var me = await _client.SendAsync(meRequest);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var meJson = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.False(meJson.RootElement.GetProperty("must_change_password").GetBoolean());
    }

    [Fact]
    public async Task Five_failed_password_attempts_lock_account_for_following_attempts()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, "CorrectPassword1");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "WrongPassword1" });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var locked = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }

    [Fact]
    public async Task Forgot_password_returns_same_generic_response_for_existing_and_unknown_email()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, "CorrectPassword1");

        using var existing = await _client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email });
        using var unknown = await _client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = "unknown@example.invalid" });
        Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        using var existingJson = JsonDocument.Parse(await existing.Content.ReadAsStringAsync());
        using var unknownJson = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
        Assert.Equal(existingJson.RootElement.GetProperty("message").GetString(), unknownJson.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Reset_password_changes_credentials_revokes_sessions_and_consumes_token_once()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, "CorrectPassword1");
        using var forgot = await _client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email });
        using var forgotJson = JsonDocument.Parse(await forgot.Content.ReadAsStringAsync());
        var token = forgotJson.RootElement.GetProperty("test_token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        using var reset = await _client.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, password = "NewSecurePass123", passwordConfirmation = "NewSecurePass123" });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        using var reused = await _client.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, password = "AnotherSecure123", passwordConfirmation = "AnotherSecure123" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        using var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "NewSecurePass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Reset_password_rejects_invalid_token_and_reset_page_is_published()
    {
        using var reset = await _client.PostAsJsonAsync("/api/v1/auth/reset-password", new
        {
            token = "invalid",
            password = "NewSecurePass123",
            passwordConfirmation = "NewSecurePass123"
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reset.StatusCode);
        using var page = await _client.GetAsync("/esqueci-senha");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("forgot-password-form", await page.Content.ReadAsStringAsync());
    }

    private async Task CreateUserAsync(string email, string password, bool mustChangePassword = false)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = "Usuário de teste",
            MustChangePassword = mustChangePassword
        };
        var result = await userManager.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));
    }

    private async Task<string> LoginAndGetTokenAsync(string email, string password)
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }
}

[Collection("PostgreSQL integration")]
public sealed class PostgreSqlAuthEndpointTests(PostgreSqlWebApplicationFactory factory)
    : IClassFixture<PostgreSqlWebApplicationFactory>
{
    [PostgreSqlFact]
    public async Task Password_change_revokes_other_postgres_sessions_but_keeps_current_session()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        Guid userId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = "Usuário PostgreSQL"
            };
            var created = await users.CreateAsync(user, "TemporaryPass1");
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));
            userId = user.Id;
        }

        try
        {
            using var client = factory.CreateClient();
            var currentToken = await LoginAsync(client, email, "TemporaryPass1");
            var otherToken = await LoginAsync(client, email, "TemporaryPass1");

            using var changeRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/change-password")
            {
                Content = JsonContent.Create(new
                {
                    currentPassword = "TemporaryPass1",
                    newPassword = "NewSecurePass123",
                    newPasswordConfirmation = "NewSecurePass123"
                })
            };
            changeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", currentToken);
            using var changed = await client.SendAsync(changeRequest);
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

            using var currentRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
            currentRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", currentToken);
            using var current = await client.SendAsync(currentRequest);
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);

            using var otherRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
            otherRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", otherToken);
            using var other = await client.SendAsync(otherRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);

            using var newLogin = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "NewSecurePass123" });
            Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<Gbrindes.Web.Data.ApplicationDbContext>();
            var user = await database.Users.SingleOrDefaultAsync(candidate => candidate.Id == userId);
            if (user is not null)
            {
                database.AuthSessions.RemoveRange(database.AuthSessions.Where(session => session.UserId == userId));
                database.Users.Remove(user);
                await database.SaveChangesAsync();
            }
        }
    }

    [PostgreSqlFact]
    public async Task Deactivating_postgres_user_invalidates_existing_token_and_blocks_login()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        Guid userId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = "Conta a desativar"
            };
            var created = await users.CreateAsync(user, "CorrectPassword1");
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));
            userId = user.Id;
        }

        try
        {
            using var client = factory.CreateClient();
            var token = await LoginAsync(client, email, "CorrectPassword1");
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<Gbrindes.Web.Data.ApplicationDbContext>();
                var user = await database.Users.SingleAsync(candidate => candidate.Id == userId);
                user.Active = false;
                await database.SaveChangesAsync();
            }

            using var currentRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
            currentRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var current = await client.SendAsync(currentRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, current.StatusCode);

            using var newLogin = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
            Assert.Equal(HttpStatusCode.Unauthorized, newLogin.StatusCode);
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<Gbrindes.Web.Data.ApplicationDbContext>();
            var user = await database.Users.SingleOrDefaultAsync(candidate => candidate.Id == userId);
            if (user is not null)
            {
                database.AuthSessions.RemoveRange(database.AuthSessions.Where(session => session.UserId == userId));
                database.Users.Remove(user);
                await database.SaveChangesAsync();
            }
        }
    }

    [PostgreSqlFact]
    public async Task Logout_revokes_the_postgres_session_and_persists_revocation()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        Guid userId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = "Logout PostgreSQL"
            };
            var created = await users.CreateAsync(user, "CorrectPassword1");
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));
            userId = user.Id;
        }

        try
        {
            using var client = factory.CreateClient();
            var token = await LoginAsync(client, email, "CorrectPassword1");
            using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
            logoutRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var logout = await client.SendAsync(logoutRequest);
            Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<Gbrindes.Web.Data.ApplicationDbContext>();
                var session = await database.AuthSessions.SingleAsync(value => value.UserId == userId);
                Assert.NotNull(session.RevokedAt);
            }

            using var currentRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
            currentRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var current = await client.SendAsync(currentRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, current.StatusCode);
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<Gbrindes.Web.Data.ApplicationDbContext>();
            var user = await database.Users.SingleOrDefaultAsync(candidate => candidate.Id == userId);
            if (user is not null)
            {
                database.AuthSessions.RemoveRange(database.AuthSessions.Where(session => session.UserId == userId));
                database.Users.Remove(user);
                await database.SaveChangesAsync();
            }
        }
    }

    [PostgreSqlFact]
    public async Task Failed_postgres_logins_lock_account_after_threshold()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        Guid userId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = "Conta para lockout"
            };
            var created = await users.CreateAsync(user, "CorrectPassword1");
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));
            userId = user.Id;
        }

        try
        {
            using var client = factory.CreateClient();
            for (var attempt = 0; attempt < 5; attempt++)
            {
                using var failed = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "WrongPassword1" });
                Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
            }

            using var locked = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
            Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(userId.ToString());
                Assert.NotNull(user);
                user.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(-1);
                await users.UpdateAsync(user);
            }

            using var unlocked = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
            Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<Gbrindes.Web.Data.ApplicationDbContext>();
            var user = await database.Users.SingleOrDefaultAsync(candidate => candidate.Id == userId);
            if (user is not null)
            {
                database.AuthSessions.RemoveRange(database.AuthSessions.Where(session => session.UserId == userId));
                database.Users.Remove(user);
                await database.SaveChangesAsync();
            }
        }
    }

    [PostgreSqlFact]
    public async Task Expired_postgres_session_is_rejected()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        Guid userId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = "Sessão expirada"
            };
            var created = await users.CreateAsync(user, "CorrectPassword1");
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));
            userId = user.Id;
        }

        try
        {
            using var client = factory.CreateClient();
            var token = await LoginAsync(client, email, "CorrectPassword1");
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<Gbrindes.Web.Data.ApplicationDbContext>();
                var session = await database.AuthSessions.SingleAsync(value => value.UserId == userId);
                session.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
                await database.SaveChangesAsync();
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<Gbrindes.Web.Data.ApplicationDbContext>();
            var user = await database.Users.SingleOrDefaultAsync(candidate => candidate.Id == userId);
            if (user is not null)
            {
                database.AuthSessions.RemoveRange(database.AuthSessions.Where(session => session.UserId == userId));
                database.Users.Remove(user);
                await database.SaveChangesAsync();
            }
        }
    }

    private static async Task<string> LoginAsync(HttpClient client, string email, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }
}
