using System.Net;
using System.Text.Json;

namespace Gbrindes.Tests;

public sealed class SettingsAvailabilityTests(UnavailableHealthDatabaseFactory factory) : IClassFixture<UnavailableHealthDatabaseFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Public_branding_uses_safe_defaults_when_database_is_unavailable()
    {
        using var response = await _client.GetAsync("/api/v1/settings/public");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = body.RootElement.GetProperty("data");
        Assert.Equal("Controle de Brindes", data.GetProperty("company_name").GetString());
        Assert.Equal("#2563EB", data.GetProperty("primary_color").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("logo_url").ValueKind);
    }
}
