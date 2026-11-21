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

public sealed class InventoryApiTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Stock_movement_list_includes_legacy_item_and_actor_display_fields()
    {
        var (token, itemId) = await CreateItemAsync();
        using var entry = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", token,
            new { quantity = 3, idempotency_key = $"movement-list-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);

        using var response = await SendAsync(HttpMethod.Get, $"/api/v1/stock/movements?item_id={itemId}", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var movement = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());

        Assert.Equal("Entrada", movement.GetProperty("type_label").GetString());
        Assert.Equal(3, movement.GetProperty("qty").GetInt32());
        Assert.Equal(itemId, movement.GetProperty("item").GetProperty("id").GetGuid());
        Assert.StartsWith("IT-", movement.GetProperty("item").GetProperty("code").GetString());
        Assert.Equal("Caneca", movement.GetProperty("item").GetProperty("name").GetString());
        Assert.NotEqual(Guid.Empty, movement.GetProperty("user").GetProperty("id").GetGuid());
        Assert.Equal("Inventory test", movement.GetProperty("user").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Industry_stock_report_aggregates_signed_movements_and_forces_user_industry_scope()
    {
        var industryId = Guid.NewGuid();
        var otherIndustryId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var deletedItemId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Industries.AddRange(
                new Industry { Id = industryId, Name = $"Industry A {industryId:N}", NameKey = industryId.ToString("N") },
                new Industry { Id = otherIndustryId, Name = $"Industry B {otherIndustryId:N}", NameKey = otherIndustryId.ToString("N") });
            database.Categories.Add(new Category { Id = categoryId, Name = $"Report {categoryId:N}", NameKey = categoryId.ToString("N") });
            database.CatalogItems.AddRange(
                new CatalogItem { Id = itemId, Code = $"RPT-{itemId:N}"[..12], Name = "Relatório item", CategoryId = categoryId },
                new CatalogItem { Id = deletedItemId, Code = $"RPD-{deletedItemId:N}"[..12], Name = "Item removido", CategoryId = categoryId, DeletedAt = DateTimeOffset.UtcNow });
            database.StockMovements.AddRange(
                new StockMovement { ItemId = itemId, IndustryId = industryId, Type = "entrada", Quantity = 10, BalanceAfter = 10 },
                new StockMovement { ItemId = itemId, IndustryId = industryId, Type = "saida", Quantity = -3, BalanceAfter = 7 },
                new StockMovement { ItemId = itemId, IndustryId = industryId, Type = "ajuste", Quantity = -2, BalanceAfter = 5 },
                new StockMovement { ItemId = itemId, IndustryId = industryId, Type = "estorno", Quantity = 1, BalanceAfter = 6 },
                new StockMovement { ItemId = itemId, IndustryId = industryId, Type = "transferencia", Quantity = 100, BalanceAfter = 106 },
                new StockMovement { ItemId = itemId, IndustryId = otherIndustryId, Type = "entrada", Quantity = 50, BalanceAfter = 50 },
                new StockMovement { ItemId = deletedItemId, IndustryId = industryId, Type = "entrada", Quantity = 9, BalanceAfter = 9 });
            await database.SaveChangesAsync();
        }

        var scopedToken = await CreateTokenAsync(["stock.view"], industryId);
        var allIndustriesToken = await CreateTokenAsync(["stock.view"]);
        var deniedToken = await CreateTokenAsync(["stock.entry"]);

        using var scopedResponse = await SendAsync(HttpMethod.Get, "/api/v1/stock/by-industry", scopedToken);
        Assert.Equal(HttpStatusCode.OK, scopedResponse.StatusCode);
        using var scopedJson = JsonDocument.Parse(await scopedResponse.Content.ReadAsStringAsync());
        var scopedRows = scopedJson.RootElement.GetProperty("data").EnumerateArray().ToArray();
        var scopedRow = Assert.Single(scopedRows);
        Assert.Equal(industryId, scopedRow.GetProperty("industry").GetProperty("id").GetGuid());
        Assert.Equal(10, scopedRow.GetProperty("received").GetInt32());
        Assert.Equal(3, scopedRow.GetProperty("withdrawn").GetInt32());
        Assert.Equal(6, scopedRow.GetProperty("saldo").GetInt32());
        Assert.Equal("Relatório item", scopedRow.GetProperty("item").GetProperty("name").GetString());

        using var allResponse = await SendAsync(HttpMethod.Get, "/api/v1/stock/by-industry", allIndustriesToken);
        Assert.Equal(HttpStatusCode.OK, allResponse.StatusCode);
        using var allJson = JsonDocument.Parse(await allResponse.Content.ReadAsStringAsync());
        var allRows = allJson.RootElement.GetProperty("data").EnumerateArray().ToArray();
        var industryAIndex = Array.FindIndex(allRows, row => row.GetProperty("industry").GetProperty("id").GetGuid() == industryId);
        var industryBIndex = Array.FindIndex(allRows, row => row.GetProperty("industry").GetProperty("id").GetGuid() == otherIndustryId);
        Assert.True(industryAIndex >= 0 && industryBIndex > industryAIndex);
        var allKeys = allRows.Select(row => (
            row.GetProperty("industry").GetProperty("id").GetGuid(),
            row.GetProperty("item").GetProperty("id").GetGuid())).ToArray();

        using var invalidFilter = await SendAsync(HttpMethod.Get,
            "/api/v1/stock/by-industry?industry_id=invalid", allIndustriesToken);
        using var invalidJson = JsonDocument.Parse(await invalidFilter.Content.ReadAsStringAsync());
        var invalidRows = invalidJson.RootElement.GetProperty("data").EnumerateArray().ToArray();
        var invalidKeys = invalidRows.Select(row => (
            row.GetProperty("industry").GetProperty("id").GetGuid(),
            row.GetProperty("item").GetProperty("id").GetGuid())).ToArray();
        Assert.Equal(allKeys, invalidKeys);

        using var scopedOverride = await SendAsync(HttpMethod.Get,
            $"/api/v1/stock/by-industry?industry_id={otherIndustryId}", scopedToken);
        Assert.Equal(HttpStatusCode.OK, scopedOverride.StatusCode);
        using var scopedOverrideJson = JsonDocument.Parse(await scopedOverride.Content.ReadAsStringAsync());
        Assert.Equal(industryId, Assert.Single(scopedOverrideJson.RootElement.GetProperty("data").EnumerateArray())
            .GetProperty("industry").GetProperty("id").GetGuid());

        using var filteredResponse = await SendAsync(HttpMethod.Get,
            $"/api/v1/stock/by-industry?industry_id={otherIndustryId}", allIndustriesToken);
        Assert.Equal(HttpStatusCode.OK, filteredResponse.StatusCode);
        using var filteredJson = JsonDocument.Parse(await filteredResponse.Content.ReadAsStringAsync());
        var filteredRow = Assert.Single(filteredJson.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(otherIndustryId, filteredRow.GetProperty("industry").GetProperty("id").GetGuid());
        Assert.Equal(50, filteredRow.GetProperty("saldo").GetInt32());

        using var denied = await SendAsync(HttpMethod.Get, "/api/v1/stock/by-industry", deniedToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        await using var cleanupScope = factory.Services.CreateAsyncScope();
        var cleanup = cleanupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        cleanup.StockMovements.RemoveRange(cleanup.StockMovements.Where(movement => movement.ItemId == itemId || movement.ItemId == deletedItemId));
        cleanup.CatalogItems.RemoveRange(cleanup.CatalogItems.Where(item => item.Id == itemId || item.Id == deletedItemId));
        cleanup.Categories.RemoveRange(cleanup.Categories.Where(category => category.Id == categoryId));
        await cleanup.SaveChangesAsync();
    }

    [Fact]
    public async Task Transfer_moves_stock_between_locations_without_changing_aggregate_balance()
    {
        var industryId = Guid.NewGuid();
        var otherIndustryId = Guid.NewGuid();
        await using (var industryScope = factory.Services.CreateAsyncScope())
        {
            var database = industryScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Industries.AddRange(
                new Industry { Id = industryId, Name = $"Transfer {industryId:N}", NameKey = industryId.ToString("N") },
                new Industry { Id = otherIndustryId, Name = $"Other {otherIndustryId:N}", NameKey = otherIndustryId.ToString("N") });
            await database.SaveChangesAsync();
        }
        var token = await CreateTokenAsync(["items.view", "items.manage", "lookups.view", "lookups.manage", "stock.view", "stock.transfer"], industryId);
        var categoryResponse = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Transfer-{Guid.NewGuid():N}" });
        using var categoryJson = JsonDocument.Parse(await categoryResponse.Content.ReadAsStringAsync());
        var categoryId = categoryJson.RootElement.GetProperty("id").GetGuid();
        var itemResponse = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { code = $"TF-{Guid.NewGuid():N}"[..10], name = "Brinde transferência", categoryId });
        using var itemJson = JsonDocument.Parse(await itemResponse.Content.ReadAsStringAsync());
        var itemId = itemJson.RootElement.GetProperty("id").GetGuid();
        var otherCategoryId = Guid.NewGuid();
        var otherItemId = Guid.NewGuid();
        var fromId = Guid.NewGuid();
        var toId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Categories.Add(new Category { Id = otherCategoryId, Name = $"Outra categoria {otherCategoryId:N}", NameKey = otherCategoryId.ToString("N") });
            database.CatalogItems.Add(new CatalogItem { Id = otherItemId, Code = $"TO-{otherItemId:N}"[..12], Name = "Outro brinde", CategoryId = otherCategoryId });
            database.Locations.AddRange(
                new Location { Id = fromId, Name = $"Origem {fromId:N}", NameKey = fromId.ToString("N"), Kind = "cd" },
                new Location { Id = toId, Name = $"Destino {toId:N}", NameKey = toId.ToString("N"), Kind = "outro" });
            database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 10 });
            database.StockPositions.Add(new StockPosition { ItemId = itemId, LocationId = fromId, Quantity = 10 });
            database.StockMovements.Add(new StockMovement { ItemId = itemId, IndustryId = otherIndustryId, Type = "entrada", Quantity = 10, BalanceAfter = 10, LocationId = fromId });
            database.StockMovements.Add(new StockMovement { ItemId = itemId, IndustryId = industryId, Type = "entrada", Quantity = 10, BalanceAfter = 10, Purpose = "Campanha especial" });
            database.StockMovements.Add(new StockMovement { ItemId = otherItemId, IndustryId = industryId, Type = "entrada", Quantity = 1, BalanceAfter = 1 });
            await database.SaveChangesAsync();
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/transfers")
        {
            Content = JsonContent.Create(new { item_id = itemId, quantity = 4, industry_id = industryId, from_location_id = fromId, to_location_id = toId, notes = "Realocação" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", "transfer-test-001");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(2, data.GetProperty("movements").GetArrayLength());
        Assert.Equal(10, data.GetProperty("stock").GetProperty("on_hand").GetInt32());
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(6, (await verify.StockPositions.SingleAsync(position => position.ItemId == itemId && position.LocationId == fromId)).Quantity);
        Assert.Equal(4, (await verify.StockPositions.SingleAsync(position => position.ItemId == itemId && position.LocationId == toId)).Quantity);
        var audit = await verify.AuditLogs.SingleAsync(log => log.Action == "stock_transfer" && log.EntityId == itemId);
        Assert.Equal("item", audit.EntityType);
        Assert.NotNull(audit.UserId);
        using var before = JsonDocument.Parse(audit.BeforeJson!);
        using var after = JsonDocument.Parse(audit.AfterJson!);
        Assert.Equal(10, before.RootElement.GetProperty("on_hand").GetInt32());
        Assert.Equal(10, after.RootElement.GetProperty("on_hand").GetInt32());
        using var positionsResponse = await SendAsync(HttpMethod.Get, $"/api/v1/stock/positions?item_id={itemId}", token);
        Assert.Equal(HttpStatusCode.OK, positionsResponse.StatusCode);
        using var positionsJson = JsonDocument.Parse(await positionsResponse.Content.ReadAsStringAsync());
        var positions = positionsJson.RootElement.GetProperty("data");
        Assert.Equal(2, positions.GetArrayLength());
        Assert.Contains(positions.EnumerateArray(), position => position.GetProperty("location").GetProperty("id").GetGuid() == fromId && position.GetProperty("qty").GetInt32() == 6);
        Assert.Contains(positions.EnumerateArray(), position => position.GetProperty("location").GetProperty("id").GetGuid() == toId && position.GetProperty("qty").GetInt32() == 4);
        using var movementsResponse = await SendAsync(HttpMethod.Get, $"/api/v1/stock/movements?itemId={itemId}&type=transferencia", token);
        Assert.Equal(HttpStatusCode.OK, movementsResponse.StatusCode);
        using var movementsJson = JsonDocument.Parse(await movementsResponse.Content.ReadAsStringAsync());
        var transferMovements = movementsJson.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, transferMovements.Length);
        Assert.All(transferMovements, movement =>
        {
            Assert.Equal(fromId, movement.GetProperty("fromLocationId").GetGuid());
            Assert.Equal(toId, movement.GetProperty("toLocationId").GetGuid());
            Assert.Equal(industryId, movement.GetProperty("industryId").GetGuid());
        });
        Assert.Single(transferMovements.Select(movement => movement.GetProperty("transferId").GetGuid()).Distinct());
        using var scopedMovements = await SendAsync(HttpMethod.Get, $"/api/v1/stock/movements?itemId={itemId}", token);
        Assert.Equal(3, JsonDocument.Parse(await scopedMovements.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength());
        using var snakeCaseItemMovements = await SendAsync(HttpMethod.Get, $"/api/v1/stock/movements?item_id={itemId}", token);
        Assert.Equal(3, JsonDocument.Parse(await snakeCaseItemMovements.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength());
        using var categoryMovements = await SendAsync(HttpMethod.Get, $"/api/v1/stock/movements?category_id={categoryId}", token);
        Assert.Equal(3, JsonDocument.Parse(await categoryMovements.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength());
        using var otherCategoryMovements = await SendAsync(HttpMethod.Get, $"/api/v1/stock/movements?category_id={otherCategoryId}", token);
        Assert.Single(JsonDocument.Parse(await otherCategoryMovements.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray());
        using var searchedMovements = await SendAsync(HttpMethod.Get, $"/api/v1/stock/movements?itemId={itemId}&q=CAMPANHA", token);
        using var searchedMovementsJson = JsonDocument.Parse(await searchedMovements.Content.ReadAsStringAsync());
        Assert.Single(searchedMovementsJson.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("Campanha especial", searchedMovementsJson.RootElement.GetProperty("items")[0].GetProperty("purpose").GetString());
        using var actorMovements = await SendAsync(HttpMethod.Get,
            $"/api/v1/stock/movements?itemId={itemId}&user_id={audit.UserId}", token);
        Assert.Equal(2, JsonDocument.Parse(await actorMovements.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength());
        using var otherIndustryMovements = await SendAsync(HttpMethod.Get,
            $"/api/v1/stock/movements?itemId={itemId}&industry_id={otherIndustryId}", token);
        Assert.Empty(JsonDocument.Parse(await otherIndustryMovements.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray());
        using var pastMovements = await SendAsync(HttpMethod.Get,
            $"/api/v1/stock/movements?itemId={itemId}&to=2000-01-01", token);
        Assert.Empty(JsonDocument.Parse(await pastMovements.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray());
        var futureDate = DateTimeOffset.UtcNow.AddYears(20).ToString("yyyy-MM-dd");
        using var futureMovements = await SendAsync(HttpMethod.Get,
            $"/api/v1/stock/movements?itemId={itemId}&from={futureDate}", token);
        Assert.Empty(JsonDocument.Parse(await futureMovements.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray());
        using var sameLocationRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/transfers")
        {
            Content = JsonContent.Create(new { item_id = itemId, quantity = 1, industry_id = industryId, to_location_id = fromId })
        };
        sameLocationRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        sameLocationRequest.Headers.Add("Idempotency-Key", "transfer-same-location");
        using var sameDefaultLocation = await _client.SendAsync(sameLocationRequest);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, sameDefaultLocation.StatusCode);
    }

    [Fact]
    public async Task Transfer_to_open_event_moves_stock_increases_allocation_and_reserves_stock()
    {
        var (token, itemId) = await CreateItemAsync();
        token = await CreateTokenAsync(["stock.transfer"]);
        var industryId = Guid.NewGuid();
        var fromId = Guid.NewGuid();
        var eventLocationId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var transferKey = $"event-transfer-{Guid.NewGuid():N}";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Industries.Add(new Industry { Id = industryId, Name = $"Event transfer {industryId:N}", NameKey = industryId.ToString("N") });
            database.Locations.AddRange(
                new Location { Id = fromId, Name = $"CD {fromId:N}", NameKey = fromId.ToString("N"), Kind = "cd" },
                new Location { Id = eventLocationId, Name = $"Event {eventLocationId:N}", NameKey = eventLocationId.ToString("N"), Kind = "evento" });
            database.Events.Add(new Event { Id = eventId, Name = "Open event transfer", LocationId = eventLocationId, Status = "aberto" });
            database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 8 });
            database.StockPositions.Add(new StockPosition { ItemId = itemId, LocationId = fromId, Quantity = 8 });
            await database.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/transfers")
        {
            Content = JsonContent.Create(new { item_id = itemId, quantity = 3, industry_id = industryId, from_location_id = fromId, event_id = eventId })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", transferKey);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var responseJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(eventId, responseJson.RootElement.GetProperty("data").GetProperty("event_id").GetGuid());

        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/transfers")
        {
            Content = JsonContent.Create(new { item_id = itemId, quantity = 3, industry_id = industryId, from_location_id = fromId, event_id = eventId })
        };
        replayRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        replayRequest.Headers.Add("Idempotency-Key", transferKey);
        using var replay = await _client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        using var changedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/transfers")
        {
            Content = JsonContent.Create(new { item_id = itemId, quantity = 2, industry_id = industryId, from_location_id = fromId, event_id = eventId })
        };
        changedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        changedRequest.Headers.Add("Idempotency-Key", transferKey);
        using var changedReplay = await _client.SendAsync(changedRequest);
        Assert.Equal(HttpStatusCode.Conflict, changedReplay.StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(5, (await verify.StockPositions.SingleAsync(row => row.ItemId == itemId && row.LocationId == fromId)).Quantity);
        Assert.Equal(3, (await verify.StockPositions.SingleAsync(row => row.ItemId == itemId && row.LocationId == eventLocationId)).Quantity);
        var allocation = await verify.EventAllocations.SingleAsync(row => row.EventId == eventId && row.ItemId == itemId && row.IndustryId == industryId);
        Assert.Equal(3, allocation.QuantityAllocated);
        Assert.Equal(3, (await verify.Stocks.SingleAsync(row => row.ItemId == itemId)).Reserved);
        var movements = await verify.StockMovements.Where(row => row.TransferId != null && row.ItemId == itemId).ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.Contains(movements, row => row.Quantity == -3 && row.FromLocationId == fromId && row.ToLocationId == eventLocationId);
        Assert.Contains(movements, row => row.Quantity == 3 && row.FromLocationId == fromId && row.ToLocationId == eventLocationId);
        Assert.Single(await verify.AuditLogs.Where(row => row.Action == "stock_transfer" && row.EntityId == itemId).ToListAsync());
    }

    [Fact]
    public async Task Php_transfer_alias_accepts_stock_exit_without_broadening_versioned_transfer_policy()
    {
        var (_, itemId) = await CreateItemAsync();
        var token = await CreateTokenAsync(["stock.exit"]);
        var industryId = Guid.NewGuid();
        var fromId = Guid.NewGuid();
        var toId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Industries.Add(new Industry { Id = industryId, Name = $"PHP transfer {industryId:N}", NameKey = industryId.ToString("N") });
            database.Locations.AddRange(
                new Location { Id = fromId, Name = $"PHP from {fromId:N}", NameKey = fromId.ToString("N"), Kind = "cd" },
                new Location { Id = toId, Name = $"PHP to {toId:N}", NameKey = toId.ToString("N"), Kind = "outro" });
            database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 4 });
            database.StockPositions.Add(new StockPosition { ItemId = itemId, LocationId = fromId, Quantity = 4 });
            await database.SaveChangesAsync();
        }

        using var deniedVersioned = await SendAsync(HttpMethod.Post, "/api/v1/stock/transfers", token,
            new { item_id = itemId, quantity = 2, industry_id = industryId, from_location_id = fromId, to_location_id = toId });
        Assert.Equal(HttpStatusCode.Forbidden, deniedVersioned.StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/stock/transfers")
        {
            Content = JsonContent.Create(new { item_id = itemId, quantity = 2, industry_id = industryId, from_location_id = fromId, to_location_id = toId })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", $"php-transfer-{Guid.NewGuid():N}");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(2, json.RootElement.GetProperty("data").GetProperty("movements").GetArrayLength());
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(2, (await verify.StockPositions.SingleAsync(value => value.ItemId == itemId && value.LocationId == fromId)).Quantity);
        Assert.Equal(2, (await verify.StockPositions.SingleAsync(value => value.ItemId == itemId && value.LocationId == toId)).Quantity);
    }

    [Fact]
    public async Task Entry_is_persisted_and_identical_replay_returns_same_movement()
    {
        var (token, itemId) = await CreateItemAsync();
        var first = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", token, new { quantity = 9, idempotency_key = "entry-1" });
        var replay = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", token, new { quantity = 9, idempotency_key = "entry-1" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        using var a = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var b = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(a.RootElement.GetProperty("id").GetGuid(), b.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(9, b.RootElement.GetProperty("balanceAfter").GetInt32());
        using var list = await SendAsync(HttpMethod.Get, $"/api/v1/stock/movements?itemId={itemId}&page=1&pageSize=10", token);
        Assert.Equal(1, JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Entry_persists_legacy_history_metadata_and_rejects_changed_replay()
    {
        var (token, itemId) = await CreateItemAsync();
        var body = new
        {
            quantity = 2,
            purpose = "Kit de evento",
            recipient = "Equipe comercial",
            purchase_ticket_no = "OC-2026-17",
            idempotency_key = "entry-meta-001"
        };
        using var created = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", token, body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var createdId = createdJson.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("Kit de evento", createdJson.RootElement.GetProperty("purpose").GetString());
        Assert.Equal("Equipe comercial", createdJson.RootElement.GetProperty("recipient").GetString());
        Assert.Equal("OC-2026-17", createdJson.RootElement.GetProperty("purchaseTicketNo").GetString());

        using var replay = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", token, body);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(createdId, JsonDocument.Parse(await replay.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid());

        using var changedPayload = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", token, new
        {
            quantity = 2,
            purpose = "Purpose alterado",
            recipient = "Equipe comercial",
            purchase_ticket_no = "OC-2026-17",
            idempotency_key = "entry-meta-001"
        });
        Assert.Equal(HttpStatusCode.Conflict, changedPayload.StatusCode);

        using var list = await SendAsync(HttpMethod.Get, $"/api/v1/stock/movements?itemId={itemId}", token);
        using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Equal("OC-2026-17", listJson.RootElement.GetProperty("items")[0].GetProperty("purchaseTicketNo").GetString());
    }

    [Fact]
    public async Task Exit_authorization_reserves_stock_and_returns_a_pending_order()
    {
        var (token, itemId) = await CreateItemAsync();
        using var entry = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", token,
            new { quantity = 5, idempotency_key = "exit-order-entry-01" });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/exits")
        {
            Content = JsonContent.Create(new
            {
                item_id = itemId,
                quantity = 2,
                purpose = "Evento com cliente",
                recipient = "Cliente XPTO"
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", "exit-order-create-001");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var order = json.RootElement.GetProperty("data").GetProperty("order");
        Assert.Equal("autorizada", order.GetProperty("status").GetString());
        Assert.StartsWith($"SAI-{DateTime.UtcNow.Year}-", order.GetProperty("code").GetString());
        Assert.Equal("Evento com cliente", order.GetProperty("purpose").GetString());
        Assert.Equal("Cliente XPTO", order.GetProperty("recipient").GetString());
        var stock = json.RootElement.GetProperty("data").GetProperty("stock");
        Assert.Equal(5, stock.GetProperty("onHand").GetInt32());
        Assert.Equal(2, stock.GetProperty("reserved").GetInt32());
        Assert.Equal(3, stock.GetProperty("available").GetInt32());

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var savedStock = await database.Stocks.SingleAsync(row => row.ItemId == itemId);
        Assert.Equal(5, savedStock.OnHand);
        Assert.Equal(2, savedStock.Reserved);
    }

    [Fact]
    public async Task Cd_operations_role_cannot_create_an_authorized_exit_or_reserve_stock()
    {
        var (entryToken, itemId) = await CreateItemAsync();
        using var entry = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", entryToken,
            new { quantity = 5, idempotency_key = $"cd-exit-entry-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);

        var roleName = $"CD / Estoque {Guid.NewGuid():N}";
        var email = $"{Guid.NewGuid():N}@example.invalid";
        Guid roleId = Guid.Empty;
        Guid userId = Guid.Empty;
        string? cdToken = null;
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                roleId = role.Id;
                Assert.True((await roles.AddClaimAsync(role, new System.Security.Claims.Claim("permission", "stock.exit"))).Succeeded);

                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Operador CD" };
                Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
                userId = user.Id;
                Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
            }

            using var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            cdToken = loginJson.RootElement.GetProperty("accessToken").GetString();
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/exits")
            {
                Content = JsonContent.Create(new { item_id = itemId, quantity = 2, purpose = "Solicitação para negar" })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cdToken);
            request.Headers.Add("Idempotency-Key", $"cd-exit-deny-{Guid.NewGuid():N}");
            using var denied = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

            await using var verifyScope = factory.Services.CreateAsyncScope();
            var database = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(5, (await database.Stocks.SingleAsync(stock => stock.ItemId == itemId)).OnHand);
            Assert.Equal(0, (await database.Stocks.SingleAsync(stock => stock.ItemId == itemId)).Reserved);
            Assert.Empty(await database.StockExitOrders.Where(order => order.ItemId == itemId).ToListAsync());
        }
        finally
        {
            await using var cleanupScope = factory.Services.CreateAsyncScope();
            var services = cleanupScope.ServiceProvider;
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = userId == Guid.Empty ? null : await users.FindByIdAsync(userId.ToString());
            if (user is not null) Assert.True((await users.DeleteAsync(user)).Succeeded);
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = roleId == Guid.Empty ? null : await roles.FindByIdAsync(roleId.ToString());
            if (role is not null) Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }

    [Fact]
    public async Task Exit_confirmation_consumes_reserved_stock_and_writes_a_movement()
    {
        var (managerToken, itemId) = await CreateItemAsync();
        var department = new Department { Name = $"Department-{Guid.NewGuid():N}" };
        department.NameKey = department.Name.ToUpperInvariant();
        var industry = new Industry
        {
            Id = Guid.NewGuid(),
            Name = $"Industry-{Guid.NewGuid():N}",
            NameKey = Guid.NewGuid().ToString("N")
        };
        await using (var seedScope = factory.Services.CreateAsyncScope())
        {
            var seedDatabase = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            seedDatabase.Departments.Add(department);
            seedDatabase.Industries.Add(industry);
            await seedDatabase.SaveChangesAsync();
        }
        using var entry = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", managerToken,
            new { quantity = 5, idempotency_key = "confirm-exit-entry-01" });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/exits")
        {
            Content = JsonContent.Create(new
            {
                item_id = itemId,
                quantity = 2,
                purpose = "Entrega aprovada",
                recipient = "Cliente local",
                department_id = department.Id,
                industry_id = industry.Id,
                purchase_ticket_no = "CONF-OC-2026-1",
                document_ref = "NF-CONF-1"
            })
        };
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
        createRequest.Headers.Add("Idempotency-Key", "confirm-exit-create-001");
        using var created = await _client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var orderId = createdJson.RootElement.GetProperty("data").GetProperty("order").GetProperty("id").GetGuid();

        var confirmerToken = await CreateTokenAsync(["stock.exit_confirm"]);
        using var confirmRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/stock/exit-orders/{orderId}/confirm");
        confirmRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", confirmerToken);
        confirmRequest.Headers.Add("Idempotency-Key", "confirm-exit-order-001");
        using var confirmed = await _client.SendAsync(confirmRequest);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        using var confirmedJson = JsonDocument.Parse(await confirmed.Content.ReadAsStringAsync());
        var data = confirmedJson.RootElement.GetProperty("data");
        var order = data.GetProperty("order");
        Assert.Equal("confirmada", order.GetProperty("status").GetString());
        var movementId = order.GetProperty("movementId").GetGuid();
        var stock = data.GetProperty("stock");
        Assert.Equal(3, stock.GetProperty("onHand").GetInt32());
        Assert.Equal(0, stock.GetProperty("reserved").GetInt32());

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var movement = await database.StockMovements.SingleAsync(row => row.Id == movementId);
        Assert.Equal("saida", movement.Type);
        Assert.Equal(-2, movement.Quantity);
        Assert.Equal(3, movement.BalanceAfter);
        Assert.Equal("Entrega aprovada", movement.Purpose);
        Assert.Equal("Cliente local", movement.Recipient);
        Assert.Equal("CONF-OC-2026-1", movement.PurchaseTicketNo);
        Assert.Equal("NF-CONF-1", movement.DocumentRef);
        Assert.Equal(industry.Id, movement.IndustryId);

        using var departmentMovements = await SendAsync(HttpMethod.Get,
            $"/api/v1/stock/movements?itemId={itemId}&department_id={department.Id}&industry_id={industry.Id}", managerToken);
        Assert.Equal(HttpStatusCode.OK, departmentMovements.StatusCode);
        using var departmentMovementsJson = JsonDocument.Parse(await departmentMovements.Content.ReadAsStringAsync());
        var filteredMovements = departmentMovementsJson.RootElement.GetProperty("items");
        Assert.Single(filteredMovements.EnumerateArray());
        Assert.Equal(department.Id, filteredMovements[0].GetProperty("department_id").GetGuid());
    }

    [Fact]
    public async Task Exit_cancellation_releases_reservation_and_preserves_on_hand_stock()
    {
        var (managerToken, itemId) = await CreateItemAsync();
        using var entry = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", managerToken,
            new { quantity = 5, idempotency_key = "cancel-exit-entry-01" });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/exits")
        {
            Content = JsonContent.Create(new { item_id = itemId, quantity = 2, purpose = "Entrega cancelável" })
        };
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
        createRequest.Headers.Add("Idempotency-Key", "cancel-exit-create-001");
        using var created = await _client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var orderId = createdJson.RootElement.GetProperty("data").GetProperty("order").GetProperty("id").GetGuid();

        using var cancelRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/stock/exit-orders/{orderId}/cancel")
        {
            Content = JsonContent.Create(new { reason = "Solicitação retirada" })
        };
        cancelRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
        using var cancelled = await _client.SendAsync(cancelRequest);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        using var cancelledJson = JsonDocument.Parse(await cancelled.Content.ReadAsStringAsync());
        Assert.Equal("cancelada", cancelledJson.RootElement.GetProperty("data").GetProperty("order").GetProperty("status").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stock = await database.Stocks.SingleAsync(row => row.ItemId == itemId);
        Assert.Equal(5, stock.OnHand);
        Assert.Equal(0, stock.Reserved);
        var order = await database.StockExitOrders.SingleAsync(row => row.Id == orderId);
        Assert.Contains("Cancelada: Solicitação retirada", order.Notes);
        var audit = await database.AuditLogs.SingleAsync(log => log.Action == "stock_exit_cancel" && log.EntityId == orderId);
        Assert.Equal("stock_exit_order", audit.EntityType);
        Assert.NotNull(audit.UserId);
        using var before = JsonDocument.Parse(audit.BeforeJson!);
        using var after = JsonDocument.Parse(audit.AfterJson!);
        Assert.Equal("autorizada", before.RootElement.GetProperty("status").GetString());
        Assert.Equal("cancelada", after.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Exit_order_queue_lists_pending_orders_for_cd_permission()
    {
        var (managerToken, itemId) = await CreateItemAsync();
        using var entry = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", managerToken,
            new { quantity = 4, idempotency_key = "exit-queue-entry-001" });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/exits")
        {
            Content = JsonContent.Create(new { item_id = itemId, quantity = 1, purpose = "Fila do CD" })
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
        create.Headers.Add("Idempotency-Key", "exit-queue-create-001");
        using var created = await _client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var orderId = createdJson.RootElement.GetProperty("data").GetProperty("order").GetProperty("id").GetGuid();

        var cdToken = await CreateTokenAsync(["stock.exit_confirm"]);
        using var list = await SendAsync(HttpMethod.Get, "/api/v1/stock/exit-orders", cdToken);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.True(listJson.RootElement.GetProperty("ok").GetBoolean());
        var data = listJson.RootElement.GetProperty("data");
        Assert.Contains(data.EnumerateArray(), row => row.GetProperty("id").GetGuid() == orderId);

        using var denied = await SendAsync(HttpMethod.Get, "/api/v1/stock/exit-orders", await CreateTokenAsync(["stock.view"]));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Exit_order_detail_returns_order_and_related_item_for_authorized_stock_user()
    {
        var (token, itemId) = await CreateItemAsync();
        using var entry = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", token,
            new { quantity = 3, idempotency_key = "exit-detail-entry-001" });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/exits")
        {
            Content = JsonContent.Create(new { item_id = itemId, quantity = 1, purpose = "Consulta detalhada" })
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        create.Headers.Add("Idempotency-Key", "exit-detail-create-001");
        using var created = await _client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var orderId = createdJson.RootElement.GetProperty("data").GetProperty("order").GetProperty("id").GetGuid();

        using var detail = await SendAsync(HttpMethod.Get, $"/api/v1/stock/exit-orders/{orderId}", token);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var order = detailJson.RootElement.GetProperty("data");
        Assert.Equal(orderId, order.GetProperty("id").GetGuid());
        Assert.Equal("Consulta detalhada", order.GetProperty("purpose").GetString());
        Assert.Equal(itemId, order.GetProperty("item").GetProperty("id").GetGuid());
        Assert.StartsWith("data:image/png;base64,", order.GetProperty("qr_png").GetString());
        Assert.Equal($"/api/v1/stock/exit-orders/{orderId}/qr", order.GetProperty("qr_url").GetString());

        using var denied = await SendAsync(HttpMethod.Get, $"/api/v1/stock/exit-orders/{orderId}", await CreateTokenAsync(["stock.view"]));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Exit_order_lookup_resolves_code_case_insensitively()
    {
        var (token, itemId) = await CreateItemAsync();
        using var entry = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", token,
            new { quantity = 2, idempotency_key = "exit-lookup-entry-001" });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/exits")
        {
            Content = JsonContent.Create(new { item_id = itemId, quantity = 1, purpose = "Busca por código" })
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        create.Headers.Add("Idempotency-Key", "exit-lookup-create-001");
        using var created = await _client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var code = createdJson.RootElement.GetProperty("data").GetProperty("order").GetProperty("code").GetString()!;

        using var lookup = await SendAsync(HttpMethod.Get, $"/api/v1/stock/exit-orders/lookup?code={code.ToLowerInvariant()}", token);
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        using var lookupJson = JsonDocument.Parse(await lookup.Content.ReadAsStringAsync());
        Assert.Equal(code, lookupJson.RootElement.GetProperty("data").GetProperty("code").GetString());
        Assert.Equal("Busca por código", lookupJson.RootElement.GetProperty("data").GetProperty("purpose").GetString());
    }

    [Fact]
    public async Task Exit_order_qr_returns_private_png_to_users_with_exit_access()
    {
        var (token, itemId) = await CreateItemAsync();
        using var entry = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", token,
            new { quantity = 1, idempotency_key = "exit-qr-entry-0001" });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/exits")
        {
            Content = JsonContent.Create(new { item_id = itemId, quantity = 1, purpose = "QR de retirada" })
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        create.Headers.Add("Idempotency-Key", "exit-qr-create-0001");
        using var created = await _client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var orderId = createdJson.RootElement.GetProperty("data").GetProperty("order").GetProperty("id").GetGuid();

        using var qr = await SendAsync(HttpMethod.Get, $"/api/v1/stock/exit-orders/{orderId}/qr", token);
        Assert.Equal(HttpStatusCode.OK, qr.StatusCode);
        Assert.Equal("image/png", qr.Content.Headers.ContentType?.MediaType);
        var bytes = await qr.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 8);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        Assert.True(qr.Headers.CacheControl?.Private);
        Assert.Equal(TimeSpan.FromSeconds(120), qr.Headers.CacheControl?.MaxAge);
        Assert.Equal("nosniff", qr.Headers.GetValues("X-Content-Type-Options").Single());

        using var denied = await SendAsync(HttpMethod.Get, $"/api/v1/stock/exit-orders/{orderId}/qr", await CreateTokenAsync(["stock.view"]));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Exit_confirmation_replays_identical_request_without_consuming_stock_twice()
    {
        var (managerToken, itemId) = await CreateItemAsync();
        using var entry = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", managerToken,
            new { quantity = 5, idempotency_key = "confirm-replay-entry-01" });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/exits")
        {
            Content = JsonContent.Create(new { item_id = itemId, quantity = 2, purpose = "Entrega repetida" })
        };
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
        createRequest.Headers.Add("Idempotency-Key", "confirm-replay-create-001");
        using var created = await _client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var orderId = createdJson.RootElement.GetProperty("data").GetProperty("order").GetProperty("id").GetGuid();

        var confirmerToken = await CreateTokenAsync(["stock.exit_confirm"]);
        async Task<HttpResponseMessage> ConfirmAsync()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/stock/exit-orders/{orderId}/confirm");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", confirmerToken);
            request.Headers.Add("Idempotency-Key", "confirm-replay-order-001");
            return await _client.SendAsync(request);
        }

        using var first = await ConfirmAsync();
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var movementId = firstJson.RootElement.GetProperty("data").GetProperty("order").GetProperty("movementId").GetGuid();

        using var replay = await ConfirmAsync();
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replayed").Single());
        using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(movementId, replayJson.RootElement.GetProperty("data").GetProperty("order").GetProperty("movementId").GetGuid());
        var stock = replayJson.RootElement.GetProperty("data").GetProperty("stock");
        Assert.Equal(3, stock.GetProperty("onHand").GetInt32());
        Assert.Equal(0, stock.GetProperty("reserved").GetInt32());

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Single(await database.StockMovements.Where(row => row.ItemId == itemId && row.Type == "saida").ToListAsync());
    }

    [Fact]
    public async Task Exit_confirmation_with_new_key_rejects_an_already_confirmed_order()
    {
        var (managerToken, itemId) = await CreateItemAsync();
        using var entry = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", managerToken,
            new { quantity = 5, idempotency_key = "confirm-reject-entry-01" });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/exits")
        {
            Content = JsonContent.Create(new { item_id = itemId, quantity = 2, purpose = "Entrega finalizada" })
        };
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
        createRequest.Headers.Add("Idempotency-Key", "confirm-reject-create-001");
        using var created = await _client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var orderId = createdJson.RootElement.GetProperty("data").GetProperty("order").GetProperty("id").GetGuid();
        var confirmerToken = await CreateTokenAsync(["stock.exit_confirm"]);

        async Task<HttpResponseMessage> ConfirmAsync(string key)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/stock/exit-orders/{orderId}/confirm");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", confirmerToken);
            request.Headers.Add("Idempotency-Key", key);
            return await _client.SendAsync(request);
        }

        using var first = await ConfirmAsync("confirm-reject-first-001");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var second = await ConfirmAsync("confirm-reject-second-001");
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stock = await database.Stocks.SingleAsync(row => row.ItemId == itemId);
        Assert.Equal(3, stock.OnHand);
        Assert.Equal(0, stock.Reserved);
        Assert.Single(await database.StockMovements.Where(row => row.ItemId == itemId && row.Type == "saida").ToListAsync());
    }

    [Fact]
    public async Task Exit_confirmation_rejects_idempotency_key_reuse_for_another_order()
    {
        var (managerToken, itemId) = await CreateItemAsync();
        using var entry = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", managerToken,
            new { quantity = 5, idempotency_key = "confirm-conflict-entry-01" });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);

        async Task<Guid> AuthorizeAsync(string key)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/exits")
            {
                Content = JsonContent.Create(new { item_id = itemId, quantity = 1, purpose = "Entrega distinta" })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
            request.Headers.Add("Idempotency-Key", key);
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("data").GetProperty("order").GetProperty("id").GetGuid();
        }

        var firstOrderId = await AuthorizeAsync("confirm-conflict-create-001");
        var secondOrderId = await AuthorizeAsync("confirm-conflict-create-002");
        var confirmerToken = await CreateTokenAsync(["stock.exit_confirm"]);

        async Task<HttpResponseMessage> ConfirmAsync(Guid orderId)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/stock/exit-orders/{orderId}/confirm");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", confirmerToken);
            request.Headers.Add("Idempotency-Key", "confirm-global-key-001");
            return await _client.SendAsync(request);
        }

        using var first = await ConfirmAsync(firstOrderId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var reused = await ConfirmAsync(secondOrderId);
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stock = await database.Stocks.SingleAsync(row => row.ItemId == itemId);
        Assert.Equal(4, stock.OnHand);
        Assert.Equal(1, stock.Reserved);
        Assert.Equal("autorizada", (await database.StockExitOrders.SingleAsync(row => row.Id == secondOrderId)).Status);
    }

    [Fact]
    public async Task Exit_confirmation_writes_stock_and_order_audits_atomically()
    {
        var (managerToken, itemId) = await CreateItemAsync();
        using var entry = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", managerToken,
            new { quantity = 4, idempotency_key = "confirm-audit-entry-01" });
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/exits")
        {
            Content = JsonContent.Create(new { item_id = itemId, quantity = 1, purpose = "Entrega auditada" })
        };
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
        createRequest.Headers.Add("Idempotency-Key", "confirm-audit-create-001");
        using var created = await _client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var order = createdJson.RootElement.GetProperty("data").GetProperty("order");
        var orderId = order.GetProperty("id").GetGuid();
        var orderCode = order.GetProperty("code").GetString();

        var confirmerToken = await CreateTokenAsync(["stock.exit_confirm"]);
        using var confirmRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/stock/exit-orders/{orderId}/confirm");
        confirmRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", confirmerToken);
        confirmRequest.Headers.Add("Idempotency-Key", "confirm-audit-order-001");
        using var confirmed = await _client.SendAsync(confirmRequest);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        using var confirmedJson = JsonDocument.Parse(await confirmed.Content.ReadAsStringAsync());
        var confirmedOrder = confirmedJson.RootElement.GetProperty("data").GetProperty("order");
        var movementId = confirmedOrder.GetProperty("movementId").GetGuid();
        var confirmerId = confirmedOrder.GetProperty("confirmedBy").GetGuid();

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stockAudit = await database.AuditLogs.SingleAsync(log => log.Action == "stock_saida"
            && log.EntityType == "item" && log.EntityId == itemId);
        var orderAudit = await database.AuditLogs.SingleAsync(log => log.Action == "stock_exit_confirm"
            && log.EntityType == "stock_exit_order" && log.EntityId == orderId);
        Assert.Equal(confirmerId, stockAudit.UserId);
        Assert.Equal(confirmerId, orderAudit.UserId);
        Assert.Equal(orderCode, orderAudit.EntityLabel);
        using var stockBefore = JsonDocument.Parse(stockAudit.BeforeJson!);
        using var stockAfter = JsonDocument.Parse(stockAudit.AfterJson!);
        Assert.Equal(4, stockBefore.RootElement.GetProperty("on_hand").GetInt32());
        Assert.Equal(3, stockAfter.RootElement.GetProperty("on_hand").GetInt32());
        Assert.Equal(-1, stockAfter.RootElement.GetProperty("quantity").GetInt32());
        Assert.Equal(movementId, stockAfter.RootElement.GetProperty("movement_id").GetGuid());
        using var orderBefore = JsonDocument.Parse(orderAudit.BeforeJson!);
        using var orderAfter = JsonDocument.Parse(orderAudit.AfterJson!);
        Assert.Equal("autorizada", orderBefore.RootElement.GetProperty("status").GetString());
        Assert.Equal("confirmada", orderAfter.RootElement.GetProperty("status").GetString());
        Assert.Equal(movementId, orderAfter.RootElement.GetProperty("movement_id").GetGuid());
    }

    [Fact]
    public async Task Idempotency_conflict_and_insufficient_exit_do_not_mutate_stock()
    {
        var (token, itemId) = await CreateItemAsync();
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", token, new { quantity = 4, idempotency_key = "same" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", token, new { quantity = 5, idempotency_key = "same" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/exit", token, new { quantity = 5, reason = "use", idempotency_key = "exit" })).StatusCode);
        using var list = await SendAsync(HttpMethod.Get, $"/api/v1/stock/movements?itemId={itemId}", token);
        Assert.Equal(1, JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Adjustment_sets_stock_and_returns_legacy_created_envelope()
    {
        var (token, itemId) = await CreateItemAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/adjustments")
        {
            Content = JsonContent.Create(new { item_id = itemId, mode = "set", quantity = 6, reason = "count correction", notes = "cycle count" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", "adjust-set-001");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(6, json.RootElement.GetProperty("data").GetProperty("balanceAfter").GetInt32());
        var movementId = json.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/adjustments")
        {
            Content = JsonContent.Create(new { item_id = itemId, mode = "set", quantity = 6, reason = "count correction", notes = "cycle count" })
        };
        replayRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        replayRequest.Headers.Add("Idempotency-Key", "adjust-set-001");
        using var replayResponse = await _client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        using var replayJson = JsonDocument.Parse(await replayResponse.Content.ReadAsStringAsync());
        Assert.Equal(movementId, replayJson.RootElement.GetProperty("data").GetProperty("id").GetGuid());

        using var reusedKeyRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/adjustments")
        {
            Content = JsonContent.Create(new { item_id = itemId, mode = "delta", quantity = 6, reason = "count correction", notes = "cycle count" })
        };
        reusedKeyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        reusedKeyRequest.Headers.Add("Idempotency-Key", "adjust-set-001");
        using var reusedKeyResponse = await _client.SendAsync(reusedKeyRequest);
        Assert.Equal(HttpStatusCode.Conflict, reusedKeyResponse.StatusCode);

        using var secondSetRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/adjustments")
        {
            Content = JsonContent.Create(new { item_id = itemId, mode = "set", quantity = 4, reason = "count correction", notes = "cycle count" })
        };
        secondSetRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        secondSetRequest.Headers.Add("Idempotency-Key", "adjust-set-002");
        using var secondSetResponse = await _client.SendAsync(secondSetRequest);
        Assert.Equal(HttpStatusCode.Created, secondSetResponse.StatusCode);
        using var secondSetJson = JsonDocument.Parse(await secondSetResponse.Content.ReadAsStringAsync());
        Assert.Equal(-2, secondSetJson.RootElement.GetProperty("data").GetProperty("quantity").GetInt32());
        Assert.Equal(4, secondSetJson.RootElement.GetProperty("data").GetProperty("balanceAfter").GetInt32());
    }

    [Fact]
    public async Task Adjustment_delta_respects_reservations_and_rejects_no_change()
    {
        var (token, itemId) = await CreateItemAsync();
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", token, new { quantity = 5, idempotency_key = "adjust-entry-1" })).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stock = await database.Stocks.SingleAsync(x => x.ItemId == itemId);
            stock.Reserved = 4;
            await database.SaveChangesAsync();
        }

        using var belowReserved = await PostAdjustmentAsync(token, itemId, "delta", -2, "cycle correction", "adjust-delta-001");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, belowReserved.StatusCode);
        using var belowReservedJson = JsonDocument.Parse(await belowReserved.Content.ReadAsStringAsync());
        Assert.Equal("STOCK_BELOW_RESERVED", belowReservedJson.RootElement.GetProperty("error").GetProperty("code").GetString());
        using var insufficient = await PostAdjustmentAsync(token, itemId, "delta", -10, "cycle correction", "adjust-delta-002");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, insufficient.StatusCode);
        using var validDelta = await PostAdjustmentAsync(token, itemId, "delta", -1, "cycle correction", "adjust-delta-003");
        Assert.Equal(HttpStatusCode.Created, validDelta.StatusCode);
        using var validJson = JsonDocument.Parse(await validDelta.Content.ReadAsStringAsync());
        Assert.Equal(-1, validJson.RootElement.GetProperty("data").GetProperty("quantity").GetInt32());
        Assert.Equal(4, validJson.RootElement.GetProperty("data").GetProperty("balanceAfter").GetInt32());
        using var unchanged = await PostAdjustmentAsync(token, itemId, "set", 4, "cycle correction", "adjust-set-003");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unchanged.StatusCode);
    }

    [Fact]
    public async Task Adjustment_requires_stock_adjust_permission_and_idempotency_header()
    {
        var (token, itemId) = await CreateItemAsync();
        var unauthorizedToken = await CreateTokenAsync(["items.view"]);
        using var forbidden = await PostAdjustmentAsync(unauthorizedToken, itemId, "set", 6, "count correction", "adjust-forbidden-1");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var missingKeyRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/adjustments")
        {
            Content = JsonContent.Create(new { item_id = itemId, mode = "set", quantity = 6, reason = "count correction" })
        };
        missingKeyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var missingKey = await _client.SendAsync(missingKeyRequest);
        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
        using var missingKeyJson = JsonDocument.Parse(await missingKey.Content.ReadAsStringAsync());
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", missingKeyJson.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Reversal_appends_inverse_movement_and_replays_idempotently()
    {
        var (token, itemId) = await CreateItemAsync();
        using var entry = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", token, new { quantity = 7, idempotency_key = "reverse-entry-1", document_ref = "INV-7" });
        using var entryJson = JsonDocument.Parse(await entry.Content.ReadAsStringAsync());
        var sourceId = entryJson.RootElement.GetProperty("id").GetGuid();

        using var first = await PostReversalAsync(token, sourceId, "count error", "reverse-001");
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.True(firstJson.RootElement.GetProperty("ok").GetBoolean());
        var data = firstJson.RootElement.GetProperty("data");
        Assert.Equal(-7, data.GetProperty("quantity").GetInt32());
        Assert.Equal(0, data.GetProperty("balanceAfter").GetInt32());
        Assert.Equal("count error", data.GetProperty("reason").GetString());
        Assert.Equal($"Estorno da movimentação #{sourceId}", data.GetProperty("notes").GetString());
        Assert.Equal("INV-7", data.GetProperty("documentRef").GetString());
        var reversalId = data.GetProperty("id").GetGuid();

        using var replay = await PostReversalAsync(token, sourceId, "count error", "reverse-001");
        using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(reversalId, replayJson.RootElement.GetProperty("data").GetProperty("id").GetGuid());
        using var reusedKey = await PostReversalAsync(token, sourceId, "different reason", "reverse-001");
        Assert.Equal(HttpStatusCode.Conflict, reusedKey.StatusCode);
        using var movements = await SendAsync(HttpMethod.Get, $"/api/v1/stock/movements?itemId={itemId}", token);
        Assert.Equal(2, JsonDocument.Parse(await movements.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Reversal_rejects_invalid_source_reason_transfer_and_insufficient_stock()
    {
        var (token, itemId) = await CreateItemAsync();
        using var absent = await PostReversalAsync(token, Guid.NewGuid(), "bad", "reverse-absent");
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using var entry = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/entry", token, new { quantity = 3, idempotency_key = "reverse-entry-2" });
        using var entryJson = JsonDocument.Parse(await entry.Content.ReadAsStringAsync());
        var sourceId = entryJson.RootElement.GetProperty("id").GetGuid();
        var transferId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.StockMovements.Add(new StockMovement { Id = transferId, ItemId = itemId, Type = "transferencia", Quantity = -1, BalanceAfter = 2 });
            await database.SaveChangesAsync();
        }
        using var transfer = await PostReversalAsync(token, transferId, "reverse transfer", "reverse-transfer");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, transfer.StatusCode);
        using var transferJson = JsonDocument.Parse(await transfer.Content.ReadAsStringAsync());
        Assert.Equal("REVERSE_TRANSFER", transferJson.RootElement.GetProperty("error").GetProperty("code").GetString());
        using var tooShort = await PostReversalAsync(token, sourceId, " x ", "reverse-short");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooShort.StatusCode);
        using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stock = await db.Stocks.SingleAsync(x => x.ItemId == itemId);
            stock.Reserved = 2;
            await db.SaveChangesAsync();
        }
        using var insufficient = await PostReversalAsync(token, sourceId, "undo", "reverse-low");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, insufficient.StatusCode);
        using var forbidden = await PostReversalAsync(await CreateTokenAsync(["stock.view"]), sourceId, "undo", "reverse-forbidden");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var noKeyRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/reversals") { Content = JsonContent.Create(new { movement_id = sourceId, reason = "undo" }) };
        noKeyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var noKey = await _client.SendAsync(noKeyRequest);
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
    }

    private Task<HttpResponseMessage> PostReversalAsync(string token, Guid movementId, string reason, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/reversals")
        {
            Content = JsonContent.Create(new { movement_id = movementId, reason })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", key);
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Stock_reconciliation_repairs_missing_position_and_opening_movement_once()
    {
        var (token, itemId) = await CreateItemAsync();
        var locationId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Locations.Add(new Location
            {
                Id = locationId,
                Name = $"Reconciliação {locationId:N}",
                NameKey = locationId.ToString("N"),
                Kind = "cd"
            });
            database.CatalogItems.Single(item => item.Id == itemId).LocationId = locationId;
            database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 6 });
            await database.SaveChangesAsync();
        }

        using var first = await SendAsync(HttpMethod.Post, "/api/v1/stock/reconcile", token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.True(firstJson.RootElement.GetProperty("data").GetProperty("total_items").GetInt32() >= 1);
        Assert.True(firstJson.RootElement.GetProperty("data").GetProperty("fixed_positions").GetInt32() >= 1);
        Assert.True(firstJson.RootElement.GetProperty("data").GetProperty("fixed_movements").GetInt32() >= 1);

        using var replay = await SendAsync(HttpMethod.Post, "/api/v1/stock/reconcile", token);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(0, replayJson.RootElement.GetProperty("data").GetProperty("fixed_positions").GetInt32());
        Assert.Equal(0, replayJson.RootElement.GetProperty("data").GetProperty("fixed_movements").GetInt32());

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(6, (await verify.Stocks.SingleAsync(stock => stock.ItemId == itemId)).OnHand);
        Assert.Equal(6, (await verify.StockPositions.SingleAsync(position => position.ItemId == itemId)).Quantity);
        var movement = await verify.StockMovements.SingleAsync(entry => entry.ItemId == itemId);
        Assert.Equal("entrada", movement.Type);
        Assert.Equal(6, movement.Quantity);
        Assert.Equal("Saldo inicial", movement.Reason);
    }

    [Fact]
    public async Task Inventory_routes_require_permissions()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/stock/movements")).StatusCode);
        var token = await CreateTokenAsync(["items.manage"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/v1/stock/movements", token)).StatusCode);
    }

    private async Task<(string, Guid)> CreateItemAsync()
    {
        var token = await CreateTokenAsync(["items.view", "items.manage", "lookups.view", "lookups.manage", "stock.view", "stock.entry", "stock.exit", "stock.adjust"]);
        var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Cat-{Guid.NewGuid():N}" });
        using var cj = JsonDocument.Parse(await category.Content.ReadAsStringAsync());
        var categoryId = cj.RootElement.GetProperty("id").GetGuid();
        var response = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { code = $"IT-{Guid.NewGuid():N}"[..10], name = "Caneca", categoryId });
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (token, json.RootElement.GetProperty("id").GetGuid());
    }

    private async Task<string> CreateTokenAsync(string[] permissions, Guid? industryId = null)
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = new ApplicationRole($"r-{Guid.NewGuid():N}");
        Assert.True((await roles.CreateAsync(role)).Succeeded);
        foreach (var permission in permissions) Assert.True((await roles.AddClaimAsync(role, new("permission", permission))).Succeeded);
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Inventory test", IndustryId = industryId };
        Assert.True((await users.CreateAsync(user, "CorrectPassword1")).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role.Name!)).Succeeded);
        using var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("accessToken").GetString()!;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> PostAdjustmentAsync(string token, Guid itemId, string mode, int quantity, string reason, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/adjustments")
        {
            Content = JsonContent.Create(new { item_id = itemId, mode, quantity, reason })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", key);
        return _client.SendAsync(request);
    }
}
