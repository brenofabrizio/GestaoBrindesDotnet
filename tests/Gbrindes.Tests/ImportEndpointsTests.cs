using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Gbrindes.Web.Data;
using Gbrindes.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gbrindes.Tests;

public sealed class ImportEndpointsTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Import_page_exposes_items_and_requests_workflows_like_the_legacy_screen()
    {
        using var response = await _client.GetAsync("/import/items");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Importar dados", html);
        Assert.Contains("value=\"items\"", html);
        Assert.Contains("value=\"requests\"", html);
        Assert.Contains("data-import-type", html);
        Assert.Contains("/api/v1/import/template/requests", html);
        Assert.Contains("/api/v1/import/requests/preview", html);
        Assert.Contains("/api/v1/import/requests/apply", html);
    }

    [Fact]
    public async Task Import_routes_reject_users_without_import_permission()
    {
        var token = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", []);
        var routes = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, "/api/v1/import/template/items"),
            (HttpMethod.Get, "/api/v1/import/template/requests"),
            (HttpMethod.Post, "/api/v1/import/items/preview"),
            (HttpMethod.Post, "/api/v1/import/items/apply"),
            (HttpMethod.Post, "/api/v1/import/requests/preview"),
            (HttpMethod.Post, "/api/import/items/commit"),
            (HttpMethod.Post, "/api/import/requests/commit")
        };

        foreach (var (method, path) in routes)
        {
            using var request = new HttpRequestMessage(method, path);
            if (method == HttpMethod.Post)
                request.Content = new MultipartFormDataContent();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path}: {response.StatusCode}");
        }
    }

    [Fact]
    public async Task Items_preview_validates_each_xlsx_row_without_persisting_items()
    {
        var categoryId = Guid.NewGuid();
        var categoryName = $"Import-{Guid.NewGuid():N}";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Categories.Add(new Category { Id = categoryId, Name = categoryName, NameKey = categoryName.ToUpperInvariant() });
            await database.SaveChangesAsync();
        }
        var token = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", ["import.run"]);
        int itemCountBefore;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            itemCountBefore = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CatalogItems.Count();
        }
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(CreateWorkbook(
            ["codigo", "nome", "categoria", "local", "fornecedor", "valor_unitario", "estoque_minimo", "quantidade_inicial"],
            ["IT-1", "Caneca", categoryName],
            ["IT-2", "Caderno", "Categoria ausente"],
            ["IT-3", "", categoryName],
            ["IT-4", "Caneca inválida", categoryName, "", "", "abc", "-1", "x"],
            ["!BAD", "Código inválido", categoryName]));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(file, "file", "items.xlsx");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/import/items/preview") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(5, json.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(4, json.RootElement.GetProperty("errors").GetInt32());
        var rows = json.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.True(rows[0].GetProperty("ok").GetBoolean());
        Assert.Equal(2, rows[0].GetProperty("line").GetInt32());
        Assert.Equal(categoryId, rows[0].GetProperty("payload").GetProperty("category_id").GetGuid());
        Assert.False(rows[1].GetProperty("ok").GetBoolean());
        Assert.Contains("Categoria não encontrada", rows[1].GetProperty("errors")[0].GetString());
        Assert.False(rows[2].GetProperty("ok").GetBoolean());
        Assert.False(rows[3].GetProperty("ok").GetBoolean());
        Assert.False(rows[4].GetProperty("ok").GetBoolean());
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(itemCountBefore, await verifyDatabase.CatalogItems.CountAsync());
    }

    [Fact]
    public async Task Items_preview_returns_validation_error_for_malformed_workbook_xml()
    {
        var token = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", ["import.run"]);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(CreateMalformedWorkbook()), "file", "broken.xlsx");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/import/items/preview") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Items_apply_creates_only_valid_rows_and_replaying_same_codes_does_not_duplicate()
    {
        var categoryId = Guid.NewGuid();
        var categoryName = $"Apply-{Guid.NewGuid():N}";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Categories.Add(new Category { Id = categoryId, Name = categoryName, NameKey = categoryName.ToUpperInvariant() });
            await database.SaveChangesAsync();
        }
        var token = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", ["import.run"]);
        var code = $"IM-{Guid.NewGuid():N}"[..16];

        async Task<HttpResponseMessage> ApplyAsync(string notes = "")
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(CreateWorkbook(
                ["codigo", "nome", "categoria", "observacoes"],
                [code, "Caneca importada", categoryName, notes],
                [$"BAD-{Guid.NewGuid():N}"[..16], "", categoryName])), "file", "items.xlsx");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/import/items/apply") { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await _client.SendAsync(request);
        }

        using var first = await ApplyAsync();
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.True(first.StatusCode == HttpStatusCode.OK, firstBody);
        using var firstJson = JsonDocument.Parse(firstBody);
        Assert.Equal(1, firstJson.RootElement.GetProperty("created").GetInt32());
        Assert.Equal(1, firstJson.RootElement.GetProperty("skipped").GetInt32());
        using var second = await ApplyAsync();
        var secondBody = await second.Content.ReadAsStringAsync();
        Assert.True(second.StatusCode == HttpStatusCode.OK, secondBody);
        using var secondJson = JsonDocument.Parse(secondBody);
        Assert.Equal(0, secondJson.RootElement.GetProperty("created").GetInt32());
        Assert.Equal(2, secondJson.RootElement.GetProperty("skipped").GetInt32());
        using var changedReplay = await ApplyAsync("alteração divergente");
        Assert.Equal(HttpStatusCode.Conflict, changedReplay.StatusCode);
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await verifyDatabase.CatalogItems.CountAsync(item => item.Code == code.ToUpperInvariant()));
    }

    [Fact]
    public async Task Items_apply_rejects_conflicting_rows_in_same_file_without_partial_write()
    {
        var categoryName = $"Conflict-{Guid.NewGuid():N}";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var categoryId = Guid.NewGuid();
            database.Categories.Add(new Category { Id = categoryId, Name = categoryName, NameKey = categoryName.ToUpperInvariant() });
            await database.SaveChangesAsync();
        }
        var token = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", ["import.run"]);
        var code = $"IM-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(CreateWorkbook(
            ["codigo", "nome", "categoria"],
            [code, "Primeiro nome", categoryName],
            [code.ToLowerInvariant(), "Outro nome", categoryName])), "file", "items.xlsx");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/import/items/apply") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var databaseAfter = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(0, await databaseAfter.CatalogItems.CountAsync(item => item.Code == code));
        Assert.Equal(0, await databaseAfter.AuditLogs.CountAsync(log => log.EntityType == "item" && log.EntityLabel!.StartsWith(code)));
    }

    [Fact]
    public async Task Items_apply_records_initial_stock_only_with_stock_entry_permission()
    {
        var categoryId = Guid.NewGuid();
        var categoryName = $"Initial-{Guid.NewGuid():N}";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Categories.Add(new Category { Id = categoryId, Name = categoryName, NameKey = categoryName.ToUpperInvariant() });
            await database.SaveChangesAsync();
        }
        var code = $"IN-{Guid.NewGuid():N}"[..16].ToUpperInvariant();

        async Task<HttpResponseMessage> ApplyAsync(string token)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(CreateWorkbook(
                ["codigo", "nome", "categoria", "quantidade_inicial"],
                [code, "Item com saldo inicial", categoryName, "3"])), "file", "items.xlsx");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/import/items/apply") { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await _client.SendAsync(request);
        }

        var importOnlyToken = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", ["import.run"]);
        using var denied = await ApplyAsync(importOnlyToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var stockToken = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", ["import.run", "stock.entry"]);
        using var applied = await ApplyAsync(stockToken);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var databaseAfter = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var item = await databaseAfter.CatalogItems.SingleAsync(row => row.Code == code);
        Assert.Equal(3, await databaseAfter.Stocks.Where(stock => stock.ItemId == item.Id).Select(stock => stock.OnHand).SingleAsync());
        Assert.Equal("entrada", await databaseAfter.StockMovements.Where(movement => movement.ItemId == item.Id).Select(movement => movement.Type).SingleAsync());
        Assert.Equal(3, await databaseAfter.StockMovements.Where(movement => movement.ItemId == item.Id).Select(movement => movement.Quantity).SingleAsync());
    }

    [Fact]
    public async Task Legacy_items_commit_route_preserves_ok_data_envelope()
    {
        var categoryId = Guid.NewGuid();
        var categoryName = $"LegacyImport-{Guid.NewGuid():N}";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Categories.Add(new Category { Id = categoryId, Name = categoryName, NameKey = categoryName.ToUpperInvariant() });
            await database.SaveChangesAsync();
        }
        var token = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", ["import.run"]);
        var code = $"LG-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(CreateWorkbook(
            ["codigo", "nome", "categoria"], [code, "Importação contrato PHP", categoryName])), "file", "items.xlsx");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/import/items/commit") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, json.RootElement.GetProperty("data").GetProperty("created").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("data").GetProperty("skipped").GetInt32());
    }


    [Fact]
    public async Task Requests_preview_resolves_items_and_marks_invalid_quantity_without_persisting_requests()
    {
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var categoryName = $"RequestImport-{Guid.NewGuid():N}";

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Categories.Add(new Category { Id = categoryId, Name = categoryName, NameKey = categoryName.ToUpperInvariant() });
            database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"RI-{Guid.NewGuid():N}"[..16], Name = "Item para solicitação", CategoryId = categoryId });
            await database.SaveChangesAsync();
        }
        var token = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", ["import.run"]);
        int requestCountBefore;
        await using (var scope = factory.Services.CreateAsyncScope())
            requestCountBefore = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Requests.Count();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(CreateWorkbook(
            ["brinde", "quantidade", "finalidade", "data_necessaria"],
            ["Item para solicitação", "3", "Evento", "09/11/2026"],
            ["Item inexistente", "0", "", ""])), "file", "requests.xlsx");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/import/requests/preview") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, json.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("report").GetProperty("valid").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("report").GetProperty("invalid").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("report").GetProperty("pending").GetInt32());
        Assert.True(json.RootElement.GetProperty("errors").GetInt32() == 1, json.RootElement.GetProperty("rows").ToString());
        var rows = json.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.True(rows[0].GetProperty("ok").GetBoolean());
        Assert.Equal(itemId, rows[0].GetProperty("payload").GetProperty("items")[0].GetProperty("item_id").GetGuid());
        Assert.Equal(3, rows[0].GetProperty("payload").GetProperty("items")[0].GetProperty("qty_requested").GetInt32());
        Assert.Equal("2026-11-09", rows[0].GetProperty("payload").GetProperty("needed_date").GetString());
        Assert.False(rows[1].GetProperty("ok").GetBoolean());
        await using var verifyScope = factory.Services.CreateAsyncScope();
        Assert.Equal(requestCountBefore, await verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Requests.CountAsync());
    }

    [Fact]
    public async Task Requests_preview_marks_impossible_iso_dates_invalid_instead_of_silently_clearing_them()
    {
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var categoryName = $"InvalidDateImport-{Guid.NewGuid():N}";
        var itemName = $"Item para data inválida {Guid.NewGuid():N}";

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Categories.Add(new Category { Id = categoryId, Name = categoryName, NameKey = categoryName.ToUpperInvariant() });
            database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"ID-{Guid.NewGuid():N}"[..16], Name = itemName, CategoryId = categoryId });
            await database.SaveChangesAsync();
        }
        var token = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", ["import.run"]);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(CreateWorkbook(
            ["brinde", "quantidade", "data_necessaria"],
            [itemName, "1", "2026-02-30"])), "file", "requests.xlsx");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/import/requests/preview") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = json.RootElement.GetProperty("rows")[0];
        Assert.False(row.GetProperty("ok").GetBoolean());
        Assert.Contains("Data necessária inválida", row.GetProperty("errors").EnumerateArray().Select(error => error.GetString()));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("payload").GetProperty("needed_date").ValueKind);
    }

    [Fact]
    public async Task Requests_apply_submits_valid_rows_and_reserves_stock_without_requests_create_permission()
    {
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var categoryName = $"ApplyRequest-{Guid.NewGuid():N}";
        var itemName = $"Importable request item {Guid.NewGuid():N}";
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var purpose = $"Solicitação importada {Guid.NewGuid():N}";
        var recipient = $"Destinatário {Guid.NewGuid():N}";
        var purchaseTicket = $"OC-{Guid.NewGuid():N}"[..24];
        var notes = $"Observações {Guid.NewGuid():N}";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Categories.Add(new Category { Id = categoryId, Name = categoryName, NameKey = categoryName.ToUpperInvariant() });
            database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"RQ-{Guid.NewGuid():N}"[..16], Name = itemName, CategoryId = categoryId, UnitValue = 2m });
            database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 5, Reserved = 0, UpdatedAt = DateTimeOffset.UtcNow });
            await database.SaveChangesAsync();
        }
        var token = await CreateUserAndLoginAsync(email, ["import.run"]);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(CreateWorkbook(
            ["brinde", "quantidade", "finalidade", "destinatario", "chamado_compra", "data_necessaria", "observacoes"],
            [itemName, "3", purpose, recipient, purchaseTicket, "2026-11-20", notes],
            ["Brinde inexistente", "4", "Linha inválida", "", "", "", ""])), "file", "requests.xlsx");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/import/requests/apply") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(1, json.RootElement.GetProperty("created").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("skipped").GetInt32());

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var requester = await verifyDatabase.Users.SingleAsync(user => user.Email == email);
        var imported = await verifyDatabase.Requests.Include(row => row.Items).SingleAsync(row => row.Purpose == purpose);
        Assert.Equal("aprovada", imported.Status);
        Assert.Equal("interna", imported.Flow);
        Assert.Equal(requester.Id, imported.RequesterId);
        Assert.Equal(new DateOnly(2026, 11, 20), imported.NeededDate);
        Assert.Equal(recipient, imported.Recipient);
        Assert.Equal(purchaseTicket, imported.PurchaseTicketNo);
        Assert.Equal(notes, imported.Notes);
        Assert.Equal(6m, imported.TotalValue);
        Assert.Equal(3, imported.Items.Single().QuantityApproved);
        Assert.Equal(3, imported.Items.Single().QuantityReserved);
        Assert.Equal(3, await verifyDatabase.Stocks.Where(stock => stock.ItemId == itemId).Select(stock => stock.Reserved).SingleAsync());
        Assert.Contains(await verifyDatabase.RequestStatusHistory.Where(history => history.RequestId == imported.Id)
            .Select(history => history.ToStatus).ToArrayAsync(), status => status == "aprovada");
        Assert.Equal(2, await verifyDatabase.AuditLogs.CountAsync(log => log.EntityType == "request" && log.EntityId == imported.Id));

        var legacyPurpose = $"Solicitação PHP importada {Guid.NewGuid():N}";
        using var legacyForm = new MultipartFormDataContent();
        legacyForm.Add(new ByteArrayContent(CreateWorkbook(
            ["brinde", "quantidade", "finalidade"], [itemName, "1", legacyPurpose])), "file", "requests.xlsx");
        using var legacyRequest = new HttpRequestMessage(HttpMethod.Post, "/api/import/requests/commit") { Content = legacyForm };
        legacyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var legacyResponse = await _client.SendAsync(legacyRequest);
        Assert.Equal(HttpStatusCode.OK, legacyResponse.StatusCode);
        using var legacyJson = JsonDocument.Parse(await legacyResponse.Content.ReadAsStringAsync());
        Assert.True(legacyJson.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, legacyJson.RootElement.GetProperty("data").GetProperty("created").GetInt32());
        Assert.Equal(0, legacyJson.RootElement.GetProperty("data").GetProperty("skipped").GetInt32());
        Assert.True(await verifyDatabase.Requests.AnyAsync(row => row.Purpose == legacyPurpose && row.Status == "aprovada"));
    }

    [Fact]
    public async Task Requests_apply_replays_are_idempotent_and_changed_rows_conflict()
    {
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var categoryName = $"RequestReplay-{Guid.NewGuid():N}";
        var itemName = $"Replay item {Guid.NewGuid():N}";
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var purpose = $"Replay purpose {Guid.NewGuid():N}";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Categories.Add(new Category { Id = categoryId, Name = categoryName, NameKey = categoryName.ToUpperInvariant() });
            database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"RP-{Guid.NewGuid():N}"[..16], Name = itemName, CategoryId = categoryId, UnitValue = 2m });
            database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 10, Reserved = 0, UpdatedAt = DateTimeOffset.UtcNow });
            await database.SaveChangesAsync();
        }
        var token = await CreateUserAndLoginAsync(email, ["import.run"]);

        async Task<HttpResponseMessage> ApplyAsync(string notes)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(CreateWorkbook(
                ["brinde", "quantidade", "finalidade", "observacoes"],
                [itemName, "2", purpose, notes])), "file", "requests.xlsx");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/import/requests/apply") { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await _client.SendAsync(request);
        }

        using var first = await ApplyAsync("original");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.Equal(1, firstJson.RootElement.GetProperty("created").GetInt32());

        using var replay = await ApplyAsync("original");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(0, replayJson.RootElement.GetProperty("created").GetInt32());
        Assert.Equal(1, replayJson.RootElement.GetProperty("skipped").GetInt32());

        using var changed = await ApplyAsync("alteração");
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var databaseAfter = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await databaseAfter.Requests.CountAsync(row => row.Purpose == purpose));
        Assert.Equal(2, await databaseAfter.Requests.Include(row => row.Items).Where(row => row.Purpose == purpose).SelectMany(row => row.Items).Select(row => row.QuantityRequested).SingleAsync());
    }

    [Fact]
    public async Task Requests_apply_rolls_back_the_entire_file_when_a_later_valid_row_fails()
    {
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var categoryName = $"RequestAtomic-{Guid.NewGuid():N}";
        var itemName = $"Atomic item {Guid.NewGuid():N}";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Categories.Add(new Category { Id = categoryId, Name = categoryName, NameKey = categoryName.ToUpperInvariant() });
            database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"AT-{Guid.NewGuid():N}"[..16], Name = itemName, CategoryId = categoryId, UnitValue = 1m });
            await database.SaveChangesAsync();
        }
        var token = await CreateUserAndLoginAsync($"{Guid.NewGuid():N}@example.invalid", ["import.run"]);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(CreateWorkbook(
            ["brinde", "quantidade", "finalidade", "observacoes"],
            [itemName, "1", "Atomic same identity", "first"],
            [itemName, "1", "Atomic same identity", "second"])), "file", "requests.xlsx");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/import/requests/apply") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var databaseAfter = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var expectedPersisted = databaseAfter.Database.IsRelational() ? 0 : 1;
        Assert.Equal(expectedPersisted, await databaseAfter.Requests.CountAsync(row => row.Purpose.StartsWith("Atomic")));
    }

    [Fact]
    public async Task Requests_apply_routes_rule_matched_rows_to_pending_approval_without_reserving_stock()
    {
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var categoryName = $"ApprovalImport-{Guid.NewGuid():N}";
        var itemName = $"Approval import item {Guid.NewGuid():N}";
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var approverEmail = $"{Guid.NewGuid():N}@example.invalid";
        Guid approverId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            database.Categories.Add(new Category { Id = categoryId, Name = categoryName, NameKey = categoryName.ToUpperInvariant() });
            database.CatalogItems.Add(new CatalogItem { Id = itemId, Code = $"AR-{Guid.NewGuid():N}"[..16], Name = itemName, CategoryId = categoryId, UnitValue = 2m });
            database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 5, Reserved = 0, UpdatedAt = DateTimeOffset.UtcNow });
            var approver = new ApplicationUser { UserName = approverEmail, Email = approverEmail, EmailConfirmed = true, FullName = "Import approver" };
            Assert.True((await services.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(approver, "CorrectPassword1")).Succeeded);
            approverId = approver.Id;
            database.ApprovalRules.Add(new ApprovalRule
            {
                Name = $"Import threshold {Guid.NewGuid():N}",
                Criterion = "quantidade",
                Operator = ">=",
                Value = "3",
                ApproverUserId = approverId,
                Priority = 1,
                Active = true
            });
            await database.SaveChangesAsync();
        }

        var token = await CreateUserAndLoginAsync(email, ["import.run"]);
        var purpose = $"Solicitação sob aprovação {Guid.NewGuid():N}";
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(CreateWorkbook(
            ["brinde", "quantidade", "finalidade"], [itemName, "3", purpose])), "file", "requests.xlsx");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/import/requests/apply") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var imported = await verifyDatabase.Requests.SingleAsync(row => row.Purpose == purpose);
        Assert.Equal("aguardando_aprovacao", imported.Status);
        Assert.Equal("pendente", await verifyDatabase.RequestApprovals.Where(approval => approval.RequestId == imported.Id).Select(approval => approval.Decision).SingleAsync());
        Assert.Equal(0, await verifyDatabase.Stocks.Where(stock => stock.ItemId == itemId).Select(stock => stock.Reserved).SingleAsync());
        Assert.Contains(await verifyDatabase.Notifications.Where(notification => notification.RelatedId == imported.Id)
            .Select(notification => notification.UserId).ToArrayAsync(), userId => userId == approverId);
    }

    private async Task<string> CreateUserAndLoginAsync(string email, string[] permissions)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = new ApplicationRole($"role-{Guid.NewGuid():N}");
        Assert.True((await roles.CreateAsync(role)).Succeeded);
        foreach (var permission in permissions)
            Assert.True((await roles.AddClaimAsync(role, new System.Security.Claims.Claim("permission", permission))).Succeeded);
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Import test" };
        Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role.Name!)).Succeeded);
        using var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    internal static byte[] CreateWorkbook(params string[][] rows)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var workbookWriter = new StreamWriter(archive.CreateEntry("xl/workbook.xml").Open(), new UTF8Encoding(false)))
                workbookWriter.Write("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Import\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            using (var relationshipWriter = new StreamWriter(archive.CreateEntry("xl/_rels/workbook.xml.rels").Open(), new UTF8Encoding(false)))
                relationshipWriter.Write("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\"/></Relationships>");
            var sheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
            for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                sheet.Append($"<row r=\"{rowIndex + 1}\">");
                for (var colIndex = 0; colIndex < rows[rowIndex].Length; colIndex++)
                {
                    var col = (char)('A' + colIndex);
                    var value = System.Security.SecurityElement.Escape(rows[rowIndex][colIndex]);
                    sheet.Append($"<c r=\"{col}{rowIndex + 1}\" t=\"inlineStr\"><is><t>{value}</t></is></c>");
                }
                sheet.Append("</row>");
            }
            sheet.Append("</sheetData></worksheet>");
            using var writer = new StreamWriter(archive.CreateEntry("xl/worksheets/sheet1.xml").Open(), new UTF8Encoding(false));
            writer.Write(sheet.ToString());
        }
        return output.ToArray();
    }

    private static byte[] CreateMalformedWorkbook()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var workbookWriter = new StreamWriter(archive.CreateEntry("xl/workbook.xml").Open(), new UTF8Encoding(false)))
                workbookWriter.Write("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Import\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            using (var relationshipWriter = new StreamWriter(archive.CreateEntry("xl/_rels/workbook.xml.rels").Open(), new UTF8Encoding(false)))
                relationshipWriter.Write("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\"/></Relationships>");
            using var sheetWriter = new StreamWriter(archive.CreateEntry("xl/worksheets/sheet1.xml").Open(), new UTF8Encoding(false));
            sheetWriter.Write("<worksheet><broken>");
        }
        return output.ToArray();
    }
}
