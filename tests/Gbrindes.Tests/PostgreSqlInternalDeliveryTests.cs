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
using SkiaSharp;

namespace Gbrindes.Tests;

[Collection("PostgreSQL integration")]
public sealed class PostgreSqlInternalDeliveryTests(PostgreSqlWebApplicationFactory factory)
    : IClassFixture<PostgreSqlWebApplicationFactory>
{
    [PostgreSqlFact]
    public async Task Concurrent_internal_delivery_replay_consumes_reserved_stock_once_in_postgres()
    {
        using var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@example.invalid";
        var roleName = $"postgres-internal-delivery-{Guid.NewGuid():N}";
        var categoryId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        Guid? userId = null;
        Guid? requestId = null;
        string? signaturePath = null;

        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var services = scope.ServiceProvider;
                var database = services.GetRequiredService<ApplicationDbContext>();
                Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", database.Database.ProviderName);
                database.Categories.Add(new Category
                {
                    Id = categoryId,
                    Name = $"Delivery category {categoryId:N}",
                    NameKey = categoryId.ToString("N")
                });
                database.Locations.Add(new Location
                {
                    Id = locationId,
                    Name = $"Delivery location {locationId:N}",
                    NameKey = locationId.ToString("N"),
                    Kind = "outro",
                    Active = true
                });
                database.CatalogItems.Add(new CatalogItem
                {
                    Id = itemId,
                    Code = $"ID-{itemId:N}"[..12],
                    Name = "PostgreSQL internal delivery item",
                    CategoryId = categoryId,
                    LocationId = locationId,
                    UnitValue = 3.25m
                });
                database.Stocks.Add(new InventoryStock { ItemId = itemId, OnHand = 3 });
                database.StockPositions.Add(new StockPosition { ItemId = itemId, LocationId = locationId, Quantity = 3 });
                var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
                var role = new ApplicationRole(roleName);
                Assert.True((await roles.CreateAsync(role)).Succeeded);
                foreach (var permission in new[] { "requests.create", "requests.view_own", "requests.process", "deliveries.view" })
                    Assert.True((await roles.AddClaimAsync(role, new Claim("permission", permission))).Succeeded);
                var user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    FullName = "PostgreSQL delivery operator"
                };
                Assert.True((await services.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(user, "CorrectPassword1")).Succeeded);
                userId = user.Id;
                Assert.True((await services.GetRequiredService<UserManager<ApplicationUser>>().AddToRoleAsync(user, roleName)).Succeeded);
                await database.SaveChangesAsync();
            }

            var token = await LoginAsync(client, email);
            using (var created = await SendAsync(client, HttpMethod.Post, "/api/v1/requests", token, new
            {
                purpose = "PostgreSQL internal delivery",
                items = new[] { new { item_id = itemId, qty_requested = 2 } }
            }))
            {
                var body = await created.Content.ReadAsStringAsync();
                Assert.True(created.StatusCode == HttpStatusCode.Created, body);
                using var json = JsonDocument.Parse(body);
                requestId = json.RootElement.GetProperty("data").GetProperty("id").GetGuid();
            }

            using (var submitted = await SendAsync(client, HttpMethod.Post, $"/api/v1/requests/{requestId}/submit", token))
                Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
            using (var started = await SendAsync(client, HttpMethod.Post, $"/api/v1/requests/{requestId}/start-picking", token))
                Assert.Equal(HttpStatusCode.OK, started.StatusCode);
            using (var ready = await SendAsync(client, HttpMethod.Post, $"/api/v1/requests/{requestId}/ready", token))
                Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

            var signature = CreateSignatureDataUri();
            int notificationCountBefore;
            await using (var notificationScope = factory.Services.CreateAsyncScope())
            {
                var notificationDatabase = notificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                notificationCountBefore = await notificationDatabase.Notifications.CountAsync(value => value.RelatedId == requestId);
            }
            await using (var failureSetupScope = factory.Services.CreateAsyncScope())
            {
                var failureSetup = failureSetupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await failureSetup.Database.ExecuteSqlRawAsync("ALTER TABLE \"AuditLogs\" DROP CONSTRAINT IF EXISTS \"CK_TestRejectInternalDelivery\"");
                await failureSetup.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"AuditLogs\" ADD CONSTRAINT \"CK_TestRejectInternalDelivery\" CHECK (\"Action\" <> 'deliver') NOT VALID");
            }
            try
            {
                using var failedDelivery = await SendDeliveryAsync(client, requestId.Value, token,
                    $"pg-internal-delivery-fail-{Guid.NewGuid():N}", signature);
                Assert.Equal(HttpStatusCode.InternalServerError, failedDelivery.StatusCode);
            }
            finally
            {
                await using var failureCleanupScope = factory.Services.CreateAsyncScope();
                var failureCleanup = failureCleanupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await failureCleanup.Database.ExecuteSqlRawAsync("ALTER TABLE \"AuditLogs\" DROP CONSTRAINT IF EXISTS \"CK_TestRejectInternalDelivery\"");
            }

            await using (var rollbackScope = factory.Services.CreateAsyncScope())
            {
                var rollback = rollbackScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var unchanged = await rollback.Requests.Include(value => value.Items).SingleAsync(value => value.Id == requestId);
                Assert.Equal("pronta", unchanged.Status);
                Assert.All(unchanged.Items, item => Assert.Equal(0, item.QuantityDelivered));
                var unchangedStock = await rollback.Stocks.SingleAsync(value => value.ItemId == itemId);
                Assert.Equal(3, unchangedStock.OnHand);
                Assert.Equal(2, unchangedStock.Reserved);
                Assert.Equal(3, await rollback.StockPositions.Where(value => value.ItemId == itemId).Select(value => value.Quantity).SingleAsync());
                Assert.Empty(await rollback.Deliveries.Where(value => value.RequestId == requestId).ToListAsync());
                Assert.Empty(await rollback.StockMovements.Where(value => value.ItemId == itemId && value.DeliveryId != null).ToListAsync());
                Assert.Empty(await rollback.AuditLogs.Where(value => value.EntityType == "delivery"
                    && value.Action == "deliver" && value.UserId == userId).ToListAsync());
                Assert.DoesNotContain(await rollback.RequestStatusHistory.Where(value => value.RequestId == requestId).ToListAsync(),
                    history => history.ToStatus == "finalizada");
                Assert.Equal(notificationCountBefore, await rollback.Notifications.CountAsync(value => value.RelatedId == requestId));
            }
            Assert.False(Directory.Exists(factory.SignatureRoot)
                && Directory.EnumerateFiles(factory.SignatureRoot, "*.png", SearchOption.AllDirectories).Any());

            var key = $"pg-internal-delivery-{Guid.NewGuid():N}";
            using var firstRequest = CreateDeliveryRequest(requestId.Value, token, key, signature);
            using var secondRequest = CreateDeliveryRequest(requestId.Value, token, key, signature);
            var responses = await Task.WhenAll(client.SendAsync(firstRequest), client.SendAsync(secondRequest));
            try
            {
                Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.Created);
                Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.OK);
                var payloads = await Task.WhenAll(responses.Select(async response =>
                {
                    var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    return (json.RootElement.GetProperty("data").GetProperty("id").GetGuid(),
                        json.RootElement.GetProperty("data").GetProperty("code").GetString());
                }));
                Assert.Single(payloads.Select(payload => payload.Item1).Distinct());
                Assert.Single(payloads.Select(payload => payload.Item2).Distinct());

                await using var verifyScope = factory.Services.CreateAsyncScope();
                var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var deliveryId = payloads[0].Item1;
                var delivery = await verify.Deliveries.Include(value => value.Items).SingleAsync(value => value.Id == deliveryId);
                signaturePath = delivery.SignaturePath;
                Assert.Equal(requestId, delivery.RequestId);
                Assert.Equal(userId, delivery.DeliveredById);
                Assert.Equal(2, Assert.Single(delivery.Items).Quantity);
                Assert.True(File.Exists(Path.Combine(factory.SignatureRoot, signaturePath.Replace('/', Path.DirectorySeparatorChar))));

                var request = await verify.Requests.Include(value => value.Items).Include(value => value.History)
                    .SingleAsync(value => value.Id == requestId);
                Assert.Equal("finalizada", request.Status);
                Assert.Equal(2, Assert.Single(request.Items).QuantityDelivered);
                Assert.Equal(1, (await verify.Stocks.SingleAsync(value => value.ItemId == itemId)).OnHand);
                Assert.Equal(0, (await verify.Stocks.SingleAsync(value => value.ItemId == itemId)).Reserved);
                Assert.Equal(1, (await verify.StockPositions.SingleAsync(value => value.ItemId == itemId && value.LocationId == locationId)).Quantity);
                var movement = await verify.StockMovements.SingleAsync(value => value.DeliveryId == deliveryId);
                Assert.Equal(-2, movement.Quantity);
                Assert.Equal(1, movement.BalanceAfter);
                Assert.Equal(userId, movement.UserId);
                Assert.Equal(1, await verify.Deliveries.CountAsync(value => value.RequestId == requestId));
                Assert.Equal(1, await verify.StockMovements.CountAsync(value => value.DeliveryId == deliveryId));
                Assert.Equal(1, await verify.AuditLogs.CountAsync(value => value.EntityType == "delivery"
                    && value.EntityId == deliveryId && value.Action == "deliver"));
                Assert.Contains(request.History, history => history.FromStatus == "pronta" && history.ToStatus == "finalizada");

                using var pdfResponse = await SendAsync(client, HttpMethod.Get, $"/api/v1/deliveries/{deliveryId}/pdf", token);
                Assert.Equal(HttpStatusCode.OK, pdfResponse.StatusCode);
                Assert.Equal("application/pdf", pdfResponse.Content.Headers.ContentType?.MediaType);
                Assert.True(pdfResponse.Headers.CacheControl?.Private);
                Assert.True(pdfResponse.Headers.CacheControl?.NoStore);
                var pdfBytes = await pdfResponse.Content.ReadAsByteArrayAsync();
                Assert.True(pdfBytes.Length > 8);
                Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdfBytes, 0, 5));
            }
            finally
            {
                foreach (var response in responses)
                    response.Dispose();
            }
        }
        finally
        {
            await using var cleanupScope = factory.Services.CreateAsyncScope();
            var services = cleanupScope.ServiceProvider;
            var database = services.GetRequiredService<ApplicationDbContext>();
            var deliveries = requestId is Guid id
                ? await database.Deliveries.Where(value => value.RequestId == id).ToListAsync()
                : [];
            var deliveryIds = deliveries.Select(value => value.Id).ToArray();
            database.AuditLogs.RemoveRange(await database.AuditLogs.Where(value =>
                (value.EntityType == "request" && value.EntityId == requestId)
                || (value.EntityType == "delivery" && value.EntityId != null && deliveryIds.Contains(value.EntityId.Value))).ToListAsync());
            if (requestId is Guid notificationRequestId)
                database.Notifications.RemoveRange(await database.Notifications.Where(value => value.RelatedId == notificationRequestId).ToListAsync());
            if (itemId != Guid.Empty)
                database.StockMovements.RemoveRange(await database.StockMovements.Where(value => value.ItemId == itemId).ToListAsync());
            database.Deliveries.RemoveRange(deliveries);
            await database.SaveChangesAsync();
            if (requestId is Guid requestKey)
            {
                database.RequestItems.RemoveRange(await database.RequestItems.Where(value => value.RequestId == requestKey).ToListAsync());
                database.RequestStatusHistory.RemoveRange(await database.RequestStatusHistory.Where(value => value.RequestId == requestKey).ToListAsync());
                database.RequestApprovals.RemoveRange(await database.RequestApprovals.Where(value => value.RequestId == requestKey).ToListAsync());
                await database.SaveChangesAsync();
                var request = await database.Requests.SingleOrDefaultAsync(value => value.Id == requestKey);
                if (request is not null)
                {
                    database.Requests.Remove(request);
                    await database.SaveChangesAsync();
                }
            }
            database.StockPositions.RemoveRange(await database.StockPositions.Where(value => value.ItemId == itemId).ToListAsync());
            var stock = await database.Stocks.SingleOrDefaultAsync(value => value.ItemId == itemId);
            if (stock is not null)
                database.Stocks.Remove(stock);
            var item = await database.CatalogItems.SingleOrDefaultAsync(value => value.Id == itemId);
            if (item is not null)
                database.CatalogItems.Remove(item);
            var category = await database.Categories.SingleOrDefaultAsync(value => value.Id == categoryId);
            if (category is not null)
                database.Categories.Remove(category);
            var location = await database.Locations.SingleOrDefaultAsync(value => value.Id == locationId);
            if (location is not null)
                database.Locations.Remove(location);
            await database.SaveChangesAsync();

            if (signaturePath is not null)
                services.GetRequiredService<DigitalSignatureStorage>().Delete(signaturePath);
            if (userId is Guid userKey)
            {
                var users = services.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.FindByIdAsync(userKey.ToString());
                if (user is not null)
                    Assert.True((await users.DeleteAsync(user)).Succeeded);
            }
            var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();
            if (await roles.FindByNameAsync(roleName) is { } role)
                Assert.True((await roles.DeleteAsync(role)).Succeeded);
        }
    }

    private static HttpRequestMessage CreateDeliveryRequest(Guid requestId, string token, string key, string signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/requests/{requestId}/deliver")
        {
            Content = JsonContent.Create(new
            {
                received_by_name = "PostgreSQL receiver",
                received_by_email = "receiver@example.invalid",
                received_by_document = "123456789",
                received_by_phone = "21999999999",
                signature,
                notes = "Concurrent replay verification."
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static Task<HttpResponseMessage> SendDeliveryAsync(HttpClient client, Guid requestId, string token, string key, string signature)
    {
        return client.SendAsync(CreateDeliveryRequest(requestId, token, key, signature));
    }

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "CorrectPassword1" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token, object body)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static string CreateSignatureDataUri()
    {
        using var bitmap = new SKBitmap(128, 64);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black, StrokeWidth = 4, IsStroke = true, IsAntialias = true };
        canvas.DrawLine(4, 36, 124, 20, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return $"data:image/png;base64,{Convert.ToBase64String(encoded.ToArray())}";
    }
}
