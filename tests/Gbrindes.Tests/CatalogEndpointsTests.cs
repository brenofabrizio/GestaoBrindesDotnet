using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Gbrindes.Web.Data;
using Gbrindes.Web.Identity;
using Gbrindes.Web.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace Gbrindes.Tests;

public sealed class CatalogEndpointsTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Catalog_endpoints_require_authentication()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/categories")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/items")).StatusCode);
    }

    [Fact]
    public async Task Category_and_item_crud_and_status_transitions_persist()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view", "lookups.manage", "lookups.delete", "items.view", "items.manage"]);
        var token = await LoginAsync(email);
        using var categoryResponse = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = "Outdoor", description = "Equipamentos" });
        Assert.Equal(HttpStatusCode.Created, categoryResponse.StatusCode);
        using var categoryJson = JsonDocument.Parse(await categoryResponse.Content.ReadAsStringAsync());
        var categoryId = categoryJson.RootElement.GetProperty("id").GetGuid();

        using var categoryList = await SendAsync(HttpMethod.Get, "/api/v1/categories", token);
        Assert.Equal(HttpStatusCode.OK, categoryList.StatusCode);
        using var categoryGet = await SendAsync(HttpMethod.Get, $"/api/v1/categories/{categoryId}", token);
        Assert.Equal("Outdoor", JsonDocument.Parse(await categoryGet.Content.ReadAsStringAsync()).RootElement.GetProperty("name").GetString());
        using var categoryUpdate = await SendAsync(HttpMethod.Put, $"/api/v1/categories/{categoryId}", token, new { description = "Acessórios" });
        Assert.Equal(HttpStatusCode.OK, categoryUpdate.StatusCode);
        using var categoryInactive = await SendAsync(HttpMethod.Post, $"/api/v1/categories/{categoryId}/deactivate", token);
        Assert.False(JsonDocument.Parse(await categoryInactive.Content.ReadAsStringAsync()).RootElement.GetProperty("active").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/api/v1/categories/{categoryId}/deactivate", token)).StatusCode);
        using var categoryActive = await SendAsync(HttpMethod.Post, $"/api/v1/categories/{categoryId}/activate", token);
        Assert.True(JsonDocument.Parse(await categoryActive.Content.ReadAsStringAsync()).RootElement.GetProperty("active").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/api/v1/categories/{categoryId}/activate", token)).StatusCode);

        using var itemResponse = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { code = "BRD-100", name = "Garrafa", categoryId, kind = "fisico", minStock = 3 });
        Assert.Equal(HttpStatusCode.Created, itemResponse.StatusCode);
        using var itemJson = JsonDocument.Parse(await itemResponse.Content.ReadAsStringAsync());
        var itemId = itemJson.RootElement.GetProperty("id").GetGuid();
        using var itemList = await SendAsync(HttpMethod.Get, "/api/v1/items", token);
        Assert.Equal(HttpStatusCode.OK, itemList.StatusCode);
        using var itemGet = await SendAsync(HttpMethod.Get, $"/api/v1/items/{itemId}", token);
        Assert.Equal("BRD-100", JsonDocument.Parse(await itemGet.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        using var update = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { name = "Garrafa térmica" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using var inactive = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/deactivate", token);
        Assert.Equal("inativo", JsonDocument.Parse(await inactive.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());
        using var active = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/activate", token);
        Assert.Equal("ativo", JsonDocument.Parse(await active.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/categories/{categoryId}", token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/api/v1/categories/{categoryId}/restore", token)).StatusCode);
        await using var auditScope = factory.Services.CreateAsyncScope();
        var auditDatabase = auditScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var events = await auditDatabase.AuditLogs.Where(log => log.EntityType == "category" && log.EntityId == categoryId).ToListAsync();
        Assert.Equal(["activate", "create", "deactivate", "delete", "restore", "update"], events.Select(log => log.Action).OrderBy(action => action));
        Assert.All(events, log => Assert.NotNull(log.UserId));
        var categoryActorId = events.Single(log => log.Action == "create").UserId;
        var persistedCategory = await auditDatabase.Categories.SingleAsync(value => value.Id == categoryId);
        Assert.Equal(categoryActorId, typeof(Category).GetProperty("CreatedBy")?.GetValue(persistedCategory));
        Assert.Equal(events.Single(log => log.Action == "activate").UserId, typeof(Category).GetProperty("UpdatedBy")?.GetValue(persistedCategory));
        Assert.False(categoryJson.RootElement.TryGetProperty("createdBy", out _));
        var updateEvent = events.Single(log => log.Action == "update");
        using var updateBefore = JsonDocument.Parse(updateEvent.BeforeJson!);
        using var updateAfter = JsonDocument.Parse(updateEvent.AfterJson!);
        Assert.Equal("Equipamentos", updateBefore.RootElement.GetProperty("description").GetString());
        Assert.Equal("Acessórios", updateAfter.RootElement.GetProperty("description").GetString());
        var persistedItem = await auditDatabase.CatalogItems.SingleAsync(value => value.Id == itemId);
        var itemActorId = (await auditDatabase.AuditLogs.SingleAsync(log => log.EntityType == "item" && log.EntityId == itemId && log.Action == "create")).UserId;
        Assert.Equal(itemActorId, typeof(CatalogItem).GetProperty("CreatedBy")?.GetValue(persistedItem));
        Assert.Equal(itemActorId, typeof(CatalogItem).GetProperty("UpdatedBy")?.GetValue(persistedItem));
        Assert.False(itemJson.RootElement.TryGetProperty("createdBy", out _));
    }

    [Fact]
    public async Task Catalog_item_industry_is_optional_editable_filterable_and_used_for_unassigned_stock()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view", "items.view", "items.manage", "reports.view", "stock.view"]);
        var token = await LoginAsync(email);
        var categoryId = Guid.NewGuid();
        var firstIndustryId = Guid.NewGuid();
        var secondIndustryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var code = $"IND-{Guid.NewGuid():N}"[..15].ToUpperInvariant();
        var firstIndustryName = $"Catalog industry A {firstIndustryId:N}";
        var secondIndustryName = $"Catalog industry B {secondIndustryId:N}";

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                database.Categories.Add(new Category { Id = categoryId, Name = $"Industry category {categoryId:N}", NameKey = categoryId.ToString("N") });
                database.Industries.AddRange(
                    new Industry { Id = firstIndustryId, Name = firstIndustryName, NameKey = firstIndustryId.ToString("N") },
                    new Industry { Id = secondIndustryId, Name = secondIndustryName, NameKey = secondIndustryId.ToString("N") });
                await database.SaveChangesAsync();
            }

            using (var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new
            {
                code,
                name = "Industry-linked catalog item",
                categoryId,
                industry_id = firstIndustryId
            }))
            {
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
                itemId = json.RootElement.GetProperty("id").GetGuid();
            }

            using (var updated = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { industryId = secondIndustryId }))
                Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            using (var itemResponse = await SendAsync(HttpMethod.Get, $"/api/v1/items/{itemId}", token))
            {
                using var itemJson = JsonDocument.Parse(await itemResponse.Content.ReadAsStringAsync());
                Assert.Equal(secondIndustryId, itemJson.RootElement.GetProperty("industry").GetProperty("id").GetGuid());
            }

            using (var filtered = await SendAsync(HttpMethod.Get, $"/api/v1/items?industry_id={secondIndustryId}&q={code}", token))
            {
                using var json = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
                Assert.Single(json.RootElement.GetProperty("data").EnumerateArray());
            }

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                database.StockMovements.AddRange(
                    new StockMovement { ItemId = itemId, IndustryId = firstIndustryId, Type = "entrada", Quantity = 2 },
                    new StockMovement { ItemId = itemId, Type = "entrada", Quantity = 3 });
                await database.SaveChangesAsync();
            }

            using (var report = await SendAsync(HttpMethod.Get, "/api/v1/reports/stock_industry", token))
            {
                using var json = JsonDocument.Parse(await report.Content.ReadAsStringAsync());
                var rows = json.RootElement.GetProperty("data").GetProperty("rows").EnumerateArray().ToArray();
                var firstRow = Assert.Single(rows, row => row[0].GetString() == firstIndustryName && row[1].GetString() == code);
                var secondRow = Assert.Single(rows, row => row[0].GetString() == secondIndustryName && row[1].GetString() == code);
                Assert.Equal(2, firstRow[3].GetInt32());
                Assert.Equal(3, secondRow[3].GetInt32());
            }

            using (var stock = await SendAsync(HttpMethod.Get, "/api/v1/stock/by-industry", token))
            {
                using var json = JsonDocument.Parse(await stock.Content.ReadAsStringAsync());
                var rows = json.RootElement.GetProperty("data").EnumerateArray().ToArray();
                Assert.Contains(rows, row => row.GetProperty("industry").GetProperty("id").GetGuid() == firstIndustryId
                    && row.GetProperty("item").GetProperty("code").GetString() == code
                    && row.GetProperty("received").GetInt32() == 2);
                Assert.Contains(rows, row => row.GetProperty("industry").GetProperty("id").GetGuid() == secondIndustryId
                    && row.GetProperty("item").GetProperty("code").GetString() == code
                    && row.GetProperty("received").GetInt32() == 3);
            }
        }
        finally
        {
            await using var cleanup = factory.Services.CreateAsyncScope();
            var database = cleanup.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.StockMovements.RemoveRange(await database.StockMovements.Where(row => row.ItemId == itemId).ToListAsync());
            var item = await database.CatalogItems.SingleOrDefaultAsync(row => row.Id == itemId);
            if (item is not null) database.CatalogItems.Remove(item);
            var category = await database.Categories.SingleOrDefaultAsync(row => row.Id == categoryId);
            if (category is not null) database.Categories.Remove(category);
            database.Industries.RemoveRange(await database.Industries.Where(row => row.Id == firstIndustryId || row.Id == secondIndustryId).ToListAsync());
            await database.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Category_list_matches_legacy_envelope_and_pagination_contract()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view", "lookups.manage"]);
        var token = await LoginAsync(email);
        var marker = $"Category-envelope-{Guid.NewGuid():N}";
        foreach (var suffix in new[] { "A", "B" })
        {
            using var created = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"{marker}-{suffix}" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var encodedMarker = Uri.EscapeDataString(marker);
        using var secondPage = await SendAsync(HttpMethod.Get, $"/api/v1/categories?q={encodedMarker}&sort=name&page=2&per_page=1", token);
        Assert.Equal(HttpStatusCode.OK, secondPage.StatusCode);
        using var secondPageJson = JsonDocument.Parse(await secondPage.Content.ReadAsStringAsync());
        Assert.True(secondPageJson.RootElement.GetProperty("ok").GetBoolean());
        var secondPageData = secondPageJson.RootElement.GetProperty("data");
        Assert.Single(secondPageData.EnumerateArray());
        Assert.Equal($"{marker}-B", secondPageData[0].GetProperty("name").GetString());
        var metadata = secondPageJson.RootElement.GetProperty("meta");
        Assert.Equal(2, metadata.GetProperty("page").GetInt32());
        Assert.Equal(1, metadata.GetProperty("per_page").GetInt32());
        Assert.Equal(2, metadata.GetProperty("total").GetInt32());
        Assert.Equal(2, metadata.GetProperty("last_page").GetInt32());

        using var zeroPerPage = await SendAsync(HttpMethod.Get, $"/api/v1/categories?q={encodedMarker}&per_page=0", token);
        using var zeroPerPageJson = JsonDocument.Parse(await zeroPerPage.Content.ReadAsStringAsync());
        Assert.Equal(25, zeroPerPageJson.RootElement.GetProperty("meta").GetProperty("per_page").GetInt32());
        Assert.Equal(2, zeroPerPageJson.RootElement.GetProperty("data").GetArrayLength());

        using var all = await SendAsync(HttpMethod.Get, "/api/v1/categories?all=1", token);
        using var allJson = JsonDocument.Parse(await all.Content.ReadAsStringAsync());
        Assert.True(allJson.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains(allJson.RootElement.GetProperty("data").EnumerateArray(), row => row.GetProperty("name").GetString() == $"{marker}-A");
        Assert.False(allJson.RootElement.TryGetProperty("meta", out _));
    }

    [Fact]
    public async Task Item_list_matches_legacy_envelope_and_pagination_contract()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view", "lookups.manage", "items.view", "items.manage"]);
        var token = await LoginAsync(email);
        var marker = $"Item-envelope-{Guid.NewGuid():N}";
        using var categoryResponse = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"{marker}-category" });
        Assert.Equal(HttpStatusCode.Created, categoryResponse.StatusCode);
        var categoryId = JsonDocument.Parse(await categoryResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        foreach (var suffix in new[] { "A", "B" })
        {
            using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new
            {
                code = $"ENV-{Guid.NewGuid():N}",
                name = $"{marker}-{suffix}",
                categoryId
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var encodedMarker = Uri.EscapeDataString(marker);
        using var secondPage = await SendAsync(HttpMethod.Get, $"/api/v1/items?q={encodedMarker}&sort=name&page=2&per_page=1", token);
        Assert.Equal(HttpStatusCode.OK, secondPage.StatusCode);
        using var secondPageJson = JsonDocument.Parse(await secondPage.Content.ReadAsStringAsync());
        Assert.True(secondPageJson.RootElement.GetProperty("ok").GetBoolean());
        var secondPageData = secondPageJson.RootElement.GetProperty("data");
        Assert.Single(secondPageData.EnumerateArray());
        Assert.Equal($"{marker}-B", secondPageData[0].GetProperty("name").GetString());
        var metadata = secondPageJson.RootElement.GetProperty("meta");
        Assert.Equal(2, metadata.GetProperty("page").GetInt32());
        Assert.Equal(1, metadata.GetProperty("per_page").GetInt32());
        Assert.Equal(2, metadata.GetProperty("total").GetInt32());
        Assert.Equal(2, metadata.GetProperty("last_page").GetInt32());

        using var zeroPerPage = await SendAsync(HttpMethod.Get, $"/api/v1/items?q={encodedMarker}&per_page=0", token);
        using var zeroPerPageJson = JsonDocument.Parse(await zeroPerPage.Content.ReadAsStringAsync());
        Assert.Equal(25, zeroPerPageJson.RootElement.GetProperty("meta").GetProperty("per_page").GetInt32());
        Assert.Equal(2, zeroPerPageJson.RootElement.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task Item_options_returns_only_matching_active_items_with_stock_and_thumbnail_fields()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["items.view", "lookups.view", "lookups.manage"]);
        var token = await LoginAsync(email);
        var marker = $"Picker-{Guid.NewGuid():N}";
        using var categoryResponse = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"{marker}-category" });
        Assert.Equal(HttpStatusCode.Created, categoryResponse.StatusCode);
        var categoryId = JsonDocument.Parse(await categoryResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var activeItems = Enumerable.Range(0, 51).Select(index => new CatalogItem
        {
            Code = $"OPT-{Guid.NewGuid():N}"[..20],
            Name = $"{marker}-{index:D2}",
            CategoryId = categoryId,
            UnitValue = index == 0 ? 12.34m : null,
            MinStock = index == 0 ? 5 : 0,
            PhotoPath = index == 0 ? $"items/{Guid.NewGuid():N}/photo.jpg" : null,
            ThumbPath = index == 0 ? $"items/{Guid.NewGuid():N}/thumb.jpg" : null
        }).ToList();
        activeItems.Add(new CatalogItem { Code = $"OPT-{Guid.NewGuid():N}"[..20], Name = $"{marker}-inactive", CategoryId = categoryId, Status = "inativo" });
        activeItems.Add(new CatalogItem { Code = $"OPT-{Guid.NewGuid():N}"[..20], Name = $"{marker}-trashed", CategoryId = categoryId, DeletedAt = DateTimeOffset.UtcNow });
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.CatalogItems.AddRange(activeItems);
            database.Stocks.Add(new InventoryStock { ItemId = activeItems[0].Id, Item = activeItems[0], OnHand = 7, Reserved = 2 });
            await database.SaveChangesAsync();
        }

        using var response = await SendAsync(HttpMethod.Get, $"/api/v1/items/options?q={Uri.EscapeDataString(marker)}", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        var options = json.RootElement.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(50, options.Length);
        Assert.DoesNotContain(options, option => option.GetProperty("name").GetString()!.EndsWith("inactive", StringComparison.Ordinal));
        Assert.DoesNotContain(options, option => option.GetProperty("name").GetString()!.EndsWith("trashed", StringComparison.Ordinal));
        var first = Assert.Single(options, option => option.GetProperty("id").GetGuid() == activeItems[0].Id);
        Assert.Equal(12.34m, first.GetProperty("unit_value").GetDecimal());
        Assert.Equal(5, first.GetProperty("available").GetInt32());
        Assert.Equal("low", first.GetProperty("level").GetString());
        Assert.Contains("size=thumb", first.GetProperty("thumb_url").GetString());
        Assert.Equal($"{marker}-00", options[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Item_next_code_requires_manage_and_matches_the_code_used_for_creation()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["items.manage", "lookups.view", "lookups.manage"]);
        var token = await LoginAsync(email);
        using var preview = await SendAsync(HttpMethod.Get, "/api/v1/items/next-code", token);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var previewJson = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.True(previewJson.RootElement.GetProperty("ok").GetBoolean());
        var code = previewJson.RootElement.GetProperty("data").GetProperty("code").GetString()!;
        Assert.Matches("^BRD-[0-9]{5,}$", code);

        using var categoryResponse = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Next-code-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, categoryResponse.StatusCode);
        var categoryId = JsonDocument.Parse(await categoryResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var item = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { name = "Generated code item", categoryId });
        Assert.Equal(HttpStatusCode.Created, item.StatusCode);
        Assert.Equal(code, JsonDocument.Parse(await item.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        var viewerEmail = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(viewerEmail, ["items.view"]);
        var viewerToken = await LoginAsync(viewerEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/api/v1/items/next-code", viewerToken)).StatusCode);
    }

    [Fact]
    public async Task Item_code_preview_and_creation_use_the_monotonic_sequence_counter()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["items.manage", "lookups.view", "lookups.manage"]);
        var token = await LoginAsync(email);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var sequence = await database.SequenceCounters.SingleOrDefaultAsync(counter => counter.Name == "item" && counter.Period == "all");
            if (sequence is null)
                database.SequenceCounters.Add(new SequenceCounter { Name = "item", Period = "all", LastValue = 42 });
            else
                sequence.LastValue = 42;
            await database.SaveChangesAsync();
        }

        using var preview = await SendAsync(HttpMethod.Get, "/api/v1/items/next-code", token);
        using var previewJson = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.Equal("BRD-00043", previewJson.RootElement.GetProperty("data").GetProperty("code").GetString());
        using var categoryResponse = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Sequence-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, categoryResponse.StatusCode);
        var categoryId = JsonDocument.Parse(await categoryResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var item = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { name = "Sequenced item", categoryId });
        Assert.Equal(HttpStatusCode.Created, item.StatusCode);
        Assert.Equal("BRD-00043", JsonDocument.Parse(await item.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(43, (await verifyDatabase.SequenceCounters.SingleAsync(counter => counter.Name == "item" && counter.Period == "all")).LastValue);
    }

    [Fact]
    public async Task Item_photo_upload_serves_private_full_and_thumbnail_and_removes_files()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view", "lookups.manage", "items.view", "items.manage"]);
        var token = await LoginAsync(email);
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Photo-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, category.StatusCode);
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var item = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { code = $"PIC-{Guid.NewGuid():N}", name = "Photo item", categoryId });
        Assert.Equal(HttpStatusCode.Created, item.StatusCode);
        var itemId = JsonDocument.Parse(await item.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
        using var multipart = new MultipartFormDataContent();
        using var imageContent = new ByteArrayContent(png);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        multipart.Add(imageContent, "photo", "pixel.png");
        using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/items/{itemId}/photo") { Content = multipart };
        uploadRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var uploaded = await _client.SendAsync(uploadRequest);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        using var uploadedJson = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
        var photoUrl = uploadedJson.RootElement.GetProperty("photo_url").GetString();
        var thumbnailUrl = uploadedJson.RootElement.GetProperty("thumb_url").GetString();
        Assert.StartsWith($"/api/v1/items/{itemId}/photo", photoUrl);
        Assert.False(uploadedJson.RootElement.TryGetProperty("PhotoPath", out _));
        Assert.False(uploadedJson.RootElement.TryGetProperty("ThumbPath", out _));
        Assert.Contains("size=thumb", thumbnailUrl);
        var photoRoot = factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()
            .GetSection("Storage:ItemPhotos:RootPath").Value!;
        var photoItemRoot = Path.Combine(photoRoot, "items", itemId.ToString("N"));
        var previousFiles = Directory.GetFiles(photoItemRoot, "*.jpg", SearchOption.AllDirectories);
        Assert.Equal(2, previousFiles.Length);

        using var replacementMultipart = new MultipartFormDataContent();
        using var replacementContent = new ByteArrayContent(png);
        replacementContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        replacementMultipart.Add(replacementContent, "photo", "replacement.png");
        using var replacementRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/items/{itemId}/photo") { Content = replacementMultipart };
        replacementRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var replacement = await _client.SendAsync(replacementRequest);
        Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
        using var replacementJson = JsonDocument.Parse(await replacement.Content.ReadAsStringAsync());
        var replacementPhotoUrl = replacementJson.RootElement.GetProperty("photo_url").GetString();
        Assert.NotEqual(photoUrl, replacementPhotoUrl);
        Assert.Equal(2, Directory.GetFiles(photoItemRoot, "*.jpg", SearchOption.AllDirectories).Length);
        Assert.All(previousFiles, previousFile => Assert.False(File.Exists(previousFile)));
        photoUrl = replacementPhotoUrl;
        thumbnailUrl = replacementJson.RootElement.GetProperty("thumb_url").GetString();

        using var fullImage = await SendAsync(HttpMethod.Get, photoUrl!, token);
        using var thumbnail = await SendAsync(HttpMethod.Get, thumbnailUrl!, token);
        Assert.Equal("image/jpeg", fullImage.Content.Headers.ContentType?.MediaType);
        Assert.Equal("image/jpeg", thumbnail.Content.Headers.ContentType?.MediaType);
        Assert.True(fullImage.Headers.CacheControl?.Private);
        Assert.Equal(TimeSpan.FromHours(24), fullImage.Headers.CacheControl?.MaxAge);
        Assert.Equal("nosniff", fullImage.Headers.GetValues("X-Content-Type-Options").Single());
        var fullBytes = await fullImage.Content.ReadAsByteArrayAsync();
        Assert.Equal(0xFF, fullBytes[0]);
        Assert.Equal(0xD8, fullBytes[1]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync(photoUrl)).StatusCode);

        using var removed = await SendAsync(HttpMethod.Delete, $"/api/v1/items/{itemId}/photo", token);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/items/{itemId}/photo", token)).StatusCode);
        Assert.Empty(Directory.GetFiles(photoItemRoot, "*.jpg", SearchOption.AllDirectories));
        using var missingPhoto = await SendAsync(HttpMethod.Get, photoUrl!, token);
        Assert.Equal(HttpStatusCode.NotFound, missingPhoto.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await database.Users.SingleAsync(value => value.Email == email);
        var photoEvents = await database.AuditLogs.Where(log => log.EntityType == "item" && log.EntityId == itemId && log.Action.StartsWith("photo_")).ToListAsync();
        Assert.Equal(["photo_delete", "photo_update", "photo_update"], photoEvents.Select(log => log.Action).OrderBy(action => action));
        Assert.All(photoEvents, log => Assert.Equal(user.Id, log.UserId));
    }

    [Fact]
    public async Task Legacy_item_photo_route_serves_thumbnail_with_the_existing_items_view_permission()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view", "lookups.manage", "items.view", "items.manage"]);
        var token = await LoginAsync(email);
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Legacy-photo-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, category.StatusCode);
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var item = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { code = $"LEG-{Guid.NewGuid():N}", name = "Legacy photo item", categoryId });
        Assert.Equal(HttpStatusCode.Created, item.StatusCode);
        var itemId = JsonDocument.Parse(await item.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
        using var multipart = new MultipartFormDataContent();
        using var imageContent = new ByteArrayContent(png);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        multipart.Add(imageContent, "photo", "pixel.png");
        using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/items/{itemId}/photo") { Content = multipart };
        uploadRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var uploaded = await _client.SendAsync(uploadRequest);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);

        using var legacyThumbnail = await SendAsync(HttpMethod.Get, $"/api/items/{itemId}/photo?size=thumb", token);
        Assert.Equal(HttpStatusCode.OK, legacyThumbnail.StatusCode);
        Assert.Equal("image/jpeg", legacyThumbnail.Content.Headers.ContentType?.MediaType);
        Assert.True(legacyThumbnail.Headers.CacheControl?.Private);
        Assert.Equal(TimeSpan.FromHours(24), legacyThumbnail.Headers.CacheControl?.MaxAge);
        Assert.Equal("Authorization", legacyThumbnail.Headers.Vary.Single());
        Assert.Equal("nosniff", legacyThumbnail.Headers.GetValues("X-Content-Type-Options").Single());
        var bytes = await legacyThumbnail.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xFF, 0xD8], bytes.Take(2));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync($"/api/items/{itemId}/photo?size=thumb")).StatusCode);

        var deniedEmail = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(deniedEmail, []);
        var deniedToken = await LoginAsync(deniedEmail);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(HttpMethod.Get, $"/api/items/{itemId}/photo?size=thumb", deniedToken)).StatusCode);
    }

    [Fact]
    public async Task Item_photo_upload_rejects_content_that_is_not_a_supported_image()
    {
        var token = await CreatePermittedUserTokenAsync();
        var category = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Categories.Add(new Category { Id = category, Name = $"Invalid photo {category:N}", NameKey = category.ToString("N") });
            await database.SaveChangesAsync();
        }
        using var item = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { code = $"BAD-{Guid.NewGuid():N}", name = "Invalid photo item", categoryId = category });
        Assert.Equal(HttpStatusCode.Created, item.StatusCode);
        var itemId = JsonDocument.Parse(await item.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var multipart = new MultipartFormDataContent();
        using var invalidContent = new ByteArrayContent("not an image"u8.ToArray());
        invalidContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        multipart.Add(invalidContent, "photo", "fake.png");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/items/{itemId}/photo") { Content = multipart };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var oversizedMultipart = new MultipartFormDataContent();
        using var oversizedContent = new ByteArrayContent(new byte[checked((int)ItemPhotoStorage.MaximumUploadBytes + 1)]);
        oversizedContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        oversizedMultipart.Add(oversizedContent, "photo", "oversized.jpg");
        using var oversizedRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/items/{itemId}/photo") { Content = oversizedMultipart };
        oversizedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var oversizedResponse = await _client.SendAsync(oversizedRequest);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, oversizedResponse.StatusCode);
    }

    [Fact]
    public async Task Item_photo_upload_requires_manage_but_not_view_permission()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view", "lookups.manage", "items.manage"]);
        var token = await LoginAsync(email);
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Manage-only-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, category.StatusCode);
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var item = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { code = $"MNG-{Guid.NewGuid():N}", name = "Manage-only photo", categoryId });
        Assert.Equal(HttpStatusCode.Created, item.StatusCode);
        var itemId = JsonDocument.Parse(await item.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
        using var multipart = new MultipartFormDataContent();
        using var content = new ByteArrayContent(png);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        multipart.Add(content, "photo", "pixel.png");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/items/{itemId}/photo") { Content = multipart };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var uploaded = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var photoUrl = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync()).RootElement.GetProperty("photo_url").GetString();
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, photoUrl!, token)).StatusCode);

        var viewerEmail = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(viewerEmail, ["items.view"]);
        var viewerToken = await LoginAsync(viewerEmail);
        using var deniedMultipart = new MultipartFormDataContent();
        using var deniedContent = new ByteArrayContent(png);
        deniedContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        deniedMultipart.Add(deniedContent, "photo", "pixel.png");
        using var deniedRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/items/{itemId}/photo") { Content = deniedMultipart };
        deniedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        using var deniedUpload = await _client.SendAsync(deniedRequest);
        Assert.Equal(HttpStatusCode.Forbidden, deniedUpload.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/items/{itemId}/photo", token)).StatusCode);
    }

    [Fact]
    public async Task Item_purge_removes_private_photo_files_after_database_commit()
    {
        var token = await CreatePermittedPurgeTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Purge-photo-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, category.StatusCode);
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var code = $"PHT-{Guid.NewGuid():N}";
        using var item = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { code, name = "Purge photo item", categoryId });
        Assert.Equal(HttpStatusCode.Created, item.StatusCode);
        var itemId = JsonDocument.Parse(await item.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
        using var multipart = new MultipartFormDataContent();
        using var content = new ByteArrayContent(png);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        multipart.Add(content, "photo", "pixel.png");
        using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/items/{itemId}/photo") { Content = multipart };
        uploadRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var uploaded = await _client.SendAsync(uploadRequest);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var photoRoot = factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()
            .GetSection("Storage:ItemPhotos:RootPath").Value!;
        var photoItemRoot = Path.Combine(photoRoot, "items", itemId.ToString("N"));
        Assert.Equal(2, Directory.GetFiles(photoItemRoot, "*.jpg", SearchOption.AllDirectories).Length);

        using var trashed = await SendAsync(HttpMethod.Delete, $"/api/v1/items/{itemId}", token);
        Assert.Equal(HttpStatusCode.OK, trashed.StatusCode);
        using var purged = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/purge", token, new { confirm = code });
        Assert.Equal(HttpStatusCode.OK, purged.StatusCode);
        Assert.Empty(Directory.GetFiles(photoItemRoot, "*.jpg", SearchOption.AllDirectories));
    }

    [Fact]
    public void Item_photo_storage_does_not_read_or_delete_files_outside_its_root()
    {
        var storage = factory.Services.GetRequiredService<ItemPhotoStorage>();
        var photoRoot = factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()
            .GetSection("Storage:ItemPhotos:RootPath").Value!;
        var rootParent = Directory.GetParent(photoRoot)!.FullName;
        Directory.CreateDirectory(rootParent);
        var fileName = $"{Path.GetFileName(photoRoot)}-keep.txt";
        var markerPath = Path.Combine(rootParent, fileName);
        File.WriteAllText(markerPath, "keep");
        try
        {
            Assert.Null(storage.OpenRead($"../{fileName}"));
            storage.Delete($"../{fileName}");
            Assert.Equal("keep", File.ReadAllText(markerPath));
        }
        finally
        {
            File.Delete(markerPath);
        }
    }

    [Theory]
    [InlineData(3, 1200, 600, 320, 160)]
    [InlineData(6, 600, 1200, 160, 320)]
    [InlineData(8, 600, 1200, 160, 320)]
    public async Task Item_photo_upload_applies_jpeg_exif_rotation_before_saving(
        int orientation, int expectedFullWidth, int expectedFullHeight, int expectedThumbWidth, int expectedThumbHeight)
    {
        var token = await CreatePermittedUserTokenAsync();
        var categoryId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.Categories.Add(new Category { Id = categoryId, Name = $"Exif {categoryId:N}", NameKey = categoryId.ToString("N") });
            await database.SaveChangesAsync();
        }
        using var item = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { code = $"EXIF-{Guid.NewGuid():N}", name = "Rotated photo", categoryId });
        Assert.Equal(HttpStatusCode.Created, item.StatusCode);
        var itemId = JsonDocument.Parse(await item.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var bitmap = new SKBitmap(1600, 800);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        using var jpegStream = new MemoryStream();
        encoded.SaveTo(jpegStream);
        var jpeg = jpegStream.ToArray();
        byte[] exif = [0x45, 0x78, 0x69, 0x66, 0, 0, 0x49, 0x49, 0x2a, 0, 0x08, 0, 0, 0,
            0x01, 0, 0x12, 0x01, 0x03, 0, 0x01, 0, 0, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0];
        var jpegWithOrientation = new byte[jpeg.Length + exif.Length + 4];
        jpegWithOrientation[0] = 0xff;
        jpegWithOrientation[1] = 0xd8;
        jpegWithOrientation[2] = 0xff;
        jpegWithOrientation[3] = 0xe1;
        var segmentLength = exif.Length + 2;
        jpegWithOrientation[4] = (byte)(segmentLength >> 8);
        jpegWithOrientation[5] = (byte)segmentLength;
        Buffer.BlockCopy(exif, 0, jpegWithOrientation, 6, exif.Length);
        Buffer.BlockCopy(jpeg, 2, jpegWithOrientation, 6 + exif.Length, jpeg.Length - 2);

        using var multipart = new MultipartFormDataContent();
        using var content = new ByteArrayContent(jpegWithOrientation);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        multipart.Add(content, "photo", "rotated.jpg");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/items/{itemId}/photo") { Content = multipart };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var uploaded = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        using var uploadedJson = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
        var photoUrl = uploadedJson.RootElement.GetProperty("photo_url").GetString();
        var thumbnailUrl = uploadedJson.RootElement.GetProperty("thumb_url").GetString();
        using var response = await SendAsync(HttpMethod.Get, photoUrl!, token);
        var output = await response.Content.ReadAsByteArrayAsync();
        using var decoded = SKBitmap.Decode(output);
        Assert.NotNull(decoded);
        Assert.Equal(expectedFullWidth, decoded.Width);
        Assert.Equal(expectedFullHeight, decoded.Height);
        using var thumbnailResponse = await SendAsync(HttpMethod.Get, thumbnailUrl!, token);
        using var thumbnail = SKBitmap.Decode(await thumbnailResponse.Content.ReadAsByteArrayAsync());
        Assert.NotNull(thumbnail);
        Assert.Equal(expectedThumbWidth, thumbnail.Width);
        Assert.Equal(expectedThumbHeight, thumbnail.Height);
    }

    [Fact]
    public async Task Catalog_validation_rejects_missing_required_fields()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = "" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Item_creation_rejects_whitespace_only_name_without_persisting_item()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token,
            new { name = $"Whitespace-name-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, category.StatusCode);
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var countBefore = await database.CatalogItems.CountAsync();
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { name = "   ", categoryId });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(countBefore, await database.CatalogItems.CountAsync());
    }

    [Fact]
    public async Task Item_creation_rejects_oversized_description_and_notes_like_legacy()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Long-fields-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var oversized = new string('x', 5001);

        using var longDescription = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { name = "Description limit", categoryId, description = oversized });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, longDescription.StatusCode);

        using var longNotes = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { name = "Notes limit", categoryId, notes = oversized });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, longNotes.StatusCode);
    }

    [Fact]
    public async Task Item_update_rejects_oversized_description_and_notes_without_mutating_item()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Long-update-fields-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { name = "Text update limits", categoryId, code = $"TXT-{Guid.NewGuid():N}"[..12], description = "original description", notes = "original notes" });
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var oversized = new string('x', 5001);

        using var longDescription = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { description = oversized });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, longDescription.StatusCode);

        using var longNotes = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { notes = oversized });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, longNotes.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await database.CatalogItems.SingleAsync(item => item.Id == itemId);
        Assert.Equal("original description", persisted.Description);
        Assert.Equal("original notes", persisted.Notes);
    }

    [Fact]
    public async Task Item_update_can_clear_nullable_description_like_legacy()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Nullable-description-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { name = "Clear description", categoryId, code = $"CLR-{Guid.NewGuid():N}"[..12], description = "description to clear" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var updated = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { description = (string?)null });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var json = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("description").ValueKind);
    }

    [Fact]
    public async Task Item_update_can_clear_nullable_notes_like_legacy()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Nullable-notes-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { name = "Clear notes", categoryId, code = $"CLR-{Guid.NewGuid():N}"[..12], notes = "notes to clear" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var updated = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { notes = (string?)null });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var json = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("notes").ValueKind);
    }

    [Fact]
    public async Task Item_update_can_clear_nullable_entry_date_like_legacy()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Nullable-entry-date-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { name = "Clear entry date", categoryId, code = $"DATE-{Guid.NewGuid():N}"[..12], entryDate = "2026-10-31" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var updated = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { entryDate = (string?)null });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var json = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("entryDate").ValueKind);
    }

    [Fact]
    public async Task Item_update_resets_nullable_min_stock_to_zero_like_legacy()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Nullable-min-stock-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { name = "Reset min stock", categoryId, code = $"MIN-{Guid.NewGuid():N}"[..12], minStock = 12 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var updated = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { minStock = (int?)null });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(0, JsonDocument.Parse(await updated.Content.ReadAsStringAsync()).RootElement.GetProperty("minStock").GetInt32());
    }

    [Fact]
    public async Task Item_update_can_clear_nullable_unit_value_like_legacy()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Nullable-unit-value-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { name = "Clear unit value", categoryId, code = $"VALUE-{Guid.NewGuid():N}"[..12], unitValue = 12.34m });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var updated = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { unitValue = (decimal?)null });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(await updated.Content.ReadAsStringAsync()).RootElement.GetProperty("unitValue").ValueKind);
    }

    [Fact]
    public async Task Item_update_rejects_explicit_null_status_like_legacy()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Required-status-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { name = "Keep active", categoryId, code = $"STATUS-{Guid.NewGuid():N}"[..12] });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var updated = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { status = (string?)null });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, updated.StatusCode);
        using var current = await SendAsync(HttpMethod.Get, $"/api/v1/items/{itemId}", token);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal("ativo", JsonDocument.Parse(await current.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Item_update_rejects_explicit_null_name_like_legacy()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Required-name-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { name = "Keep this name", categoryId, code = $"NAME-{Guid.NewGuid():N}"[..12] });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var updated = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { name = (string?)null });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, updated.StatusCode);
        using var current = await SendAsync(HttpMethod.Get, $"/api/v1/items/{itemId}", token);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal("Keep this name", JsonDocument.Parse(await current.Content.ReadAsStringAsync()).RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Item_update_ignores_blank_code_like_legacy()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Optional-code-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { name = "Keep this code", categoryId, code = $"CODE-{Guid.NewGuid():N}"[..12] });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        var itemId = item.GetProperty("id").GetGuid();
        var originalCode = item.GetProperty("code").GetString();

        using var updated = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { code = "   " });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(originalCode, JsonDocument.Parse(await updated.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Category_purge_requires_trash_and_confirmation_then_audits_permanent_removal()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view", "lookups.manage", "lookups.delete", "lookups.purge", "items.view", "items.manage"]);
        var token = await LoginAsync(email);
        var name = $"Purge-{Guid.NewGuid():N}";
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var activePurge = await SendAsync(HttpMethod.Post, $"/api/v1/categories/{id}/purge", token, new { confirm = name });
        Assert.Equal(HttpStatusCode.Conflict, activePurge.StatusCode);
        using var trashed = await SendAsync(HttpMethod.Delete, $"/api/v1/categories/{id}", token);
        Assert.Equal(HttpStatusCode.OK, trashed.StatusCode);
        using var wrongConfirmation = await SendAsync(HttpMethod.Post, $"/api/v1/categories/{id}/purge", token, new { confirm = "wrong" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongConfirmation.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"/api/v1/categories/{id}", token)).StatusCode);

        using var purged = await SendAsync(HttpMethod.Post, $"/api/v1/categories/{id}/purge", token, new { confirm = name });
        Assert.Equal(HttpStatusCode.OK, purged.StatusCode);
        Assert.True(JsonDocument.Parse(await purged.Content.ReadAsStringAsync()).RootElement.GetProperty("purged").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/api/v1/categories/{id}", token)).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await database.Users.SingleAsync(value => value.Email == email);
        var audit = await database.AuditLogs.SingleAsync(log => log.Action == "purge" && log.EntityType == "category" && log.EntityId == id);
        Assert.Equal(user.Id, audit.UserId);
        using var before = JsonDocument.Parse(audit.BeforeJson!);
        Assert.Equal(name, before.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Category_purge_is_blocked_when_items_reference_it()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view", "lookups.manage", "lookups.delete", "lookups.purge", "items.view", "items.manage"]);
        var token = await LoginAsync(email);
        var name = $"Referenced-{Guid.NewGuid():N}";
        using var createdCategory = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name });
        var categoryId = JsonDocument.Parse(await createdCategory.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var createdItem = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { name = "Referenced item", categoryId, code = $"REF-{Guid.NewGuid():N}"[..12] });
        Assert.Equal(HttpStatusCode.Created, createdItem.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/categories/{categoryId}", token)).StatusCode);

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/categories/{categoryId}/purge", token, new { confirm = name });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("IN_USE", await response.Content.ReadAsStringAsync());
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await database.Categories.AnyAsync(category => category.Id == categoryId));
        Assert.Empty(await database.AuditLogs.Where(log => log.Action == "purge" && log.EntityType == "category" && log.EntityId == categoryId).ToListAsync());
    }

    [Fact]
    public async Task Category_purge_requires_lookups_purge_permission()
    {
        var ownerEmail = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(ownerEmail, ["lookups.view", "lookups.manage", "lookups.delete", "lookups.purge", "items.view", "items.manage"]);
        var ownerToken = await LoginAsync(ownerEmail);
        using var categoryResponse = await SendAsync(HttpMethod.Post, "/api/v1/categories", ownerToken, new { name = $"Restricted-purge-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await categoryResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var itemResponse = await SendAsync(HttpMethod.Post, "/api/v1/items", ownerToken,
            new { name = "Protect purge reference", categoryId, code = $"PR-{Guid.NewGuid():N}"[..12] });
        Assert.Equal(HttpStatusCode.Created, itemResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/categories/{categoryId}", ownerToken)).StatusCode);

        var readerEmail = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(readerEmail, ["lookups.view"]);
        var readerToken = await LoginAsync(readerEmail);
        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/categories/{categoryId}/purge", readerToken,
            new { confirm = $"Restricted-purge" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task User_without_catalog_permissions_is_denied()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view"]);
        var token = await LoginAsync(email);
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = "Blocked" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Creating_item_with_initial_quantity_requires_stock_entry_and_persists_entry()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Initial-stock-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var denied = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { name = "Initial stock denied", categoryId, initialQuantity = 5 });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view", "lookups.manage", "items.view", "items.manage", "stock.entry"]);
        token = await LoginAsync(email);
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { name = "Initial stock item", categoryId, initialQuantity = 5 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stock = await database.Stocks.SingleAsync(entry => entry.ItemId == itemId);
        var movement = await database.StockMovements.SingleAsync(entry => entry.ItemId == itemId);
        Assert.Equal(5, stock.OnHand);
        Assert.Equal("entrada", movement.Type);
        Assert.Equal(5, movement.Quantity);
        Assert.Equal("Saldo inicial", movement.Reason);
    }

    [Fact]
    public async Task Item_codes_follow_legacy_format_and_are_normalized_on_create_and_update()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Code-format-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var invalid = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { name = "Invalid code", categoryId, code = "BAD CODE!" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);

        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { name = "Normalized code", categoryId, code = "ab_12/x-y.z" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var itemJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var itemId = itemJson.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("AB_12/X-Y.Z", itemJson.RootElement.GetProperty("code").GetString());

        using var invalidUpdate = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { code = "BAD CODE!" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidUpdate.StatusCode);
        using var normalizedUpdate = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { code = "new/12" });
        Assert.Equal("NEW/12", JsonDocument.Parse(await normalizedUpdate.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Item_code_length_validation_returns_legacy_unprocessable_status_on_create_and_update()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Long-code-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var oversizedCode = new string('A', 41);

        using var invalidCreate = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { name = "Invalid code length", categoryId, code = oversizedCode });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidCreate.StatusCode);

        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { name = "Code update target", categoryId, code = $"CODE-{Guid.NewGuid():N}"[..12] });
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var invalidUpdate = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { code = oversizedCode });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidUpdate.StatusCode);
    }

    [Fact]
    public async Task Item_creation_rejects_categories_in_trash_like_legacy()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view", "lookups.manage", "lookups.delete", "items.view", "items.manage"]);
        var token = await LoginAsync(email);
        using var trashedCategory = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Trashed-category-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, trashedCategory.StatusCode);
        var trashedCategoryId = JsonDocument.Parse(await trashedCategory.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/categories/{trashedCategoryId}", token)).StatusCode);

        using var invalidCreate = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { name = "Item in trashed category", categoryId = trashedCategoryId, code = $"CAT-{Guid.NewGuid():N}"[..12] });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidCreate.StatusCode);
    }

    [Fact]
    public async Task Item_update_rejects_categories_in_trash_without_mutating_item()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view", "lookups.manage", "lookups.delete", "items.view", "items.manage"]);
        var token = await LoginAsync(email);
        using var activeCategory = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Active-category-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, activeCategory.StatusCode);
        var activeCategoryId = JsonDocument.Parse(await activeCategory.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var trashedCategory = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Trashed-category-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, trashedCategory.StatusCode);
        var trashedCategoryId = JsonDocument.Parse(await trashedCategory.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { name = "Existing item", categoryId = activeCategoryId, code = $"CAT-{Guid.NewGuid():N}"[..12] });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/categories/{trashedCategoryId}", token)).StatusCode);

        using var invalidUpdate = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token,
            new { categoryId = trashedCategoryId });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidUpdate.StatusCode);
        using var current = await SendAsync(HttpMethod.Get, $"/api/v1/items/{itemId}", token);
        Assert.Equal(activeCategoryId, JsonDocument.Parse(await current.Content.ReadAsStringAsync()).RootElement.GetProperty("categoryId").GetGuid());
    }

    [Fact]
    public async Task Item_update_rejects_explicit_null_category_like_legacy()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Required-category-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { name = "Keep category", categoryId, code = $"CAT-{Guid.NewGuid():N}"[..12] });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var invalidUpdate = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { categoryId = (Guid?)null });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidUpdate.StatusCode);
        using var current = await SendAsync(HttpMethod.Get, $"/api/v1/items/{itemId}", token);
        Assert.Equal(categoryId, JsonDocument.Parse(await current.Content.ReadAsStringAsync()).RootElement.GetProperty("categoryId").GetGuid());
    }

    [Fact]
    public async Task Item_creation_rejects_locations_and_suppliers_in_trash_like_legacy()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view", "lookups.manage", "lookups.delete", "items.view", "items.manage"]);
        var token = await LoginAsync(email);
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Lookup-category-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var location = await SendAsync(HttpMethod.Post, "/api/v1/locations", token, new { name = $"Trashed-location-{Guid.NewGuid():N}", kind = "cd" });
        Assert.Equal(HttpStatusCode.Created, location.StatusCode);
        var locationId = JsonDocument.Parse(await location.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var supplier = await SendAsync(HttpMethod.Post, "/api/v1/suppliers", token, new { name = $"Trashed-supplier-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, supplier.StatusCode);
        var supplierId = JsonDocument.Parse(await supplier.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/locations/{locationId}", token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/suppliers/{supplierId}", token)).StatusCode);

        using var invalidLocation = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { name = "Trashed location reference", categoryId, locationId });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidLocation.StatusCode);
        using var invalidSupplier = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { name = "Trashed supplier reference", categoryId, supplierId });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidSupplier.StatusCode);
    }

    [Fact]
    public async Task Item_update_rejects_locations_and_suppliers_in_trash_without_mutation()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view", "lookups.manage", "lookups.delete", "items.view", "items.manage"]);
        var token = await LoginAsync(email);
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Lookup-category-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var location = await SendAsync(HttpMethod.Post, "/api/v1/locations", token, new { name = $"Trashed-location-{Guid.NewGuid():N}", kind = "cd" });
        Assert.Equal(HttpStatusCode.Created, location.StatusCode);
        var locationId = JsonDocument.Parse(await location.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var supplier = await SendAsync(HttpMethod.Post, "/api/v1/suppliers", token, new { name = $"Trashed-supplier-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, supplier.StatusCode);
        var supplierId = JsonDocument.Parse(await supplier.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var item = await SendAsync(HttpMethod.Post, "/api/v1/items", token,
            new { name = "Lookup update target", categoryId, code = $"LOOK-{Guid.NewGuid():N}"[..12] });
        Assert.Equal(HttpStatusCode.Created, item.StatusCode);
        var itemId = JsonDocument.Parse(await item.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/locations/{locationId}", token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/suppliers/{supplierId}", token)).StatusCode);

        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { locationId })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { supplierId })).StatusCode);
        using var current = await SendAsync(HttpMethod.Get, $"/api/v1/items/{itemId}", token);
        using var currentJson = JsonDocument.Parse(await current.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, currentJson.RootElement.GetProperty("locationId").ValueKind);
        Assert.Equal(JsonValueKind.Null, currentJson.RootElement.GetProperty("supplierId").ValueKind);
    }

    [Fact]
    public async Task Item_creation_keeps_invalid_json_as_bad_request()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/items")
        {
            Content = new StringContent("{", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Item_creation_audits_actor_and_created_snapshot_with_initial_stock()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view", "lookups.manage", "items.view", "items.manage", "stock.entry"]);
        var token = await LoginAsync(email);
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Audit-item-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        const string code = "AUDIT-ITEM-01";
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { name = "Audited catalog item", categoryId, code, initialQuantity = 4 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var actor = await database.Users.SingleAsync(user => user.Email == email);
        var audit = await database.AuditLogs.SingleAsync(log => log.EntityType == "item" && log.EntityId == itemId && log.Action == "create");
        Assert.Equal(actor.Id, audit.UserId);
        Assert.Equal("Catalog test", audit.UserName);
        Assert.Equal("AUDIT-ITEM-01 - Audited catalog item", audit.EntityLabel);
        Assert.Null(audit.BeforeJson);
        using var after = JsonDocument.Parse(audit.AfterJson!);
        Assert.Equal(code, after.RootElement.GetProperty("code").GetString());
        Assert.Equal(4, after.RootElement.GetProperty("initial_quantity").GetInt32());
        var movement = await database.StockMovements.SingleAsync(entry => entry.ItemId == itemId);
        Assert.Equal(actor.Id, movement.UserId);
    }

    [Fact]
    public async Task Item_history_combines_movements_and_audits_with_legacy_pagination()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view", "lookups.manage", "items.view", "items.manage", "stock.entry"]);
        var token = await LoginAsync(email);
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"History-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new
        {
            code = $"HISTORY-{Guid.NewGuid():N}"[..20],
            name = "History before update",
            categoryId,
            initialQuantity = 3
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var updated = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { name = "History after update" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.AuditLogs.Add(new AuditLog
            {
                Action = "stock_entrada",
                EntityType = "item",
                EntityId = itemId,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await database.SaveChangesAsync();
        }

        using var firstPage = await SendAsync(HttpMethod.Get, $"/api/v1/items/{itemId}/history?page=1&per_page=2", token);
        Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
        using var firstPageJson = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
        var firstData = firstPageJson.RootElement.GetProperty("data");
        Assert.Equal(2, firstData.GetArrayLength());
        Assert.Equal(3, firstPageJson.RootElement.GetProperty("meta").GetProperty("total").GetInt32());
        Assert.Equal(2, firstPageJson.RootElement.GetProperty("meta").GetProperty("last_page").GetInt32());
        Assert.Equal("update", firstData[0].GetProperty("action").GetString());
        Assert.Equal("Alteração", firstData[0].GetProperty("action_label").GetString());
        Assert.True(DateTimeOffset.Parse(firstData[0].GetProperty("at").GetString()!) >=
            DateTimeOffset.Parse(firstData[1].GetProperty("at").GetString()!));

        using var secondPage = await SendAsync(HttpMethod.Get, $"/api/v1/items/{itemId}/history?page=2&per_page=2", token);
        Assert.Equal(HttpStatusCode.OK, secondPage.StatusCode);
        using var secondPageJson = JsonDocument.Parse(await secondPage.Content.ReadAsStringAsync());
        Assert.Single(secondPageJson.RootElement.GetProperty("data").EnumerateArray());

        using var defaultPage = await SendAsync(HttpMethod.Get, $"/api/v1/items/{itemId}/history?per_page=0", token);
        Assert.Equal(HttpStatusCode.OK, defaultPage.StatusCode);
        using var defaultPageJson = JsonDocument.Parse(await defaultPage.Content.ReadAsStringAsync());
        Assert.Equal(30, defaultPageJson.RootElement.GetProperty("meta").GetProperty("per_page").GetInt32());
        Assert.Equal(3, defaultPageJson.RootElement.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task Item_update_audits_actor_and_before_after_snapshots()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view", "lookups.manage", "items.view", "items.manage"]);
        var token = await LoginAsync(email);
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Audit-update-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { name = "Before update", categoryId, code = "AUDIT-UPDATE-01" });
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var updated = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { name = "After update" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var actor = await database.Users.SingleAsync(user => user.Email == email);
        var audit = await database.AuditLogs.SingleAsync(log => log.EntityType == "item" && log.EntityId == itemId && log.Action == "update");
        Assert.Equal(actor.Id, audit.UserId);
        using var before = JsonDocument.Parse(audit.BeforeJson!);
        using var after = JsonDocument.Parse(audit.AfterJson!);
        Assert.Equal("Before update", before.RootElement.GetProperty("name").GetString());
        Assert.Equal("After update", after.RootElement.GetProperty("name").GetString());
        Assert.False(before.RootElement.TryGetProperty("code", out _));
        Assert.False(after.RootElement.TryGetProperty("code", out _));

        using var noOp = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { name = "After update" });
        Assert.Equal(HttpStatusCode.OK, noOp.StatusCode);
        Assert.Equal(1, await database.AuditLogs.CountAsync(log => log.EntityType == "item" && log.EntityId == itemId && log.Action == "update"));
    }

    [Fact]
    public async Task Item_deactivation_with_reservations_is_rejected_without_audit()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Reserved-deactivate-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { name = "Reserved item", categoryId });
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        await using (var seedScope = factory.Services.CreateAsyncScope())
        {
            var database = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var item = await database.CatalogItems.SingleAsync(entry => entry.Id == itemId);
            database.Stocks.Add(new InventoryStock { ItemId = itemId, Item = item, OnHand = 3, Reserved = 1 });
            await database.SaveChangesAsync();
        }

        using var deactivated = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/deactivate", token);
        Assert.Equal(HttpStatusCode.Conflict, deactivated.StatusCode);
        Assert.Contains("ITEM_HAS_RESERVATIONS", await deactivated.Content.ReadAsStringAsync());

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal("ativo", (await verifyDatabase.CatalogItems.SingleAsync(item => item.Id == itemId)).Status);
        Assert.False(await verifyDatabase.AuditLogs.AnyAsync(log => log.EntityId == itemId && log.Action == "deactivate"));
    }

    [Fact]
    public async Task Item_update_cannot_set_inactive_while_stock_is_reserved()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Reserved-update-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { name = "Reserved via update", categoryId });
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        await using (var seedScope = factory.Services.CreateAsyncScope())
        {
            var database = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var item = await database.CatalogItems.SingleAsync(entry => entry.Id == itemId);
            database.Stocks.Add(new InventoryStock { ItemId = itemId, Item = item, OnHand = 2, Reserved = 1 });
            await database.SaveChangesAsync();
        }

        using var updated = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { status = "inativo" });
        Assert.Equal(HttpStatusCode.Conflict, updated.StatusCode);
        Assert.Contains("ITEM_HAS_RESERVATIONS", await updated.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Item_activation_records_status_change_with_actor()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Audit-activate-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { name = "Inactive item", categoryId, status = "inativo" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var activated = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/activate", token);
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var audit = await database.AuditLogs.SingleAsync(log => log.EntityType == "item" && log.EntityId == itemId && log.Action == "activate");
        Assert.Equal("Catalog test", audit.UserName);
        using var before = JsonDocument.Parse(audit.BeforeJson!);
        using var after = JsonDocument.Parse(audit.AfterJson!);
        Assert.Equal("inativo", before.RootElement.GetProperty("status").GetString());
        Assert.Equal("ativo", after.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Categories_support_trash_restore_and_filtered_listing()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = "Trash test" });
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var deniedDelete = await SendAsync(HttpMethod.Delete, $"/api/v1/categories/{id}", token);
        Assert.Equal(HttpStatusCode.Forbidden, deniedDelete.StatusCode);

        token = await CreatePermittedDeleteTokenAsync();
        using var deleted = await SendAsync(HttpMethod.Delete, $"/api/v1/categories/{id}", token);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        using var absent = await SendAsync(HttpMethod.Get, "/api/v1/categories", token);
        Assert.DoesNotContain(id.ToString(), await absent.Content.ReadAsStringAsync());
        using var trash = await SendAsync(HttpMethod.Get, "/api/v1/categories?trashed=1", token);
        Assert.Contains(id.ToString(), await trash.Content.ReadAsStringAsync());
        using var get = await SendAsync(HttpMethod.Get, $"/api/v1/categories/{id}", token);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using var blockedUpdate = await SendAsync(HttpMethod.Put, $"/api/v1/categories/{id}", token, new { description = "x" });
        Assert.Equal(HttpStatusCode.NotFound, blockedUpdate.StatusCode);
        using var restored = await SendAsync(HttpMethod.Post, $"/api/v1/categories/{id}/restore", token);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        using var duplicate = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = "TRASH TEST" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        using var all = await SendAsync(HttpMethod.Get, "/api/v1/categories?all=1&per_page=1", token);
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
    }

    [Fact]
    public async Task Categories_all_selector_overrides_filters_and_sort_supports_descending()
    {
        var token = await CreatePermittedUserTokenAsync();
        var marker = Guid.NewGuid().ToString("N")[..8];
        using var earlier = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Category-{marker}-B" });
        using var later = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Category-{marker}-A" });
        var laterId = JsonDocument.Parse(await later.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var earlierId = JsonDocument.Parse(await earlier.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var sorted = await SendAsync(HttpMethod.Get, $"/api/v1/categories?q={marker}&sort=-name&per_page=1", token);
        using var sortedJson = JsonDocument.Parse(await sorted.Content.ReadAsStringAsync());
        Assert.Equal(earlierId, sortedJson.RootElement.GetProperty("data")[0].GetProperty("id").GetGuid());

        using var all = await SendAsync(HttpMethod.Get, "/api/v1/categories?all=1&q=no-match&active=0&per_page=1", token);
        using var allJson = JsonDocument.Parse(await all.Content.ReadAsStringAsync());
        Assert.Contains(allJson.RootElement.GetProperty("data").EnumerateArray(), category => category.GetProperty("id").GetGuid() == laterId);
        Assert.Contains(allJson.RootElement.GetProperty("data").EnumerateArray(), category => category.GetProperty("id").GetGuid() == earlierId);
    }

    [Fact]
    public async Task Deleted_category_name_can_be_reused()
    {
        var token = await CreatePermittedDeleteTokenAsync();
        const string name = "Reusable after trash";
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name });
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/categories/{id}", token)).StatusCode);

        using var recreated = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = name.ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.Created, recreated.StatusCode);
    }

    [Fact]
    public async Task Category_referenced_by_an_item_can_be_soft_deleted()
    {
        var itemToken = await CreatePermittedUserTokenAsync();
        using var categoryResponse = await SendAsync(HttpMethod.Post, "/api/v1/categories", itemToken, new { name = $"Referenced-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await categoryResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var itemResponse = await SendAsync(HttpMethod.Post, "/api/v1/items", itemToken, new { name = "Category reference test", categoryId });
        Assert.Equal(HttpStatusCode.Created, itemResponse.StatusCode);

        var deleteToken = await CreatePermittedDeleteTokenAsync();
        using var deleted = await SendAsync(HttpMethod.Delete, $"/api/v1/categories/{categoryId}", deleteToken);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
    }

    [Fact]
    public async Task Items_accept_valid_location_and_supplier_and_return_related_lookup_data()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var loc = await SendAsync(HttpMethod.Post, "/api/v1/locations", token, new { name = $"Store-{Guid.NewGuid():N}", kind = "cd" });
        using var sup = await SendAsync(HttpMethod.Post, "/api/v1/suppliers", token, new { name = $"Vendor-{Guid.NewGuid():N}" });
        var locationId = JsonDocument.Parse(await loc.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var supplierId = JsonDocument.Parse(await sup.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Association-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { name = "Associated item", categoryId, locationId, supplierId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(locationId, json.RootElement.GetProperty("locationId").GetGuid());
        Assert.Equal(supplierId, json.RootElement.GetProperty("supplierId").GetGuid());
        Assert.Equal("cd", json.RootElement.GetProperty("location").GetProperty("kind").GetString());
        Assert.StartsWith("Vendor-", json.RootElement.GetProperty("supplier").GetProperty("name").GetString());
        using var get = await SendAsync(HttpMethod.Get, $"/api/v1/items/{json.RootElement.GetProperty("id").GetGuid()}", token);
        Assert.Equal(locationId, JsonDocument.Parse(await get.Content.ReadAsStringAsync()).RootElement.GetProperty("location").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Item_associations_validate_references_filter_and_allow_explicit_clear()
    {
        var token = await CreatePermittedUserTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Filter-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var invalid = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { name = "Invalid association", categoryId, locationId = Guid.NewGuid(), supplierId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        using var invalidSupplier = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { name = "Invalid supplier", categoryId, supplierId = Guid.NewGuid() });
        using var invalidSupplierJson = JsonDocument.Parse(await invalidSupplier.Content.ReadAsStringAsync());
        Assert.True(invalidSupplierJson.RootElement.GetProperty("errors").TryGetProperty("supplierId", out _));
        using var location = await SendAsync(HttpMethod.Post, "/api/v1/locations", token, new { name = $"Store-{Guid.NewGuid():N}", kind = "cd" });
        var locationId = JsonDocument.Parse(await location.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var supplier = await SendAsync(HttpMethod.Post, "/api/v1/suppliers", token, new { name = $"Vendor-{Guid.NewGuid():N}" });
        var supplierId = JsonDocument.Parse(await supplier.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { name = "Filter target", categoryId, locationId, supplierId });
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using var filtered = await SendAsync(HttpMethod.Get, $"/api/v1/items?location_id={locationId}&supplier_id={supplierId}", token);
        Assert.Contains(JsonDocument.Parse(await filtered.Content.ReadAsStringAsync()).RootElement.GetProperty("data").EnumerateArray(), item => item.GetProperty("id").GetGuid() == itemId);
        using var update = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { name = "Still associated" });
        using var unchanged = JsonDocument.Parse(await update.Content.ReadAsStringAsync());
        Assert.Equal(locationId, unchanged.RootElement.GetProperty("locationId").GetGuid());
        Assert.Equal(supplierId, unchanged.RootElement.GetProperty("supplierId").GetGuid());
        using var invalidUpdate = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { locationId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidUpdate.StatusCode);
        using var malformedUpdate = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { locationId = "not-a-guid" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, malformedUpdate.StatusCode);
        using var clearLocation = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { locationId = (Guid?)null });
        using var locationClearedJson = JsonDocument.Parse(await clearLocation.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, locationClearedJson.RootElement.GetProperty("locationId").ValueKind);
        Assert.Equal(supplierId, locationClearedJson.RootElement.GetProperty("supplierId").GetGuid());
        using var cleared = await SendAsync(HttpMethod.Put, $"/api/v1/items/{itemId}", token, new { supplierId = (Guid?)null });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        using var clearedJson = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, clearedJson.RootElement.GetProperty("locationId").ValueKind);
        Assert.Equal(JsonValueKind.Null, clearedJson.RootElement.GetProperty("supplierId").ValueKind);
    }

    [Fact]
    public async Task Item_purge_requires_trash_and_typed_code_and_retains_audit()
    {
        var token = await CreatePermittedPurgeTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Purge-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var code = $"PURGE-{Guid.NewGuid():N}";
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { name = "Purge test item", categoryId, code });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        using var activePurge = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/purge", token, new { confirm = code });
        Assert.Equal(HttpStatusCode.Conflict, activePurge.StatusCode);
        Assert.Contains("NOT_IN_TRASH", await activePurge.Content.ReadAsStringAsync());

        using var trashed = await SendAsync(HttpMethod.Delete, $"/api/v1/items/{itemId}", token);
        Assert.Equal(HttpStatusCode.OK, trashed.StatusCode);
        using var wrongConfirmation = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/purge", token, new { confirm = "wrong-code" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongConfirmation.StatusCode);

        using var purged = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/purge", token, new { confirm = code.ToLowerInvariant() });
        Assert.Equal(HttpStatusCode.OK, purged.StatusCode);
        Assert.Contains("\"purged\":true", await purged.Content.ReadAsStringAsync());
        using var missing = await SendAsync(HttpMethod.Get, $"/api/v1/items/{itemId}", token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var audit = await database.AuditLogs.SingleAsync(log => log.EntityType == "item" && log.EntityId == itemId && log.Action == "purge");
        Assert.Equal("purge", audit.Action);
        Assert.Contains(code, audit.BeforeJson, StringComparison.OrdinalIgnoreCase);
        Assert.Null(audit.AfterJson);
    }

    [Fact]
    public async Task Item_purge_requires_dedicated_permission()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view", "lookups.manage", "items.view", "items.manage", "items.delete"]);
        var token = await LoginAsync(email);
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"NoPurge-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var code = $"NO-PURGE-{Guid.NewGuid():N}"[..40];
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { name = "Protected item", categoryId, code });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/items/{itemId}", token)).StatusCode);

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/purge", token, new { confirm = code });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Item_purge_is_blocked_when_stock_history_references_the_item()
    {
        var token = await CreatePermittedPurgeTokenAsync();
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = $"Used-{Guid.NewGuid():N}" });
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var code = $"USED-{Guid.NewGuid():N}";
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { name = "Used item", categoryId, code });
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            database.StockMovements.Add(new StockMovement { ItemId = itemId, Type = "entry", Quantity = 1, BalanceAfter = 1 });
            await database.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/items/{itemId}", token)).StatusCode);

        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/purge", token, new { confirm = code });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("IN_USE", await response.Content.ReadAsStringAsync());
        using var stillThere = await SendAsync(HttpMethod.Get, $"/api/v1/items/{itemId}", token);
        Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDatabase = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await verifyDatabase.AuditLogs.Where(log => log.EntityType == "item" && log.EntityId == itemId && log.Action == "purge").ToListAsync());
    }

    [Fact]
    public async Task Item_purge_is_blocked_when_event_allocations_reference_the_item()
    {
        var token = await CreatePermittedPurgeTokenAsync();
        var categoryName = $"Event-purge-{Guid.NewGuid():N}";
        using var category = await SendAsync(HttpMethod.Post, "/api/v1/categories", token, new { name = categoryName });
        Assert.Equal(HttpStatusCode.Created, category.StatusCode);
        var categoryId = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var code = $"EVTP-{Guid.NewGuid():N}"[..20];
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/items", token, new { name = "Event referenced item", categoryId, code });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var eventId = Guid.NewGuid();
        var industryId = Guid.NewGuid();

        try
        {
            await using (var seedScope = factory.Services.CreateAsyncScope())
            {
                var database = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                database.Events.Add(new Event { Id = eventId, Name = $"Reference event {eventId:N}" });
                database.Industries.Add(new Industry { Id = industryId, Name = $"Reference industry {industryId:N}", NameKey = industryId.ToString("N") });
                database.EventAllocations.Add(new EventAllocation
                {
                    EventId = eventId,
                    IndustryId = industryId,
                    ItemId = itemId,
                    QuantityAllocated = 4
                });
                await database.SaveChangesAsync();
            }

            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"/api/v1/items/{itemId}", token)).StatusCode);
            using var response = await SendAsync(HttpMethod.Post, $"/api/v1/items/{itemId}/purge", token, new { confirm = code });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var responseJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("IN_USE", responseJson.RootElement.GetProperty("error").GetString());
            Assert.Contains("cotas de eventos", responseJson.RootElement.GetProperty("uses").EnumerateArray().Select(value => value.GetString()));
            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Empty(await verify.AuditLogs.Where(log => log.EntityType == "item" && log.EntityId == itemId && log.Action == "purge").ToListAsync());
        }
        finally
        {
            await using var cleanupScope = factory.Services.CreateAsyncScope();
            var database = cleanupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var eventRecord = await database.Events.FindAsync(eventId);
            if (eventRecord is not null)
                database.Events.Remove(eventRecord);
            var industry = await database.Industries.FindAsync(industryId);
            if (industry is not null)
                database.Industries.Remove(industry);
            var item = await database.CatalogItems.FindAsync(itemId);
            if (item is not null)
                database.CatalogItems.Remove(item);
            var categoryRecord = await database.Categories.FindAsync(categoryId);
            if (categoryRecord is not null)
                database.Categories.Remove(categoryRecord);
            await database.SaveChangesAsync();
        }
    }

    private async Task<string> CreatePermittedDeleteTokenAsync()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view", "lookups.manage", "lookups.delete"]);
        return await LoginAsync(email);
    }

    private async Task<string> CreatePermittedPurgeTokenAsync()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view", "lookups.manage", "items.view", "items.manage", "items.delete", "items.purge"]);
        return await LoginAsync(email);
    }

    private async Task<string> CreatePermittedUserTokenAsync()
    {
        var email = $"{Guid.NewGuid():N}@example.invalid";
        await CreateUserAsync(email, ["lookups.view", "lookups.manage", "items.view", "items.manage"]);
        return await LoginAsync(email);
    }

    private async Task CreateUserAsync(string email, string[] permissions)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = new ApplicationRole($"role-{Guid.NewGuid():N}");
        Assert.True((await roleManager.CreateAsync(role)).Succeeded);
        foreach (var permission in permissions)
            Assert.True((await roleManager.AddClaimAsync(role, new System.Security.Claims.Claim("permission", permission))).Succeeded);
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Catalog test" };
        Assert.True((await userManager.CreateAsync(user, "CorrectPassword1")).Succeeded);
        Assert.True((await userManager.AddToRoleAsync(user, role.Name!)).Succeeded);
    }

    private async Task<string> LoginAsync(string email)
    {
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
