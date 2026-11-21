using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Gbrindes.Web.Data;
using Gbrindes.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gbrindes.Tests;

public sealed class DashboardMigrationTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Import_template_requires_import_permission_and_returns_xlsx()
    {
        var token = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", ["import.run"]);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/import/template/items");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("PK", Encoding.ASCII.GetString(bytes, 0, 2));
        Assert.Equal("modelo-importacao-brindes.xlsx", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        using var workbook = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var reader = new StreamReader(workbook.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheet = await reader.ReadToEndAsync();
        Assert.Contains("codigo", sheet, StringComparison.Ordinal);
        Assert.Contains("quantidade_inicial", sheet, StringComparison.Ordinal);

        using var unauthenticated = await _client.GetAsync("/api/v1/import/template/items");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using var unknownRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/import/template/unknown");
        unknownRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var unknown = await _client.SendAsync(unknownRequest);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Stock_alerts_require_dashboard_permission_and_report_critical_active_items()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var token = await CreateUserAndLoginAsync(email, ["dashboard.view"]);
        var otherToken = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", []);
        var categoryId = Guid.NewGuid();
        var zeroItemId = Guid.NewGuid();
        var lowItemId = Guid.NewGuid();
        var inactiveItemId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Categories.Add(new Category { Id = categoryId, Name = $"Dash-{Guid.NewGuid():N}", NameKey = $"DASH-{Guid.NewGuid():N}" });
            db.CatalogItems.AddRange(
                new CatalogItem { Id = zeroItemId, Code = $"Z-{Guid.NewGuid():N}"[..12], Name = "Zero", CategoryId = categoryId, MinStock = 1 },
                new CatalogItem { Id = lowItemId, Code = $"L-{Guid.NewGuid():N}"[..12], Name = "Low", CategoryId = categoryId, MinStock = 5 },
                new CatalogItem { Id = inactiveItemId, Code = $"I-{Guid.NewGuid():N}"[..12], Name = "Inactive", CategoryId = categoryId, MinStock = 5, Status = "inativo" });
            db.Stocks.AddRange(new InventoryStock { ItemId = zeroItemId, OnHand = 2, Reserved = 2 }, new InventoryStock { ItemId = lowItemId, OnHand = 5, Reserved = 2 }, new InventoryStock { ItemId = inactiveItemId, OnHand = 0 });
            await db.SaveChangesAsync();
        }

        using var deniedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/dashboard/stock-alerts");
        deniedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", otherToken);
        using var denied = await _client.SendAsync(deniedRequest);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var response = await SendAsync(token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var alerts = json.RootElement.EnumerateArray().ToArray();
        var testAlerts = alerts.Where(alert => new[] { zeroItemId, lowItemId }.Contains(alert.GetProperty("item").GetProperty("id").GetGuid())).ToArray();
        Assert.Equal(2, testAlerts.Length);
        Assert.Equal("zero", testAlerts[0].GetProperty("level").GetString());
        Assert.Equal("low", testAlerts[1].GetProperty("level").GetString());
        Assert.Equal(0, testAlerts[0].GetProperty("available").GetInt32());
        Assert.Equal(3, testAlerts[1].GetProperty("available").GetInt32());
        Assert.DoesNotContain(alerts, alert => alert.GetProperty("item").GetProperty("id").GetGuid() == inactiveItemId);
    }

    [Fact]
    public async Task Stock_summary_reports_active_stock_totals_and_value()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var token = await CreateUserAndLoginAsync(email, ["dashboard.view", "stock.view"]);
        var categoryId = Guid.NewGuid();
        var expectedCount = 0;
        var expectedOnHand = 0;
        var expectedReserved = 0;
        var expectedValue = 0m;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var baseline = await db.Stocks.Where(stock => stock.Item.DeletedAt == null && stock.Item.Status == "ativo")
                .Select(stock => new { stock.OnHand, stock.Reserved, Value = stock.OnHand * (stock.Item.UnitValue ?? 0m) }).ToListAsync();
            db.Categories.Add(new Category { Id = categoryId, Name = $"Summary-{Guid.NewGuid():N}", NameKey = $"SUM-{Guid.NewGuid():N}" });
            var activeItemId = Guid.NewGuid();
            var inactiveItemId = Guid.NewGuid();
            db.CatalogItems.AddRange(
                new CatalogItem { Id = activeItemId, Code = $"S-{Guid.NewGuid():N}"[..12], Name = "Summary active", CategoryId = categoryId, UnitValue = 2.50m, Status = "ativo" },
                new CatalogItem { Id = inactiveItemId, Code = $"S-{Guid.NewGuid():N}"[..12], Name = "Summary inactive", CategoryId = categoryId, UnitValue = 99m, Status = "inativo" });
            db.Stocks.AddRange(new InventoryStock { ItemId = activeItemId, OnHand = 10, Reserved = 3 }, new InventoryStock { ItemId = inactiveItemId, OnHand = 20 });
            await db.SaveChangesAsync();
            expectedCount = baseline.Count + 1;
            expectedOnHand = baseline.Sum(stock => stock.OnHand) + 10;
            expectedReserved = baseline.Sum(stock => stock.Reserved) + 3;
            expectedValue = baseline.Sum(stock => stock.Value) + 25m;
        }
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/dashboard/stock-summary");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCount, json.RootElement.GetProperty("items_active").GetInt32());
        Assert.Equal(expectedOnHand, json.RootElement.GetProperty("units_on_hand").GetInt32());
        Assert.Equal(expectedReserved, json.RootElement.GetProperty("units_reserved").GetInt32());
        Assert.Equal(expectedOnHand - expectedReserved, json.RootElement.GetProperty("units_available").GetInt32());
        Assert.Equal(expectedValue, json.RootElement.GetProperty("stock_value").GetDecimal());
    }

    [Fact]
    public async Task Request_summary_scopes_industry_and_excludes_trade_and_drafts()
    {
        var industryId = Guid.NewGuid();
        var otherIndustryId = Guid.NewGuid();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Industries.AddRange(
                new Industry { Id = industryId, Name = $"Dash-{Guid.NewGuid():N}", NameKey = $"DA-{Guid.NewGuid():N}" },
                new Industry { Id = otherIndustryId, Name = $"Dash-{Guid.NewGuid():N}", NameKey = $"DB-{Guid.NewGuid():N}" });
            await db.SaveChangesAsync();
        }
        var token = await CreateUserAndLoginAsync(email, ["dashboard.view", "requests.view_all"], industryId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var requesterId = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!.Id;
            db.Requests.AddRange(
                new InternalRequest { Code = $"Q-{Guid.NewGuid():N}"[..12], RequesterId = requesterId, IndustryId = industryId, Status = "aguardando_aprovacao", Flow = "interna", Purpose = "Approval" },
                new InternalRequest { Code = $"Q-{Guid.NewGuid():N}"[..12], RequesterId = requesterId, IndustryId = industryId, Status = "pronta", Flow = "interna", Purpose = "Ready" },
                new InternalRequest { Code = $"Q-{Guid.NewGuid():N}"[..12], RequesterId = requesterId, IndustryId = industryId, Status = "rascunho", Flow = "interna", Purpose = "Draft" },
                new InternalRequest { Code = $"Q-{Guid.NewGuid():N}"[..12], RequesterId = requesterId, IndustryId = industryId, Status = "aguardando_aprovacao", Flow = "trade", Purpose = "Trade" },
                new InternalRequest { Code = $"Q-{Guid.NewGuid():N}"[..12], RequesterId = requesterId, IndustryId = industryId, Status = "finalizada", StatusChangedAt = new DateTimeOffset(2025, 1, 31, 23, 59, 0, TimeSpan.Zero), Flow = "interna", Purpose = "Finalized in period" },
                new InternalRequest { Code = $"Q-{Guid.NewGuid():N}"[..12], RequesterId = requesterId, IndustryId = industryId, Status = "finalizada", StatusChangedAt = new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero), Flow = "interna", Purpose = "Finalized outside period" },
                new InternalRequest { Code = $"Q-{Guid.NewGuid():N}"[..12], RequesterId = requesterId, IndustryId = otherIndustryId, Status = "pronta", Flow = "interna", Purpose = "Other industry" });
            await db.SaveChangesAsync();
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/dashboard/request-summary?from=2025-01-01&to=2025-01-31");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, json.RootElement.GetProperty("pending").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("awaiting_approval").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("ready").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("finalized_period").GetInt32());

        using var maximumDateRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/dashboard/request-summary?from=9999-12-31&to=9999-12-31");
        maximumDateRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var maximumDateResponse = await _client.SendAsync(maximumDateRequest);
        Assert.Equal(HttpStatusCode.OK, maximumDateResponse.StatusCode);
    }

    [Fact]
    public async Task Report_type_catalog_requires_reports_view_and_matches_legacy_types()
    {
        var permittedEmail = $"{Guid.NewGuid():N}@example.invalid";
        var permittedToken = await CreateUserAndLoginAsync(permittedEmail, ["reports.view"]);
        var deniedToken = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", []);
        using var deniedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports");
        deniedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deniedToken);
        using var denied = await _client.SendAsync(deniedRequest);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", permittedToken);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        var reports = json.RootElement.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(16, reports.Length);
        Assert.Equal("stock", reports[0].GetProperty("id").GetString());
        Assert.Equal("Estoque atual", reports[0].GetProperty("label").GetString());
        Assert.Equal("event_report", reports[^1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Every_xlsx_export_requires_both_view_and_export_permissions()
    {
        var token = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", ["reports.view"]);
        var exportOnlyToken = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", ["reports.export"]);
        var paths = new[]
        {
            "stock/export.xlsx", "low_stock/export.xlsx", "entries/export.xlsx", "exits/export.xlsx",
            "movements/export.xlsx", "approvals/export.xlsx", "protocols/export.xlsx",
            "event_balance/export.xlsx?event_id=00000000-0000-0000-0000-000000000001", "stock_industry/export.xlsx",
            "withdrawals/export.xlsx", "event_report/export.xlsx?event_id=00000000-0000-0000-0000-000000000001",
            "requests/export.xlsx", "by_item/export.xlsx", "by_department/export.xlsx", "by_industry/export.xlsx",
            "by_requester/export.xlsx"
        };

        foreach (var path in paths)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reports/{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            using var exportOnlyRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reports/{path}");
            exportOnlyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", exportOnlyToken);
            using var exportOnlyResponse = await _client.SendAsync(exportOnlyRequest);
            Assert.Equal(HttpStatusCode.Forbidden, exportOnlyResponse.StatusCode);
        }
    }

    [Fact]
    public async Task Stock_report_returns_legacy_columns_and_stock_rows()
    {
        var token = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", ["reports.view"]);
        var categoryId = Guid.NewGuid();
        var industryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var code = $"RPT-{Guid.NewGuid():N}"[..16];
        var categoryName = $"Report-{Guid.NewGuid():N}";
        var industryName = $"Industry-{Guid.NewGuid():N}";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Categories.Add(new Category { Id = categoryId, Name = categoryName, NameKey = categoryName.ToUpperInvariant() });
            db.Industries.Add(new Industry { Id = industryId, Name = industryName, NameKey = industryName.ToUpperInvariant() });
            db.CatalogItems.Add(new CatalogItem { Id = itemId, Code = code, Name = "Report item", CategoryId = categoryId, UnitValue = 1.75m, MinStock = 4, Status = "ativo" });
            db.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 8, Reserved = 3 });
            db.StockMovements.Add(new StockMovement { ItemId = itemId, IndustryId = industryId, Type = "entrada", Quantity = 8, BalanceAfter = 8 });
            await db.SaveChangesAsync();
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/stock");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("Estoque atual", data.GetProperty("title").GetString());
        Assert.Equal(new[] { "Código", "Nome", "Categoria", "Local", "Indústria", "Em estoque", "Reservado", "Disponível", "Mínimo", "Valor unit.", "Status" }, data.GetProperty("columns").EnumerateArray().Select(value => value.GetString()).ToArray());
        var row = data.GetProperty("rows").EnumerateArray().Single(value => value[0].GetString() == code);
        Assert.Equal("Report item", row[1].GetString());
        Assert.Equal(categoryName, row[2].GetString());
        Assert.Equal(industryName, row[4].GetString());
        Assert.Equal(8, row[5].GetInt32());
        Assert.Equal(3, row[6].GetInt32());
        Assert.Equal(5, row[7].GetInt32());
        Assert.Equal(4, row[8].GetInt32());
        Assert.Equal(1.75m, row[9].GetDecimal());
        Assert.Equal("ativo", row[10].GetString());
    }

    [Fact]
    public async Task Low_stock_report_includes_only_active_critical_items_in_available_order()
    {
        var token = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", ["reports.view"]);
        var categoryId = Guid.NewGuid();
        var industryId = Guid.NewGuid();
        var zeroId = Guid.NewGuid();
        var lowId = Guid.NewGuid();
        var inactiveId = Guid.NewGuid();
        var marker = Guid.NewGuid().ToString("N")[..6];
        var industryName = $"Low-{Guid.NewGuid():N}";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Categories.Add(new Category { Id = categoryId, Name = $"LowCategory-{Guid.NewGuid():N}", NameKey = $"LC-{Guid.NewGuid():N}" });
            db.Industries.Add(new Industry { Id = industryId, Name = industryName, NameKey = industryName.ToUpperInvariant() });
            db.CatalogItems.AddRange(
                new CatalogItem { Id = zeroId, Code = $"Z{marker}", Name = "Critical zero", CategoryId = categoryId, Status = "ativo", MinStock = 2 },
                new CatalogItem { Id = lowId, Code = $"L{marker}", Name = "Critical low", CategoryId = categoryId, Status = "ativo", MinStock = 5 },
                new CatalogItem { Id = inactiveId, Code = $"I{marker}", Name = "Inactive critical", CategoryId = categoryId, Status = "inativo", MinStock = 5 });
            db.Stocks.AddRange(new InventoryStock { ItemId = zeroId, OnHand = 3, Reserved = 3 }, new InventoryStock { ItemId = lowId, OnHand = 7, Reserved = 4 }, new InventoryStock { ItemId = inactiveId, OnHand = 0 });
            db.StockMovements.AddRange(new StockMovement { ItemId = zeroId, IndustryId = industryId, Type = "entrada", Quantity = 3 }, new StockMovement { ItemId = lowId, IndustryId = industryId, Type = "entrada", Quantity = 7 });
            await db.SaveChangesAsync();
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/low_stock");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("Abaixo do mínimo", data.GetProperty("title").GetString());
        Assert.Equal(new[] { "Código", "Nome", "Indústria", "Disponível", "Mínimo" }, data.GetProperty("columns").EnumerateArray().Select(value => value.GetString()).ToArray());
        var rows = data.GetProperty("rows").EnumerateArray().Where(row => row[0].GetString()!.EndsWith(marker, StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal($"Z{marker}", rows[0][0].GetString());
        Assert.Equal(industryName, rows[0][2].GetString());
        Assert.Equal(0, rows[0][3].GetInt32());
        Assert.Equal($"L{marker}", rows[1][0].GetString());
        Assert.Equal(industryName, rows[1][2].GetString());
        Assert.Equal(3, rows[1][3].GetInt32());
    }

    [Fact]
    public async Task Entry_report_applies_inclusive_dates_and_item_filter_with_legacy_shape()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var token = await CreateUserAndLoginAsync(email, ["reports.view"]);
        var categoryId = Guid.NewGuid();
        var industryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var otherItemId = Guid.NewGuid();
        var itemCode = $"ENT-{Guid.NewGuid():N}"[..16];
        var industryName = $"EntryIndustry-{Guid.NewGuid():N}";
        Guid userId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userId = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!.Id;
            db.Categories.Add(new Category { Id = categoryId, Name = $"EntryCategory-{Guid.NewGuid():N}", NameKey = $"EC-{Guid.NewGuid():N}" });
            db.Industries.Add(new Industry { Id = industryId, Name = industryName, NameKey = industryName.ToUpperInvariant() });
            db.CatalogItems.AddRange(
                new CatalogItem { Id = itemId, Code = itemCode, Name = "Entry test item", CategoryId = categoryId },
                new CatalogItem { Id = otherItemId, Code = $"OTH-{Guid.NewGuid():N}"[..16], Name = "Other entry item", CategoryId = categoryId });
            db.StockMovements.AddRange(
                new StockMovement { ItemId = itemId, UserId = userId, IndustryId = industryId, Type = "entrada", Quantity = 12, BalanceAfter = 20, Recipient = "Almoxarifado", Purpose = "Reposição", PurchaseTicketNo = "CH-42", CreatedAt = new DateTimeOffset(2025, 1, 31, 23, 59, 0, TimeSpan.Zero) },
                new StockMovement { ItemId = itemId, UserId = userId, Type = "saida", Quantity = -2, BalanceAfter = 18, CreatedAt = new DateTimeOffset(2025, 1, 20, 9, 0, 0, TimeSpan.Zero) },
                new StockMovement { ItemId = itemId, UserId = userId, Type = "entrada", Quantity = 4, BalanceAfter = 22, CreatedAt = new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero) },
                new StockMovement { ItemId = otherItemId, UserId = userId, Type = "entrada", Quantity = 6, BalanceAfter = 6, CreatedAt = new DateTimeOffset(2025, 1, 15, 9, 0, 0, TimeSpan.Zero) });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reports/entries?from=2025-01-01&to=2025-01-31&item_id={itemId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("Histórico de entradas", data.GetProperty("title").GetString());
        Assert.Equal(new[] { "Data", "Tipo", "Código", "Brinde", "Indústria", "Qtd", "Saldo após", "Usuário", "Destinatário", "Finalidade", "Chamado" }, data.GetProperty("columns").EnumerateArray().Select(value => value.GetString()).ToArray());
        var rows = data.GetProperty("rows").EnumerateArray().ToArray();
        var row = Assert.Single(rows);
        Assert.Contains("2025-01-31T23:59", row[0].GetString());
        Assert.Equal("entrada", row[1].GetString());
        Assert.Equal(itemCode, row[2].GetString());
        Assert.Equal(industryName, row[4].GetString());
        Assert.Equal(12, row[5].GetInt32());
        Assert.Equal(20, row[6].GetInt32());
        Assert.Equal("Dashboard test", row[7].GetString());
        Assert.Equal("Almoxarifado", row[8].GetString());
        Assert.Equal("Reposição", row[9].GetString());
        Assert.Equal("CH-42", row[10].GetString());
    }

    [Fact]
    public async Task Exit_report_returns_only_filtered_exits_with_legacy_title_and_quantity()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var token = await CreateUserAndLoginAsync(email, ["reports.view"]);
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var otherItemId = Guid.NewGuid();
        var itemCode = $"EXT-{Guid.NewGuid():N}"[..16];
        Guid userId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userId = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!.Id;
            db.Categories.Add(new Category { Id = categoryId, Name = $"ExitCategory-{Guid.NewGuid():N}", NameKey = $"XC-{Guid.NewGuid():N}" });
            db.CatalogItems.AddRange(
                new CatalogItem { Id = itemId, Code = itemCode, Name = "Exit test item", CategoryId = categoryId },
                new CatalogItem { Id = otherItemId, Code = $"OTH-{Guid.NewGuid():N}"[..16], Name = "Other exit item", CategoryId = categoryId });
            db.StockMovements.AddRange(
                new StockMovement { ItemId = itemId, UserId = userId, Type = "saida", Quantity = -5, BalanceAfter = 15, CreatedAt = new DateTimeOffset(2025, 1, 31, 23, 59, 0, TimeSpan.Zero) },
                new StockMovement { ItemId = itemId, UserId = userId, Type = "entrada", Quantity = 2, BalanceAfter = 17, CreatedAt = new DateTimeOffset(2025, 1, 20, 9, 0, 0, TimeSpan.Zero) },
                new StockMovement { ItemId = itemId, UserId = userId, Type = "saida", Quantity = -3, BalanceAfter = 12, CreatedAt = new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero) },
                new StockMovement { ItemId = otherItemId, UserId = userId, Type = "saida", Quantity = -1, BalanceAfter = 9, CreatedAt = new DateTimeOffset(2025, 1, 15, 9, 0, 0, TimeSpan.Zero) });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reports/exits?from=2025-01-01&to=2025-01-31&item_id={itemId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("Histórico de saídas", data.GetProperty("title").GetString());
        Assert.Equal(new[] { "Data", "Tipo", "Código", "Brinde", "Indústria", "Qtd", "Saldo após", "Usuário", "Destinatário", "Finalidade", "Chamado" }, data.GetProperty("columns").EnumerateArray().Select(value => value.GetString()).ToArray());
        var row = Assert.Single(data.GetProperty("rows").EnumerateArray());
        Assert.Equal("saida", row[1].GetString());
        Assert.Equal(itemCode, row[2].GetString());
        Assert.Equal(-5, row[5].GetInt32());
        Assert.Equal(15, row[6].GetInt32());
    }

    [Fact]
    public async Task Movements_report_includes_all_types_in_date_descending_order()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var token = await CreateUserAndLoginAsync(email, ["reports.view"]);
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var itemCode = $"MOV-{Guid.NewGuid():N}"[..16];
        Guid userId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userId = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!.Id;
            db.Categories.Add(new Category { Id = categoryId, Name = $"MovementCategory-{Guid.NewGuid():N}", NameKey = $"MC-{Guid.NewGuid():N}" });
            db.CatalogItems.Add(new CatalogItem { Id = itemId, Code = itemCode, Name = "Movement test item", CategoryId = categoryId });
            db.StockMovements.AddRange(
                new StockMovement { ItemId = itemId, UserId = userId, Type = "entrada", Quantity = 10, BalanceAfter = 10, CreatedAt = new DateTimeOffset(2025, 1, 5, 9, 0, 0, TimeSpan.Zero) },
                new StockMovement { ItemId = itemId, UserId = userId, Type = "saida", Quantity = -2, BalanceAfter = 8, CreatedAt = new DateTimeOffset(2025, 1, 31, 23, 59, 0, TimeSpan.Zero) },
                new StockMovement { ItemId = itemId, UserId = userId, Type = "entrada", Quantity = 7, BalanceAfter = 15, CreatedAt = new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero) });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/movements?from=2025-01-01&to=2025-01-31");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("Movimentação por período", data.GetProperty("title").GetString());
        Assert.Equal(new[] { "Data", "Tipo", "Código", "Brinde", "Indústria", "Qtd", "Saldo após", "Usuário", "Destinatário", "Finalidade", "Chamado" }, data.GetProperty("columns").EnumerateArray().Select(value => value.GetString()).ToArray());
        var rows = data.GetProperty("rows").EnumerateArray().Where(row => row[2].GetString() == itemCode).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal("saida", rows[0][1].GetString());
        Assert.Equal("entrada", rows[1][1].GetString());
    }

    [Fact]
    public async Task Approval_report_csv_and_xlsx_include_filtered_decisions_safely()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var token = await CreateUserAndLoginAsync(email, ["reports.view", "reports.export"]);
        var requestId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        var code = $"APR-{Guid.NewGuid():N}"[..16];
        Guid userId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userId = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!.Id;
            db.Requests.Add(new InternalRequest
            {
                Id = requestId,
                Code = code,
                RequesterId = userId,
                Flow = "interna",
                Status = "aprovada",
                Purpose = "Relatório de aprovação",
                CreatedAt = new DateTimeOffset(2025, 1, 3, 12, 0, 0, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2025, 1, 3, 12, 0, 0, TimeSpan.Zero)
            });
            db.ApprovalRules.Add(new ApprovalRule { Id = ruleId, Name = "Regra relatório", Criterion = "valor", Value = "100" });
            db.RequestApprovals.AddRange(
                new RequestApproval
                {
                    Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
                    RequestId = requestId,
                    RuleId = ruleId,
                    Decision = "aprovado",
                    Justification = "=1+1",
                    DecidedById = userId,
                    DecidedAt = new DateTimeOffset(2025, 1, 4, 10, 30, 0, TimeSpan.Zero),
                    CreatedAt = new DateTimeOffset(2025, 1, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999)
                },
                new RequestApproval
                {
                    Id = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                    RequestId = requestId,
                    Decision = "pendente",
                    Justification = "Sem nomes opcionais",
                    CreatedAt = new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero)
                },
                new RequestApproval
                {
                    Id = Guid.NewGuid(),
                    RequestId = requestId,
                    Decision = "reprovado",
                    Justification = "Fora do período",
                    CreatedAt = new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero)
                });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/approvals?from=2025-01-01&to=2025-01-31");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("Aprovações", data.GetProperty("title").GetString());
        Assert.Equal(new[] { "Solicitação", "Decisão", "Justificativa", "Por", "Em", "Regra" }, data.GetProperty("columns").EnumerateArray().Select(value => value.GetString()).ToArray());
        var rows = data.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        var row = rows[0];
        Assert.Equal(code, row[0].GetString());
        Assert.Equal("aprovado", row[1].GetString());
        Assert.Equal("=1+1", row[2].GetString());
        Assert.Equal("Dashboard test", row[3].GetString());
        Assert.Contains("2025-01-04T10:30", row[4].GetString());
        Assert.Equal("Regra relatório", row[5].GetString());
        Assert.Equal("pendente", rows[1][1].GetString());
        Assert.Equal(JsonValueKind.Null, rows[1][3].ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[1][4].ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[1][5].ValueKind);

        using var maximumDateRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/approvals?from=9999-12-31&to=9999-12-31");
        maximumDateRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var maximumDateResponse = await _client.SendAsync(maximumDateRequest);
        Assert.Equal(HttpStatusCode.OK, maximumDateResponse.StatusCode);

        using var csvRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/approvals/export.csv?from=2025-01-01&to=2025-01-31");
        csvRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var csvResponse = await _client.SendAsync(csvRequest);
        Assert.Equal(HttpStatusCode.OK, csvResponse.StatusCode);
        Assert.Equal("text/csv", csvResponse.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".csv", csvResponse.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var csv = Encoding.UTF8.GetString(await csvResponse.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains($"\"{code}\";\"aprovado\";\"'=1+1\"", csv, StringComparison.Ordinal);

        using var xlsxRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/approvals/export.xlsx?from=2025-01-01&to=2025-01-31");
        xlsxRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var xlsxResponse = await _client.SendAsync(xlsxRequest);
        Assert.Equal(HttpStatusCode.OK, xlsxResponse.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsxResponse.Content.Headers.ContentType?.MediaType);
        using var archive = new ZipArchive(new MemoryStream(await xlsxResponse.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        using var sheet = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheetXml = await sheet.ReadToEndAsync();
        Assert.Contains(code, sheetXml, StringComparison.Ordinal);
        Assert.Contains("=1+1", sheetXml, StringComparison.Ordinal);
        Assert.DoesNotContain("Fora do período", sheetXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", sheetXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Protocol_report_json_csv_and_xlsx_filter_by_event_and_dates_without_private_signatures()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var token = await CreateUserAndLoginAsync(email, ["reports.view", "reports.export"]);
        var eventId = Guid.NewGuid();
        var industryId = Guid.NewGuid();
        var industryName = $"ProtocolIndustry-{Guid.NewGuid():N}";
        Guid userId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userId = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!.Id;
            db.Events.Add(new Event { Id = eventId, Name = "Test event protocol", Status = "planejado" });
            db.Industries.Add(new Industry { Id = industryId, Name = industryName, NameKey = industryName.ToUpperInvariant() });
            db.Deliveries.AddRange(
                new Delivery
                {
                    Id = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
                    Code = "PROT-REPORT-01",
                    Type = "feirao",
                    EventId = eventId,
                    IndustryId = industryId,
                    DeliveredById = userId,
                    ReceivedByName = "Recebedor da sprint",
                    SignaturePath = "private/test-signature",
                    IdempotencyKeyHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                    RequestHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                    CreatedAt = new DateTimeOffset(2025, 1, 31, 23, 59, 59, 999, TimeSpan.Zero)
                },
                new Delivery
                {
                    Id = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                    Code = "PROT-REPORT-02",
                    Type = "feirao",
                    EventId = eventId,
                    DeliveredById = userId,
                    ReceivedByName = "Sem indústria",
                    SignaturePath = "private/test-signature-2",
                    IdempotencyKeyHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                    RequestHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                    CreatedAt = new DateTimeOffset(2025, 1, 20, 10, 0, 0, TimeSpan.Zero)
                },
                new Delivery
                {
                    Code = "PROT-REPORT-03",
                    Type = "feirao",
                    EventId = eventId,
                    DeliveredById = userId,
                    ReceivedByName = "Outro evento",
                    SignaturePath = "private/test-signature-3",
                    IdempotencyKeyHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                    RequestHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                    CreatedAt = new DateTimeOffset(2024, 12, 31, 23, 59, 0, TimeSpan.Zero)
                });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reports/protocols?from=2025-01-01&to=2025-01-31&event_id={eventId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("Protocolos", data.GetProperty("title").GetString());
        Assert.Equal(new[] { "Código", "Tipo", "Recebedor", "Entregue por", "Evento", "Indústria", "Data" }, data.GetProperty("columns").EnumerateArray().Select(value => value.GetString()).ToArray());
        var rows = data.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal("PROT-REPORT-01", rows[0][0].GetString());
        Assert.Equal("Recebedor da sprint", rows[0][2].GetString());
        Assert.Equal("Dashboard test", rows[0][3].GetString());
        Assert.Equal("Test event protocol", rows[0][4].GetString());
        Assert.Equal(industryName, rows[0][5].GetString());
        Assert.Equal("PROT-REPORT-02", rows[1][0].GetString());
        Assert.Equal(JsonValueKind.Null, rows[1][5].ValueKind);
        Assert.DoesNotContain("SignaturePath", json.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("test-signature", json.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);

        using var csvRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reports/protocols/export.csv?from=2025-01-01&to=2025-01-31&event_id={eventId}");
        csvRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var csvResponse = await _client.SendAsync(csvRequest);
        Assert.Equal(HttpStatusCode.OK, csvResponse.StatusCode);
        Assert.Equal("text/csv", csvResponse.Content.Headers.ContentType?.MediaType);
        var csv = Encoding.UTF8.GetString(await csvResponse.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains("\"PROT-REPORT-01\";\"feirao\";\"Recebedor da sprint\"", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("test-signature", csv, StringComparison.OrdinalIgnoreCase);

        using var xlsxRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reports/protocols/export.xlsx?from=2025-01-01&to=2025-01-31&event_id={eventId}");
        xlsxRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var xlsxResponse = await _client.SendAsync(xlsxRequest);
        Assert.Equal(HttpStatusCode.OK, xlsxResponse.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsxResponse.Content.Headers.ContentType?.MediaType);
        using var archive = new ZipArchive(new MemoryStream(await xlsxResponse.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        using var sheet = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheetXml = await sheet.ReadToEndAsync();
        Assert.Contains("PROT-REPORT-01", sheetXml, StringComparison.Ordinal);
        Assert.Contains("PROT-REPORT-02", sheetXml, StringComparison.Ordinal);
        Assert.DoesNotContain("PROT-REPORT-03", sheetXml, StringComparison.Ordinal);
        Assert.DoesNotContain("test-signature", sheetXml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<f>", sheetXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Event_balance_report_requires_event_and_returns_ordered_allocations()
    {
        var token = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", ["reports.view", "reports.export"]);
        var eventId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var industryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var industryName = $"BalanceIndustry-{Guid.NewGuid():N}";
        var itemCode = $"BAL-{Guid.NewGuid():N}"[..16];
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Events.Add(new Event { Id = eventId, Name = "Balance report event", Status = "planejado" });
            db.Categories.Add(new Category { Id = categoryId, Name = $"BalanceCategory-{Guid.NewGuid():N}", NameKey = $"BC-{Guid.NewGuid():N}" });
            db.Industries.Add(new Industry { Id = industryId, Name = industryName, NameKey = industryName.ToUpperInvariant() });
            db.CatalogItems.Add(new CatalogItem { Id = itemId, Code = itemCode, Name = "Balance item", CategoryId = categoryId });
            db.EventAllocations.Add(new EventAllocation { EventId = eventId, IndustryId = industryId, ItemId = itemId, QuantityAllocated = 14, QuantityWithdrawn = 5 });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reports/event_balance?event_id={eventId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("Saldo por indústria no evento", data.GetProperty("title").GetString());
        Assert.Equal(new[] { "Indústria", "Código", "Brinde", "Alocado", "Retirado", "Saldo" }, data.GetProperty("columns").EnumerateArray().Select(value => value.GetString()).ToArray());
        var row = Assert.Single(data.GetProperty("rows").EnumerateArray());
        Assert.Equal(industryName, row[0].GetString());
        Assert.Equal(itemCode, row[1].GetString());
        Assert.Equal(14, row[3].GetInt32());
        Assert.Equal(5, row[4].GetInt32());
        Assert.Equal(9, row[5].GetInt32());

        using var missingEventRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/event_balance");
        missingEventRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var missingEventResponse = await _client.SendAsync(missingEventRequest);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingEventResponse.StatusCode);

        using var csvRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reports/event_balance/export.csv?event_id={eventId}");
        csvRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var csvResponse = await _client.SendAsync(csvRequest);
        Assert.Equal(HttpStatusCode.OK, csvResponse.StatusCode);
        Assert.Equal("text/csv", csvResponse.Content.Headers.ContentType?.MediaType);
        var csv = Encoding.UTF8.GetString(await csvResponse.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains($"\"{industryName}\";\"{itemCode}\";\"Balance item\";\"14\";\"5\";\"9\"", csv, StringComparison.Ordinal);

        using var xlsxRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reports/event_balance/export.xlsx?event_id={eventId}");
        xlsxRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var xlsxResponse = await _client.SendAsync(xlsxRequest);
        Assert.Equal(HttpStatusCode.OK, xlsxResponse.StatusCode);
        using var archive = new ZipArchive(new MemoryStream(await xlsxResponse.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        using var sheet = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheetXml = await sheet.ReadToEndAsync();
        Assert.Contains(industryName, sheetXml, StringComparison.Ordinal);
        Assert.Contains(itemCode, sheetXml, StringComparison.Ordinal);
        Assert.Contains("<v>14</v>", sheetXml, StringComparison.Ordinal);
        Assert.Contains("<v>5</v>", sheetXml, StringComparison.Ordinal);
        Assert.Contains("<v>9</v>", sheetXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", sheetXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stock_industry_report_groups_legacy_movement_totals_and_applies_industry_filter()
    {
        var token = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", ["reports.view", "reports.export"]);
        var categoryId = Guid.NewGuid();
        var industryId = Guid.NewGuid();
        var otherIndustryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var itemCode = $"SI-{Guid.NewGuid():N}"[..14];
        var industryName = $"StockIndustry-{Guid.NewGuid():N}";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Categories.Add(new Category { Id = categoryId, Name = $"StockIndustryCategory-{Guid.NewGuid():N}", NameKey = $"SIC-{Guid.NewGuid():N}" });
            db.Industries.AddRange(
                new Industry { Id = industryId, Name = industryName, NameKey = industryName.ToUpperInvariant() },
                new Industry { Id = otherIndustryId, Name = $"Other-{Guid.NewGuid():N}", NameKey = $"OTHER-{Guid.NewGuid():N}" });
            db.CatalogItems.Add(new CatalogItem { Id = itemId, Code = itemCode, Name = "=1+1\u0001😀", CategoryId = categoryId, Kind = "brinde" });
            db.StockMovements.AddRange(
                new StockMovement { ItemId = itemId, IndustryId = industryId, Type = "entrada", Quantity = 8 },
                new StockMovement { ItemId = itemId, IndustryId = industryId, Type = "saida", Quantity = -3 },
                new StockMovement { ItemId = itemId, IndustryId = industryId, Type = "ajuste", Quantity = 2 },
                new StockMovement { ItemId = itemId, IndustryId = industryId, Type = "estorno", Quantity = -1 },
                new StockMovement { ItemId = itemId, IndustryId = otherIndustryId, Type = "entrada", Quantity = 100 });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reports/stock_industry?industry_id={industryId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("Estoque por indústria (recebido / retirado / saldo)", data.GetProperty("title").GetString());
        Assert.Equal(new[] { "Indústria", "Código", "Brinde", "Recebido", "Retirado", "Saldo" }, data.GetProperty("columns").EnumerateArray().Select(value => value.GetString()).ToArray());
        var row = Assert.Single(data.GetProperty("rows").EnumerateArray(), value => value[1].GetString() == itemCode);
        Assert.Equal(industryName, row[0].GetString());
        Assert.Equal("=1+1\u0001😀", row[2].GetString());
        Assert.Equal(8, row[3].GetInt32());
        Assert.Equal(3, row[4].GetInt32());
        Assert.Equal(6, row[5].GetInt32());

        using var csvRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reports/stock_industry/export.csv?industry_id={industryId}");
        csvRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var csvResponse = await _client.SendAsync(csvRequest);
        Assert.Equal(HttpStatusCode.OK, csvResponse.StatusCode);
        Assert.Equal("text/csv", csvResponse.Content.Headers.ContentType?.MediaType);
        var csv = Encoding.UTF8.GetString(await csvResponse.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains($"\"{industryName}\";\"{itemCode}\";\"'=1+1\u0001😀\";\"8\";\"3\";\"6\"", csv, StringComparison.Ordinal);

        using var xlsxRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reports/stock_industry/export.xlsx?industry_id={industryId}");
        xlsxRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var xlsxResponse = await _client.SendAsync(xlsxRequest);
        Assert.Equal(HttpStatusCode.OK, xlsxResponse.StatusCode);
        using var archive = new ZipArchive(new MemoryStream(await xlsxResponse.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        using var sheet = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheetXml = await sheet.ReadToEndAsync();
        _ = XDocument.Parse(sheetXml);
        Assert.Contains("=1+1", sheetXml, StringComparison.Ordinal);
        Assert.Contains("😀", sheetXml, StringComparison.Ordinal);
        Assert.Contains(itemCode, sheetXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", sheetXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Industry_stock_reports_assign_unattributed_receipts_only_when_item_has_one_ledger_industry()
    {
        var token = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", ["reports.view", "stock.view"]);
        var categoryId = Guid.NewGuid();
        var firstIndustryId = Guid.NewGuid();
        var secondIndustryId = Guid.NewGuid();
        var uniqueItemId = Guid.NewGuid();
        var ambiguousItemId = Guid.NewGuid();
        var uniqueCode = $"UNI-{Guid.NewGuid():N}"[..14];
        var ambiguousCode = $"AMB-{Guid.NewGuid():N}"[..14];
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Categories.Add(new Category { Id = categoryId, Name = $"Attribution-{categoryId:N}", NameKey = $"ATTR-{categoryId:N}" });
            db.Industries.AddRange(
                new Industry { Id = firstIndustryId, Name = $"Attribution A {firstIndustryId:N}", NameKey = $"ATRA-{firstIndustryId:N}" },
                new Industry { Id = secondIndustryId, Name = $"Attribution B {secondIndustryId:N}", NameKey = $"ATRB-{secondIndustryId:N}" });
            db.CatalogItems.AddRange(
                new CatalogItem { Id = uniqueItemId, Code = uniqueCode, Name = "Unique industry item", CategoryId = categoryId },
                new CatalogItem { Id = ambiguousItemId, Code = ambiguousCode, Name = "Ambiguous industry item", CategoryId = categoryId });
            db.StockMovements.AddRange(
                new StockMovement { ItemId = uniqueItemId, IndustryId = firstIndustryId, Type = "entrada", Quantity = 4 },
                new StockMovement { ItemId = uniqueItemId, IndustryId = firstIndustryId, Type = "saida", Quantity = -1 },
                new StockMovement { ItemId = uniqueItemId, Type = "entrada", Quantity = 3 },
                new StockMovement { ItemId = ambiguousItemId, IndustryId = firstIndustryId, Type = "entrada", Quantity = 5 },
                new StockMovement { ItemId = ambiguousItemId, IndustryId = secondIndustryId, Type = "entrada", Quantity = 7 },
                new StockMovement { ItemId = ambiguousItemId, Type = "entrada", Quantity = 11 });
            await db.SaveChangesAsync();
        }

        using var legacyReportRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/stock_industry");
        legacyReportRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var legacyReportResponse = await _client.SendAsync(legacyReportRequest);
        Assert.Equal(HttpStatusCode.OK, legacyReportResponse.StatusCode);
        using var legacyReportJson = JsonDocument.Parse(await legacyReportResponse.Content.ReadAsStringAsync());
        var reportRows = legacyReportJson.RootElement.GetProperty("data").GetProperty("rows").EnumerateArray().ToArray();
        var uniqueReportRow = Assert.Single(reportRows, row => row[1].GetString() == uniqueCode);
        Assert.Equal(7, uniqueReportRow[3].GetInt32());
        Assert.Equal(1, uniqueReportRow[4].GetInt32());
        Assert.Equal(6, uniqueReportRow[5].GetInt32());
        var ambiguousReportRows = reportRows.Where(row => row[1].GetString() == ambiguousCode).ToArray();
        Assert.Equal(2, ambiguousReportRows.Length);
        Assert.Contains(ambiguousReportRows, row => row[0].GetString()!.Contains("Attribution A", StringComparison.Ordinal) && row[3].GetInt32() == 5);
        Assert.Contains(ambiguousReportRows, row => row[0].GetString()!.Contains("Attribution B", StringComparison.Ordinal) && row[3].GetInt32() == 7);

        using var stockRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/stock/by-industry");
        stockRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var stockResponse = await _client.SendAsync(stockRequest);
        Assert.Equal(HttpStatusCode.OK, stockResponse.StatusCode);
        using var stockJson = JsonDocument.Parse(await stockResponse.Content.ReadAsStringAsync());
        var stockRows = stockJson.RootElement.GetProperty("data").EnumerateArray().ToArray();
        var uniqueStockRow = Assert.Single(stockRows, row => row.GetProperty("item").GetProperty("code").GetString() == uniqueCode);
        Assert.Equal(7, uniqueStockRow.GetProperty("received").GetInt32());
        Assert.Equal(1, uniqueStockRow.GetProperty("withdrawn").GetInt32());
        Assert.Equal(6, uniqueStockRow.GetProperty("saldo").GetInt32());
        var ambiguousStockRows = stockRows.Where(row => row.GetProperty("item").GetProperty("code").GetString() == ambiguousCode).ToArray();
        Assert.Equal(2, ambiguousStockRows.Length);
        Assert.Contains(ambiguousStockRows, row => row.GetProperty("industry").GetProperty("id").GetGuid() == firstIndustryId && row.GetProperty("received").GetInt32() == 5);
        Assert.Contains(ambiguousStockRows, row => row.GetProperty("industry").GetProperty("id").GetGuid() == secondIndustryId && row.GetProperty("received").GetInt32() == 7);
    }

    [Fact]
    public async Task Withdrawals_report_filters_event_and_industry_with_legacy_columns()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var token = await CreateUserAndLoginAsync(email, ["reports.view", "reports.export"]);
        var categoryId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var otherEventId = Guid.NewGuid();
        var industryId = Guid.NewGuid();
        var otherIndustryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var otherDeliveryId = Guid.NewGuid();
        var otherIndustryDeliveryId = Guid.NewGuid();
        var itemCode = $"WD-{Guid.NewGuid():N}"[..14];
        var industryName = $"WithdrawIndustry-{Guid.NewGuid():N}";
        Guid userId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userId = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!.Id;
            db.Events.AddRange(new Event { Id = eventId, Name = "Withdraw event", Status = "planejado" }, new Event { Id = otherEventId, Name = "Other event", Status = "planejado" });
            db.Categories.Add(new Category { Id = categoryId, Name = $"WithdrawCategory-{Guid.NewGuid():N}", NameKey = $"WC-{Guid.NewGuid():N}" });
            db.Industries.AddRange(
                new Industry { Id = industryId, Name = industryName, NameKey = industryName.ToUpperInvariant() },
                new Industry { Id = otherIndustryId, Name = $"Other-{Guid.NewGuid():N}", NameKey = $"OTH-{Guid.NewGuid():N}" });
            db.CatalogItems.Add(new CatalogItem { Id = itemId, Code = itemCode, Name = "Withdrawal test item", CategoryId = categoryId });
            db.Deliveries.AddRange(
                new Delivery
                {
                    Id = deliveryId,
                    Code = $"WD-{deliveryId:N}"[..20],
                    Type = "feirao",
                    EventId = eventId,
                    IndustryId = industryId,
                    DeliveredById = userId,
                    ReceivedByName = "Recebedor relatório",
                    SignaturePath = "private/wd1",
                    IdempotencyKeyHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                    RequestHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                    CreatedAt = new DateTimeOffset(2025, 1, 31, 12, 0, 0, TimeSpan.Zero)
                },
                new Delivery
                {
                    Id = otherDeliveryId,
                    Code = $"WD-{otherDeliveryId:N}"[..20],
                    Type = "feirao",
                    EventId = otherEventId,
                    IndustryId = industryId,
                    DeliveredById = userId,
                    ReceivedByName = "Outro evento",
                    SignaturePath = "private/wd2",
                    IdempotencyKeyHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                    RequestHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                    CreatedAt = new DateTimeOffset(2025, 1, 30, 12, 0, 0, TimeSpan.Zero)
                },
                new Delivery
                {
                    Id = otherIndustryDeliveryId,
                    Code = $"WD-{otherIndustryDeliveryId:N}"[..20],
                    Type = "feirao",
                    EventId = eventId,
                    IndustryId = otherIndustryId,
                    DeliveredById = userId,
                    ReceivedByName = "Outra indústria",
                    SignaturePath = "private/wd3",
                    IdempotencyKeyHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                    RequestHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                    CreatedAt = new DateTimeOffset(2025, 1, 29, 12, 0, 0, TimeSpan.Zero)
                });
            db.StockMovements.AddRange(
                new StockMovement { ItemId = itemId, UserId = userId, IndustryId = industryId, DeliveryId = deliveryId, Type = "saida", Quantity = -4, Recipient = "Recebedor relatório", Purpose = "Retirada evento", CreatedAt = new DateTimeOffset(2025, 1, 31, 12, 0, 0, TimeSpan.Zero) },
                new StockMovement { ItemId = itemId, UserId = userId, IndustryId = industryId, DeliveryId = otherDeliveryId, Type = "saida", Quantity = -1, CreatedAt = new DateTimeOffset(2025, 1, 30, 12, 0, 0, TimeSpan.Zero) },
                new StockMovement { ItemId = itemId, UserId = userId, IndustryId = otherIndustryId, DeliveryId = otherIndustryDeliveryId, Type = "saida", Quantity = -5, CreatedAt = new DateTimeOffset(2025, 1, 29, 12, 0, 0, TimeSpan.Zero) },
                new StockMovement { ItemId = itemId, UserId = userId, IndustryId = industryId, DeliveryId = deliveryId, Type = "saida", Quantity = -2, CreatedAt = new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero) });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reports/withdrawals?from=2025-01-01&to=2025-01-31&industry_id={industryId}&event_id={eventId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("Retiradas", data.GetProperty("title").GetString());
        Assert.Equal(new[] { "Data/hora", "Indústria", "Código", "Brinde", "Qtd", "Quem retirou", "Responsável", "Evento", "Finalidade" }, data.GetProperty("columns").EnumerateArray().Select(value => value.GetString()).ToArray());
        var row = Assert.Single(data.GetProperty("rows").EnumerateArray());
        Assert.Equal(industryName, row[1].GetString());
        Assert.Equal(itemCode, row[2].GetString());
        Assert.Equal(4, row[4].GetInt32());
        Assert.Equal("Recebedor relatório", row[5].GetString());
        Assert.Equal("Dashboard test", row[6].GetString());
        Assert.Equal("Withdraw event", row[7].GetString());
        Assert.Equal("Retirada evento", row[8].GetString());

        using var csvRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reports/withdrawals/export.csv?from=2025-01-01&to=2025-01-31&industry_id={industryId}&event_id={eventId}");
        csvRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var csvResponse = await _client.SendAsync(csvRequest);
        Assert.Equal(HttpStatusCode.OK, csvResponse.StatusCode);
        Assert.Equal("text/csv", csvResponse.Content.Headers.ContentType?.MediaType);
        var csv = Encoding.UTF8.GetString(await csvResponse.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains("Retirada evento", csv, StringComparison.Ordinal);

        using var xlsxRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reports/withdrawals/export.xlsx?from=2025-01-01&to=2025-01-31&industry_id={industryId}&event_id={eventId}");
        xlsxRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var xlsxResponse = await _client.SendAsync(xlsxRequest);
        Assert.Equal(HttpStatusCode.OK, xlsxResponse.StatusCode);
        using var archive = new ZipArchive(new MemoryStream(await xlsxResponse.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        using var sheet = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheetXml = await sheet.ReadToEndAsync();
        Assert.Contains("Retirada evento", sheetXml, StringComparison.Ordinal);
        Assert.DoesNotContain("private/wd1", sheetXml, StringComparison.Ordinal);
        Assert.DoesNotContain("private/wd2", sheetXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", sheetXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Event_report_combines_allocations_and_withdrawal_details()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var token = await CreateUserAndLoginAsync(email, ["reports.view", "reports.export"]);
        var eventId = Guid.NewGuid();
        var industryId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var itemCode = $"ER-{Guid.NewGuid():N}"[..14];
        Guid userId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userId = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!.Id;
            db.Events.Add(new Event { Id = eventId, Name = "Event report test", Status = "aberto" });
            db.Industries.Add(new Industry { Id = industryId, Name = "Event report industry", NameKey = $"ERI-{Guid.NewGuid():N}" });
            db.Categories.Add(new Category { Id = categoryId, Name = $"EventReportCategory-{Guid.NewGuid():N}", NameKey = $"ERC-{Guid.NewGuid():N}" });
            db.CatalogItems.Add(new CatalogItem { Id = itemId, Code = itemCode, Name = "Event report item", CategoryId = categoryId });
            db.EventAllocations.Add(new EventAllocation
            {
                EventId = eventId,
                IndustryId = industryId,
                ItemId = itemId,
                QuantityAllocated = 10,
                QuantityWithdrawn = 3
            });
            db.Deliveries.Add(new Delivery
            {
                Id = deliveryId,
                Code = $"ER-{deliveryId:N}"[..20],
                Type = "feirao",
                EventId = eventId,
                IndustryId = industryId,
                DeliveredById = userId,
                ReceivedByName = "Recebedor evento",
                SignaturePath = "private/event-report",
                IdempotencyKeyHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                RequestHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                CreatedAt = new DateTimeOffset(2025, 4, 10, 9, 30, 0, TimeSpan.Zero)
            });
            db.DeliveryItems.Add(new DeliveryItem { DeliveryId = deliveryId, ItemId = itemId, Quantity = 3, BalanceAfter = 7 });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reports/event_report?event_id={eventId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("Evento: Event report test", data.GetProperty("title").GetString());
        Assert.Equal(new[] { "Indústria / tipo", "Código / data", "Brinde", "Estoque inicial", "Retirado", "Saldo / quem" }, data.GetProperty("columns").EnumerateArray().Select(value => value.GetString()).ToArray());
        var rows = data.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal("Event report industry", rows[0][0].GetString());
        Assert.Equal(itemCode, rows[0][1].GetString());
        Assert.Equal(10, rows[0][3].GetInt32());
        Assert.Equal(3, rows[0][4].GetInt32());
        Assert.Equal(7, rows[0][5].GetInt32());
        Assert.Equal("RETIRADA", rows[1][0].GetString());
        Assert.Equal("Event report item", rows[1][3].GetString());
        Assert.Equal(3, rows[1][4].GetInt32());
        Assert.Equal("Recebedor evento", rows[1][5].GetString());

        using var invalidRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/event_report");
        invalidRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var invalidResponse = await _client.SendAsync(invalidRequest);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidResponse.StatusCode);

        using var csvRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reports/event_report/export.csv?event_id={eventId}");
        csvRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var csvResponse = await _client.SendAsync(csvRequest);
        Assert.Equal(HttpStatusCode.OK, csvResponse.StatusCode);
        Assert.Equal("text/csv", csvResponse.Content.Headers.ContentType?.MediaType);
        var csv = Encoding.UTF8.GetString(await csvResponse.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains(itemCode, csv, StringComparison.Ordinal);
        Assert.Contains("Recebedor evento", csv, StringComparison.Ordinal);

        using var xlsxRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reports/event_report/export.xlsx?event_id={eventId}");
        xlsxRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var xlsxResponse = await _client.SendAsync(xlsxRequest);
        Assert.Equal(HttpStatusCode.OK, xlsxResponse.StatusCode);
        using var archive = new ZipArchive(new MemoryStream(await xlsxResponse.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        using var sheet = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheetXml = await sheet.ReadToEndAsync();
        Assert.Contains(itemCode, sheetXml, StringComparison.Ordinal);
        Assert.Contains("Recebedor evento", sheetXml, StringComparison.Ordinal);
        Assert.DoesNotContain("private/event-report", sheetXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", sheetXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requests_report_filters_creation_period_and_returns_legacy_columns()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var token = await CreateUserAndLoginAsync(email, ["reports.view", "reports.export"]);
        var departmentId = Guid.NewGuid();
        var industryId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var departmentName = $"RequestReportDept-{Guid.NewGuid():N}";
        var industryName = $"RequestReportIndustry-{Guid.NewGuid():N}";
        var code = $"RR-{Guid.NewGuid():N}"[..14];
        Guid userId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userId = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!.Id;
            db.Departments.Add(new Department { Id = departmentId, Name = departmentName, NameKey = departmentName.ToUpperInvariant() });
            db.Industries.Add(new Industry { Id = industryId, Name = industryName, NameKey = industryName.ToUpperInvariant() });
            db.Requests.Add(new InternalRequest
            {
                Id = requestId,
                Code = code,
                RequesterId = userId,
                DepartmentId = departmentId,
                IndustryId = industryId,
                Flow = "interna",
                Status = "finalizada",
                Purpose = "Relatório de solicitações",
                Recipient = "Recebedor",
                PurchaseTicketNo = "CH-123",
                TotalValue = 123.45m,
                CreatedAt = new DateTimeOffset(2025, 1, 31, 23, 59, 59, TimeSpan.Zero),
                StatusChangedAt = new DateTimeOffset(2025, 2, 2, 10, 0, 0, TimeSpan.Zero)
            });
            db.Requests.Add(new InternalRequest
            {
                Code = $"RR-{Guid.NewGuid():N}"[..14],
                RequesterId = userId,
                Flow = "interna",
                Status = "rascunho",
                Purpose = "fora do período",
                CreatedAt = new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero)
            });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/requests?from=2025-01-01&to=2025-01-31");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("Solicitações", data.GetProperty("title").GetString());
        Assert.Equal(new[] { "Código", "Status", "Solicitante", "Depto", "Indústria", "Finalidade", "Destinatário", "Chamado", "Valor", "Criada", "Finalizada" }, data.GetProperty("columns").EnumerateArray().Select(value => value.GetString()).ToArray());
        var row = Assert.Single(data.GetProperty("rows").EnumerateArray(), value => value[0].GetString() == code);
        Assert.Equal(code, row[0].GetString());
        Assert.Equal("finalizada", row[1].GetString());
        Assert.Equal("Dashboard test", row[2].GetString());
        Assert.Equal(departmentName, row[3].GetString());
        Assert.Equal(industryName, row[4].GetString());
        Assert.Equal("Relatório de solicitações", row[5].GetString());
        Assert.Equal("Recebedor", row[6].GetString());
        Assert.Equal("CH-123", row[7].GetString());
        Assert.Equal(123.45m, row[8].GetDecimal());
        Assert.Equal(new DateTimeOffset(2025, 2, 2, 10, 0, 0, TimeSpan.Zero), row[10].GetDateTimeOffset());

        using var csvRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/requests/export.csv?from=2025-01-01&to=2025-01-31");
        csvRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var csvResponse = await _client.SendAsync(csvRequest);
        Assert.Equal(HttpStatusCode.OK, csvResponse.StatusCode);
        Assert.Equal("text/csv", csvResponse.Content.Headers.ContentType?.MediaType);
        var csv = Encoding.UTF8.GetString(await csvResponse.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains($"\"{code}\";\"finalizada\"", csv, StringComparison.Ordinal);

        using var xlsxRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/requests/export.xlsx?from=2025-01-01&to=2025-01-31");
        xlsxRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var xlsxResponse = await _client.SendAsync(xlsxRequest);
        Assert.Equal(HttpStatusCode.OK, xlsxResponse.StatusCode);
        using var archive = new ZipArchive(new MemoryStream(await xlsxResponse.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        using var sheet = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheetXml = await sheet.ReadToEndAsync();
        Assert.Contains(code, sheetXml, StringComparison.Ordinal);
        Assert.Contains("Relatório de solicitações", sheetXml, StringComparison.Ordinal);
        Assert.DoesNotContain("fora do período", sheetXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", sheetXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task By_item_report_groups_exits_and_applies_inclusive_dates()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var token = await CreateUserAndLoginAsync(email, ["reports.view", "reports.export"]);
        var categoryId = Guid.NewGuid();
        var industryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var itemCode = $"BI-{Guid.NewGuid():N}"[..14];
        var industryName = $"ByItemIndustry-{Guid.NewGuid():N}";
        Guid userId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userId = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!.Id;
            db.Categories.Add(new Category { Id = categoryId, Name = $"ByItemCategory-{Guid.NewGuid():N}", NameKey = $"BIC-{Guid.NewGuid():N}" });
            db.Industries.Add(new Industry { Id = industryId, Name = industryName, NameKey = $"BII-{Guid.NewGuid():N}" });
            db.CatalogItems.Add(new CatalogItem { Id = itemId, Code = itemCode, Name = "=1+1", CategoryId = categoryId, UnitValue = 2.5m });
            db.StockMovements.AddRange(
                new StockMovement { ItemId = itemId, UserId = userId, IndustryId = industryId, Type = "saida", Quantity = -3, CreatedAt = new DateTimeOffset(2025, 1, 31, 23, 59, 59, TimeSpan.Zero) },
                new StockMovement { ItemId = itemId, UserId = userId, IndustryId = industryId, Type = "saida", Quantity = -9, CreatedAt = new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero) });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/by_item?from=2025-01-01&to=2025-01-31");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("Distribuição por brinde", data.GetProperty("title").GetString());
        Assert.Equal(new[] { "Código", "Nome", "Indústria", "Unidades", "Valor" }, data.GetProperty("columns").EnumerateArray().Select(value => value.GetString()).ToArray());
        var row = Assert.Single(data.GetProperty("rows").EnumerateArray(), value => value[0].GetString() == itemCode);
        Assert.Equal(itemCode, row[0].GetString());
        Assert.Equal("=1+1", row[1].GetString());
        Assert.Equal(industryName, row[2].GetString());
        Assert.Equal(3, row[3].GetInt32());
        Assert.Equal(7.5m, row[4].GetDecimal());

        using var csvRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/by_item/export.csv?from=2025-01-01&to=2025-01-31");
        csvRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var csvResponse = await _client.SendAsync(csvRequest);
        Assert.Equal(HttpStatusCode.OK, csvResponse.StatusCode);
        Assert.Equal("text/csv", csvResponse.Content.Headers.ContentType?.MediaType);
        var csv = Encoding.UTF8.GetString(await csvResponse.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains($"\"{itemCode}\";\"'=1+1\";\"{industryName}\";\"3\";\"7.5\"", csv, StringComparison.Ordinal);

        using var xlsxRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/by_item/export.xlsx?from=2025-01-01&to=2025-01-31");
        xlsxRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var xlsxResponse = await _client.SendAsync(xlsxRequest);
        Assert.Equal(HttpStatusCode.OK, xlsxResponse.StatusCode);
        using var archive = new ZipArchive(new MemoryStream(await xlsxResponse.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        using var sheet = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheetXml = await sheet.ReadToEndAsync();
        Assert.Contains(itemCode, sheetXml, StringComparison.Ordinal);
        Assert.Contains("=1+1", sheetXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", sheetXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task By_department_report_aggregates_exit_units_and_industries()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var token = await CreateUserAndLoginAsync(email, ["reports.view", "reports.export"]);
        var departmentId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var firstIndustryId = Guid.NewGuid();
        var secondIndustryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var departmentName = $"ByDept-{Guid.NewGuid():N}";
        var itemCode = $"BD-{Guid.NewGuid():N}"[..14];
        Guid userId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userId = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!.Id;
            db.Departments.Add(new Department { Id = departmentId, Name = departmentName, NameKey = departmentName.ToUpperInvariant() });
            db.Categories.Add(new Category { Id = categoryId, Name = $"ByDeptCategory-{Guid.NewGuid():N}", NameKey = $"BDC-{Guid.NewGuid():N}" });
            db.Industries.AddRange(
                new Industry { Id = firstIndustryId, Name = "A-Industry", NameKey = $"BDI-A-{Guid.NewGuid():N}" },
                new Industry { Id = secondIndustryId, Name = "Z-Industry", NameKey = $"BDI-Z-{Guid.NewGuid():N}" });
            db.CatalogItems.Add(new CatalogItem { Id = itemId, Code = itemCode, Name = "Department grouped item", CategoryId = categoryId, UnitValue = 1.25m });
            db.StockMovements.AddRange(
                new StockMovement { ItemId = itemId, UserId = userId, IndustryId = secondIndustryId, DepartmentId = departmentId, Type = "saida", Quantity = -2, CreatedAt = new DateTimeOffset(2025, 1, 31, 10, 0, 0, TimeSpan.Zero) },
                new StockMovement { ItemId = itemId, UserId = userId, IndustryId = firstIndustryId, DepartmentId = departmentId, Type = "saida", Quantity = -3, CreatedAt = new DateTimeOffset(2025, 1, 31, 11, 0, 0, TimeSpan.Zero) });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/by_department?from=2025-01-01&to=2025-01-31");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("Distribuição por departamento", data.GetProperty("title").GetString());
        Assert.Equal(new[] { "Nome", "Indústria", "Unidades", "Valor" }, data.GetProperty("columns").EnumerateArray().Select(value => value.GetString()).ToArray());
        var row = Assert.Single(data.GetProperty("rows").EnumerateArray(), value => value[0].GetString() == departmentName);
        Assert.Equal("A-Industry, Z-Industry", row[1].GetString());
        Assert.Equal(5, row[2].GetInt32());
        Assert.Equal(6.25m, row[3].GetDecimal());

        using var csvRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/by_department/export.csv?from=2025-01-01&to=2025-01-31");
        csvRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var csvResponse = await _client.SendAsync(csvRequest);
        Assert.Equal(HttpStatusCode.OK, csvResponse.StatusCode);
        Assert.Equal("text/csv", csvResponse.Content.Headers.ContentType?.MediaType);
        var csv = Encoding.UTF8.GetString(await csvResponse.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains($"\"{departmentName}\";\"A-Industry, Z-Industry\";\"5\";\"6.25\"", csv, StringComparison.Ordinal);

        using var xlsxRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/by_department/export.xlsx?from=2025-01-01&to=2025-01-31");
        xlsxRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var xlsxResponse = await _client.SendAsync(xlsxRequest);
        Assert.Equal(HttpStatusCode.OK, xlsxResponse.StatusCode);
        using var archive = new ZipArchive(new MemoryStream(await xlsxResponse.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        using var sheet = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheetXml = await sheet.ReadToEndAsync();
        Assert.Contains(departmentName, sheetXml, StringComparison.Ordinal);
        Assert.Contains("A-Industry, Z-Industry", sheetXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", sheetXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task By_industry_report_groups_exits_and_orders_by_units()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var token = await CreateUserAndLoginAsync(email, ["reports.view", "reports.export"]);
        var categoryId = Guid.NewGuid();
        var firstIndustryId = Guid.NewGuid();
        var secondIndustryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var firstName = $"ByIndustry-A-{Guid.NewGuid():N}";
        var secondName = $"ByIndustry-Z-{Guid.NewGuid():N}";
        var itemCode = $"BI-{Guid.NewGuid():N}"[..14];
        Guid userId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userId = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!.Id;
            db.Categories.Add(new Category { Id = categoryId, Name = $"ByIndustryCategory-{Guid.NewGuid():N}", NameKey = $"BIC-{Guid.NewGuid():N}" });
            db.Industries.AddRange(
                new Industry { Id = firstIndustryId, Name = firstName, NameKey = $"BIA-{Guid.NewGuid():N}" },
                new Industry { Id = secondIndustryId, Name = secondName, NameKey = $"BIZ-{Guid.NewGuid():N}" });
            db.CatalogItems.Add(new CatalogItem { Id = itemId, Code = itemCode, Name = "Industry grouped item", CategoryId = categoryId, UnitValue = 3m });
            db.StockMovements.AddRange(
                new StockMovement { ItemId = itemId, UserId = userId, IndustryId = firstIndustryId, Type = "saida", Quantity = -4, CreatedAt = new DateTimeOffset(2025, 1, 31, 23, 59, 59, TimeSpan.Zero) },
                new StockMovement { ItemId = itemId, UserId = userId, IndustryId = secondIndustryId, Type = "saida", Quantity = -2, CreatedAt = new DateTimeOffset(2025, 1, 30, 12, 0, 0, TimeSpan.Zero) });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/by_industry?from=2025-01-01&to=2025-01-31");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("Distribuição por indústria", data.GetProperty("title").GetString());
        Assert.Equal(new[] { "Nome", "Unidades", "Valor" }, data.GetProperty("columns").EnumerateArray().Select(value => value.GetString()).ToArray());
        var rows = data.GetProperty("rows").EnumerateArray().Where(value => value[0].GetString() == firstName || value[0].GetString() == secondName).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(firstName, rows[0][0].GetString());
        Assert.Equal(4, rows[0][1].GetInt32());
        Assert.Equal(12m, rows[0][2].GetDecimal());
        Assert.Equal(secondName, rows[1][0].GetString());
        Assert.Equal(2, rows[1][1].GetInt32());
        Assert.Equal(6m, rows[1][2].GetDecimal());

        using var csvRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/by_industry/export.csv?from=2025-01-01&to=2025-01-31");
        csvRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var csvResponse = await _client.SendAsync(csvRequest);
        Assert.Equal(HttpStatusCode.OK, csvResponse.StatusCode);
        Assert.Equal("text/csv", csvResponse.Content.Headers.ContentType?.MediaType);
        var csv = Encoding.UTF8.GetString(await csvResponse.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains($"\"{firstName}\";\"4\";\"12\"", csv, StringComparison.Ordinal);
        Assert.Contains($"\"{secondName}\";\"2\";\"6\"", csv, StringComparison.Ordinal);

        using var xlsxRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/by_industry/export.xlsx?from=2025-01-01&to=2025-01-31");
        xlsxRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var xlsxResponse = await _client.SendAsync(xlsxRequest);
        Assert.Equal(HttpStatusCode.OK, xlsxResponse.StatusCode);
        using var archive = new ZipArchive(new MemoryStream(await xlsxResponse.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        using var sheet = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheetXml = await sheet.ReadToEndAsync();
        Assert.Contains(firstName, sheetXml, StringComparison.Ordinal);
        Assert.Contains(secondName, sheetXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", sheetXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task By_requester_report_and_xlsx_export_use_request_owner_for_delivery_movements()
    {
        var requesterEmail = $"{Guid.NewGuid():N}@example.invalid";
        var executorEmail = $"{Guid.NewGuid():N}@example.invalid";
        var token = await CreateUserAndLoginAsync(requesterEmail, ["reports.view", "reports.export"]);
        var categoryId = Guid.NewGuid();
        var industryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        var requesterName = $"Requester-{Guid.NewGuid():N}";
        var industryName = $"RequesterIndustry-{Guid.NewGuid():N}";
        var itemCode = $"BR-{Guid.NewGuid():N}"[..14];
        Guid requesterId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<ApplicationDbContext>();
            requesterId = (await services.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(requesterEmail))!.Id;
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var requester = await users.FindByIdAsync(requesterId.ToString());
            requester!.FullName = requesterName;
            Assert.True((await users.UpdateAsync(requester)).Succeeded);
            var executor = new ApplicationUser { UserName = executorEmail, Email = executorEmail, EmailConfirmed = true, FullName = "Separate executor" };
            Assert.True((await users.CreateAsync(executor, "CorrectPassword1")).Succeeded);
            db.Categories.Add(new Category { Id = categoryId, Name = $"ByRequesterCategory-{Guid.NewGuid():N}", NameKey = $"BRC-{Guid.NewGuid():N}" });
            db.Industries.Add(new Industry { Id = industryId, Name = industryName, NameKey = $"BRI-{Guid.NewGuid():N}" });
            db.CatalogItems.Add(new CatalogItem { Id = itemId, Code = itemCode, Name = "Requester grouped item", CategoryId = categoryId, UnitValue = 1.5m });
            db.Requests.Add(new InternalRequest
            {
                Id = requestId,
                Code = $"BR-{requestId:N}"[..14],
                RequesterId = requesterId,
                Flow = "interna",
                Status = "pronta",
                Purpose = "Solicitação vinculada"
            });
            db.Deliveries.Add(new Delivery
            {
                Id = deliveryId,
                Code = $"BR-{deliveryId:N}"[..20],
                Type = "dia_a_dia",
                RequestId = requestId,
                IndustryId = industryId,
                DeliveredById = executor.Id,
                ReceivedByName = "Recebedor",
                SignaturePath = "private/by-requester",
                IdempotencyKeyHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                RequestHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                CreatedAt = new DateTimeOffset(2025, 1, 20, 12, 0, 0, TimeSpan.Zero)
            });
            db.StockMovements.Add(new StockMovement
            {
                ItemId = itemId,
                UserId = executor.Id,
                IndustryId = industryId,
                DeliveryId = deliveryId,
                Type = "saida",
                Quantity = -4,
                CreatedAt = new DateTimeOffset(2025, 1, 20, 12, 0, 0, TimeSpan.Zero)
            });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/by_requester?from=2025-01-01&to=2025-01-31");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("Distribuição por solicitante", data.GetProperty("title").GetString());
        Assert.Equal(new[] { "Nome", "Indústria", "Unidades", "Valor" }, data.GetProperty("columns").EnumerateArray().Select(value => value.GetString()).ToArray());
        var row = Assert.Single(data.GetProperty("rows").EnumerateArray(), value => value[0].GetString() == requesterName);
        Assert.Equal(industryName, row[1].GetString());
        Assert.Equal(4, row[2].GetInt32());
        Assert.Equal(6m, row[3].GetDecimal());

        using var xlsxRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/by_requester/export.xlsx?from=2025-01-01&to=2025-01-31");
        xlsxRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var xlsxResponse = await _client.SendAsync(xlsxRequest);
        Assert.Equal(HttpStatusCode.OK, xlsxResponse.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsxResponse.Content.Headers.ContentType?.MediaType);
        using var archive = new ZipArchive(new MemoryStream(await xlsxResponse.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        using var sheet = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheetXml = await sheet.ReadToEndAsync();
        Assert.Contains(requesterName, sheetXml, StringComparison.Ordinal);
        Assert.Contains(industryName, sheetXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", sheetXml, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Stock_report_csv_export_quotes_cells_and_neutralizes_formulas()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var token = await CreateUserAndLoginAsync(email, ["reports.view", "reports.export"]);
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var itemCode = $"CSV-{Guid.NewGuid():N}"[..15];
        const string formula = "=HYPERLINK(\"https://example.invalid\")";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Categories.Add(new Category { Id = categoryId, Name = $"CsvCategory-{Guid.NewGuid():N}", NameKey = $"CSVC-{Guid.NewGuid():N}" });
            db.CatalogItems.Add(new CatalogItem { Id = itemId, Code = itemCode, Name = formula, CategoryId = categoryId, UnitValue = 1.25m });
            db.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 1, Reserved = 0 });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/stock/export.csv");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".csv", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var csv = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains("\"Código\";\"Nome\"", csv, StringComparison.Ordinal);
        Assert.Contains("'=HYPERLINK(", csv, StringComparison.Ordinal);
        Assert.Contains("\"\"https://example.invalid\"\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stock_report_xlsx_export_returns_a_workbook_and_keeps_formula_like_values_as_text()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var token = await CreateUserAndLoginAsync(email, ["reports.view", "reports.export"]);
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var itemCode = $"XLSX-{Guid.NewGuid():N}"[..16];
        const string formula = "=1+1";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Categories.Add(new Category { Id = categoryId, Name = $"XlsxCategory-{Guid.NewGuid():N}", NameKey = $"XLSXC-{Guid.NewGuid():N}" });
            db.CatalogItems.Add(new CatalogItem { Id = itemId, Code = itemCode, Name = formula, CategoryId = categoryId, UnitValue = 1.25m });
            db.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 1, Reserved = 0 });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/stock/export.xlsx");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".xlsx", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        using var archive = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        Assert.Contains(archive.Entries, entry => entry.FullName == "xl/workbook.xml");
        using var sheet = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var xml = await sheet.ReadToEndAsync();
        Assert.Contains("Código", xml, StringComparison.Ordinal);
        Assert.Contains("=1+1", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stock_report_xlsx_export_requires_reports_export_permission()
    {
        var token = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", ["reports.view"]);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/stock/export.xlsx");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("entries", "entrada")]
    [InlineData("exits", "saida")]
    [InlineData("movements", "ajuste")]
    public async Task Movement_report_csv_exports_filtered_rows_safely(string report, string movementType)
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var token = await CreateUserAndLoginAsync(email, ["reports.view", "reports.export"]);
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var code = $"CSV-{Guid.NewGuid():N}"[..15];
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var userId = await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
            db.Categories.Add(new Category { Id = categoryId, Name = $"CsvMovement-{Guid.NewGuid():N}", NameKey = $"CSVM-{Guid.NewGuid():N}" });
            db.CatalogItems.Add(new CatalogItem { Id = itemId, Code = code, Name = "CSV movement", CategoryId = categoryId });
            db.StockMovements.Add(new StockMovement
            {
                ItemId = itemId,
                UserId = userId,
                Type = movementType,
                Quantity = 1,
                Purpose = "=1+1",
                CreatedAt = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero)
            });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/reports/{report}/export.csv?from=2025-01-01&to=2025-01-31&item_id={itemId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".csv", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var csv = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains($"\"{movementType}\"", csv, StringComparison.Ordinal);
        Assert.Contains("'=1+1", csv, StringComparison.Ordinal);

        using var xlsxRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/reports/{report}/export.xlsx?from=2025-01-01&to=2025-01-31&item_id={itemId}");
        xlsxRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var xlsxResponse = await _client.SendAsync(xlsxRequest);
        Assert.Equal(HttpStatusCode.OK, xlsxResponse.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsxResponse.Content.Headers.ContentType?.MediaType);
        using var archive = new ZipArchive(new MemoryStream(await xlsxResponse.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        using var sheet = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheetXml = await sheet.ReadToEndAsync();
        Assert.Contains(code, sheetXml, StringComparison.Ordinal);
        Assert.Contains("=1+1", sheetXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", sheetXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Low_stock_exports_preserve_report_rows_and_neutralize_formulas_in_csv_and_xlsx()
    {
        var token = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", ["reports.view", "reports.export"]);
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var itemCode = $"LC-{Guid.NewGuid():N}"[..14];
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Categories.Add(new Category { Id = categoryId, Name = $"LowCsv-{Guid.NewGuid():N}", NameKey = $"LC-{Guid.NewGuid():N}" });
            db.CatalogItems.Add(new CatalogItem
            {
                Id = itemId,
                Code = itemCode,
                Name = "=1+1",
                CategoryId = categoryId,
                MinStock = 5
            });
            db.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 2, Reserved = 0 });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/low_stock/export.csv");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".csv", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var csv = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync());
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains("\"Código\";\"Nome\";\"Indústria\";\"Disponível\";\"Mínimo\"", csv, StringComparison.Ordinal);
        Assert.Contains($"\"{itemCode}\";\"'=1+1\";\"—\";\"2\";\"5\"", csv, StringComparison.Ordinal);
        Assert.Contains("'=1+1", csv, StringComparison.Ordinal);

        using var xlsxRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/low_stock/export.xlsx");
        xlsxRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var xlsxResponse = await _client.SendAsync(xlsxRequest);
        Assert.Equal(HttpStatusCode.OK, xlsxResponse.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsxResponse.Content.Headers.ContentType?.MediaType);
        using var archive = new ZipArchive(new MemoryStream(await xlsxResponse.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        using var sheet = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheetXml = await sheet.ReadToEndAsync();
        Assert.Contains(itemCode, sheetXml, StringComparison.Ordinal);
        Assert.Contains("=1+1", sheetXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", sheetXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Report_csv_export_requires_export_permission_separately_from_report_view()
    {
        var token = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", ["reports.view"]);
        using var jsonRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports");
        jsonRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var jsonResponse = await _client.SendAsync(jsonRequest);
        Assert.Equal(HttpStatusCode.OK, jsonResponse.StatusCode);

        using var exportRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reports/low_stock/export.csv");
        exportRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var exportResponse = await _client.SendAsync(exportRequest);
        Assert.Equal(HttpStatusCode.Forbidden, exportResponse.StatusCode);
    }

    private async Task<string> CreateUserAndLoginAsync(string email, string[] permissions, Guid? industryId = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = new ApplicationRole($"role-{Guid.NewGuid():N}");
        Assert.True((await roles.CreateAsync(role)).Succeeded);
        foreach (var permission in permissions)
            Assert.True((await roles.AddClaimAsync(role, new System.Security.Claims.Claim("permission", permission))).Succeeded);
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Dashboard test", IndustryId = industryId };
        Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role.Name!)).Succeeded);
        using var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    private Task<HttpResponseMessage> SendAsync(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/dashboard/stock-alerts");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }
}
