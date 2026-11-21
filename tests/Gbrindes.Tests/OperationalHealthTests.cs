using System.Net;
using System.Text.Json;
using Gbrindes.Web.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gbrindes.Tests;

public sealed class OperationalHealthTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Readiness_reports_available_database_without_authentication()
    {
        using var response = await _client.GetAsync("/api/v1/readiness");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ready", body.RootElement.GetProperty("status").GetString());
    }
}

public sealed class OperationalHealthUnavailableTests : IClassFixture<UnavailableHealthDatabaseFactory>
{
    private readonly HttpClient _client;

    public OperationalHealthUnavailableTests(UnavailableHealthDatabaseFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Readiness_returns_unavailable_without_exposing_database_details()
    {
        using var response = await _client.GetAsync("/api/v1/readiness");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("unavailable", content, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", content, StringComparison.Ordinal);
        Assert.DoesNotContain("password", content, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class UnavailableHealthDatabaseFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();
            services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(
                "Host=127.0.0.1;Port=1;Database=unavailable;Username=postgres;Timeout=1"));
        });
    }
}

[Collection("PostgreSQL integration")]
public sealed class OperationalHealthPostgreSqlTests(PostgreSqlWebApplicationFactory factory) : IClassFixture<PostgreSqlWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [PostgreSqlFact]
    public async Task Readiness_reaches_the_configured_postgres_database()
    {
        using var response = await _client.GetAsync("/api/v1/readiness");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ready", body.RootElement.GetProperty("status").GetString());
    }
}
