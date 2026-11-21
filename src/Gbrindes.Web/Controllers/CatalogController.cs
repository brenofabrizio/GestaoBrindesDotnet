using System.Text.Json;
using System.Globalization;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Gbrindes.Web.Data;
using Gbrindes.Web.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Route("api/v1/categories")]
[Authorize(Policy = "lookups.view")]
public sealed class CategoriesController(ApplicationDbContext database) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? trashed, [FromQuery] int? all, [FromQuery] string? q, [FromQuery] int? active, [FromQuery] string? sort, [FromQuery] int page = 1, [FromQuery(Name = "per_page")] int perPage = 25, CancellationToken ct = default)
    {
        var query = database.Categories.AsNoTracking().AsQueryable();
        if (all == 1)
        {
            var activeCategories = await query
                .Where(x => x.DeletedAt == null && x.Active)
                .OrderBy(x => x.Name)
                .ThenBy(x => x.Id)
                .ToListAsync(ct);
            return Ok(new { ok = true, data = activeCategories });
        }

        query = query.Where(x => trashed == 1 ? x.DeletedAt != null : x.DeletedAt == null);
        if (!string.IsNullOrWhiteSpace(q)) { var term = q.Trim().ToLower(); query = query.Where(x => x.Name.ToLower().Contains(term) || (x.Description != null && x.Description.ToLower().Contains(term))); }
        if (active is 0 or 1) query = query.Where(x => x.Active == (active == 1));
        query = sort switch
        {
            "name" => query.OrderBy(x => x.Name).ThenBy(x => x.Id),
            "-name" => query.OrderByDescending(x => x.Name).ThenBy(x => x.Id),
            "created_at" => query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id),
            "-created_at" => query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id),
            "updated_at" => query.OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id),
            "-updated_at" => query.OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Id),
            _ => query.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id)
        };
        page = Math.Max(1, page);
        perPage = perPage == 0 ? 25 : Math.Clamp(perPage, 1, 100);
        var total = await query.CountAsync(ct);
        var data = await query.Skip((page - 1) * perPage).Take(perPage).ToListAsync(ct);
        var lastPage = Math.Max(1, (int)Math.Ceiling(total / (double)perPage));
        return Ok(new { ok = true, data, meta = new { page, per_page = perPage, total, last_page = lastPage } });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var category = await database.Categories.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return category is null ? NotFound() : Ok(category);
    }

    [HttpPost, Authorize(Policy = "lookups.manage")]
    public async Task<IActionResult> Create(CategoryWrite request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (string.IsNullOrWhiteSpace(request.Name)) return ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["O nome é obrigatório."] });
        var name = request.Name.Trim();
        if (await database.Categories.AnyAsync(x => x.DeletedAt == null && x.NameKey == name.ToUpperInvariant(), ct))
            return ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Já existe uma categoria com este nome."] });
        var actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var categoryActorId) ? categoryActorId : (Guid?)null;
        var category = new Category { Name = name, NameKey = name.ToUpperInvariant(), Description = request.Description?.Trim(), Active = request.Active ?? true, CreatedBy = actorId, UpdatedBy = actorId };
        database.Categories.Add(category);
        AddAudit("create", category, null, AuditData(category));
        await database.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = category.Id }, category);
    }

    [HttpPut("{id:guid}"), Authorize(Policy = "lookups.manage")]
    public async Task<IActionResult> Update(Guid id, CategoryWrite request, CancellationToken ct)
    {
        var category = await database.Categories.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (category is null) return NotFound();
        var before = AuditData(category);
        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (name.Length == 0) return ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["O nome é obrigatório."] });
            if (await database.Categories.AnyAsync(x => x.Id != id && x.DeletedAt == null && x.NameKey == name.ToUpperInvariant(), ct))
                return ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Já existe uma categoria com este nome."] });
            category.Name = name; category.NameKey = name.ToUpperInvariant();
        }
        if (request.Description is not null) category.Description = request.Description.Trim();
        if (request.Active is not null) category.Active = request.Active.Value;
        category.UpdatedAt = DateTimeOffset.UtcNow;
        var current = AuditData(category);
        var beforeChanges = new Dictionary<string, object?>();
        var afterChanges = new Dictionary<string, object?>();
        foreach (var (field, value) in current)
        {
            if (!Equals(before[field], value))
            {
                beforeChanges[field] = before[field];
                afterChanges[field] = value;
            }
        }
        if (afterChanges.Count > 0)
        {
            category.UpdatedBy = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var categoryActorId) ? categoryActorId : null;
            AddAudit("update", category, beforeChanges, afterChanges, category.UpdatedAt);
        }
        await database.SaveChangesAsync(ct);
        return Ok(category);
    }

    [HttpPost("{id:guid}/activate"), Authorize(Policy = "lookups.manage")]
    public Task<IActionResult> Activate(Guid id, CancellationToken ct) => SetActive(id, true, ct);
    [HttpPost("{id:guid}/deactivate"), Authorize(Policy = "lookups.manage")]
    public Task<IActionResult> Deactivate(Guid id, CancellationToken ct) => SetActive(id, false, ct);

    [HttpDelete("{id:guid}"), Authorize(Policy = "lookups.delete")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var category = await database.Categories.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (category is null) return NotFound();
        var before = AuditData(category);
        category.DeletedAt = DateTimeOffset.UtcNow;
        category.UpdatedAt = category.DeletedAt.Value;
        AddAudit("delete", category, before, null, category.UpdatedAt);
        await database.SaveChangesAsync(ct);
        return Ok(new { id, deleted = true });
    }

    [HttpPost("{id:guid}/restore"), Authorize(Policy = "lookups.delete")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    {
        var category = await database.Categories.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (category is null) return NotFound();
        if (category.DeletedAt is null) return Conflict(new { error = "NOT_IN_TRASH" });
        if (await database.Categories.AnyAsync(x => x.Id != id && x.DeletedAt == null && x.NameKey == category.NameKey, ct))
            return Conflict(new { error = "DUPLICATE_NAME" });
        category.DeletedAt = null;
        category.UpdatedAt = DateTimeOffset.UtcNow;
        AddAudit("restore", category, null, null, category.UpdatedAt);
        await database.SaveChangesAsync(ct);
        return Ok(category);
    }

    [HttpPost("{id:guid}/purge"), Authorize(Policy = "lookups.purge")]
    public async Task<IActionResult> Purge(Guid id, CategoryPurgeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Confirm))
            return ValidationProblem(new Dictionary<string, string[]> { ["confirm"] = ["Confirmação obrigatória."] });

        IDbContextTransaction? transaction = null;
        try
        {
            if (database.Database.IsRelational())
                transaction = await database.Database.BeginTransactionAsync(ct);

            Category? category;
            if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
                category = await database.Categories.FromSqlInterpolated(
                    $"SELECT * FROM \"Categories\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
            else
                category = await database.Categories.SingleOrDefaultAsync(value => value.Id == id, ct);

            if (category is null) return NotFound();
            if (category.DeletedAt is null) return Conflict(new { error = "NOT_IN_TRASH" });
            if (!string.Equals(request.Confirm.Trim(), category.Name, StringComparison.OrdinalIgnoreCase))
                return ValidationProblem(new Dictionary<string, string[]> { ["confirm"] = [$"Digite \"{category.Name}\" para confirmar a exclusão definitiva."] });
            if (await database.CatalogItems.AnyAsync(item => item.CategoryId == id, ct))
                return Conflict(new { error = "IN_USE", uses = new[] { "brinde(s)" } });

            var now = DateTimeOffset.UtcNow;
            var agent = Request.Headers.UserAgent.ToString();
            database.AuditLogs.Add(new AuditLog
            {
                UserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ? actorId : null,
                UserName = User.FindFirstValue(ClaimTypes.Name),
                Action = "purge",
                EntityType = "category",
                EntityId = category.Id,
                EntityLabel = category.Name,
                BeforeJson = JsonSerializer.Serialize(new { id = category.Id, name = category.Name, description = category.Description, active = category.Active, deleted_at = category.DeletedAt }),
                Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = agent.Length > 255 ? agent[..255] : agent,
                CreatedAt = now
            });
            database.Categories.Remove(category);
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return Ok(new { id, purged = true });
        }
        catch (DbUpdateException) when (transaction is not null)
        {
            await transaction.RollbackAsync(ct);
            database.ChangeTracker.Clear();
            if (await database.CatalogItems.AnyAsync(item => item.CategoryId == id, ct))
                return Conflict(new { error = "IN_USE", uses = new[] { "brinde(s)" } });
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private IActionResult ValidationProblem(Dictionary<string, string[]> errors) => UnprocessableEntity(new ValidationProblemDetails(errors));

    private async Task<IActionResult> SetActive(Guid id, bool active, CancellationToken ct)
    {
        var category = await database.Categories.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (category is null) return NotFound();
        if (category.Active == active) return Ok(category);
        var previous = category.Active;
        category.Active = active;
        var now = DateTimeOffset.UtcNow;
        category.UpdatedAt = now;
        category.UpdatedBy = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var categoryActorId) ? categoryActorId : null;
        AddAudit(active ? "activate" : "deactivate", category, new { active = previous }, new { active }, now);
        await database.SaveChangesAsync(ct);
        return Ok(category);
    }

    private void AddAudit(string action, Category category, object? before, object? after, DateTimeOffset? createdAt = null)
    {
        var agent = Request.Headers.UserAgent.ToString();
        database.AuditLogs.Add(new AuditLog
        {
            UserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ? actorId : null,
            UserName = User.FindFirstValue(ClaimTypes.Name) ?? "Sistema",
            Action = action,
            EntityType = "category",
            EntityId = category.Id,
            EntityLabel = category.Name.Length > 190 ? category.Name[..190] : category.Name,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before),
            AfterJson = after is null ? null : JsonSerializer.Serialize(after),
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = agent.Length > 255 ? agent[..255] : agent,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow
        });
    }

    private static Dictionary<string, object?> AuditData(Category category) => new()
    {
        ["id"] = category.Id,
        ["name"] = category.Name,
        ["description"] = category.Description,
        ["active"] = category.Active,
        ["deleted_at"] = category.DeletedAt
    };
}

[ApiController]
[Route("api/v1/items")]
public sealed class CatalogItemsController(ApplicationDbContext database, ItemPhotoStorage photoStorage) : ControllerBase
{
    [HttpGet("options"), Authorize(Policy = "items.view")]
    public async Task<IActionResult> Options([FromQuery] string? q, CancellationToken ct)
    {
        var items = database.CatalogItems.AsNoTracking()
            .Where(item => item.DeletedAt == null && item.Status == "ativo");
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            items = items.Where(item => item.Code.ToLower().Contains(term) || item.Name.ToLower().Contains(term));
        }

        var rows = await (
            from item in items
            join stock in database.Stocks.AsNoTracking() on item.Id equals stock.ItemId into stockRows
            from stock in stockRows.DefaultIfEmpty()
            orderby item.Name, item.Id
            select new
            {
                Item = item,
                OnHand = stock == null ? 0 : stock.OnHand,
                Reserved = stock == null ? 0 : stock.Reserved
            }).Take(50).ToListAsync(ct);
        var data = rows.Select(row =>
        {
            var available = row.OnHand - row.Reserved;
            var level = available <= 0 ? "zero" : row.Item.MinStock > 0 && available <= row.Item.MinStock ? "low" : "ok";
            return new
            {
                id = row.Item.Id,
                code = row.Item.Code,
                name = row.Item.Name,
                unit_value = row.Item.UnitValue,
                available,
                level,
                thumb_url = row.Item.ThumbUrl
            };
        });
        return Ok(new { ok = true, data });
    }

    [HttpGet("next-code"), Authorize(Policy = "items.manage")]
    public async Task<IActionResult> NextCode(CancellationToken ct)
    {
        var code = await PreviewNextCodeAsync(ct);
        return Ok(new { ok = true, data = new { code } });
    }

    [HttpGet, Authorize(Policy = "items.view")]
    public async Task<IActionResult> List(
        [FromQuery(Name = "q")] string? q,
        [FromQuery(Name = "category_id")] Guid? categoryId,
        [FromQuery(Name = "location_id")] Guid? locationId,
        [FromQuery(Name = "supplier_id")] Guid? supplierId,
        [FromQuery(Name = "industry_id")] Guid? industryId,
        [FromQuery(Name = "status")] string? status,
        [FromQuery(Name = "stock_level")] string? stockLevel,
        [FromQuery(Name = "trashed")] int? trashed,
        [FromQuery(Name = "sort")] string? sort,
        [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "pageSize")] int pageSize = 25,
        [FromQuery(Name = "per_page")] int? perPage = null,
        CancellationToken ct = default)
    {
        var query = database.CatalogItems.AsNoTracking().Include(x => x.Category).Include(x => x.Industry).Include(x => x.Location).Include(x => x.Supplier).AsQueryable();
        query = query.Where(x => trashed == 1 ? x.DeletedAt != null : x.DeletedAt == null);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var normalizedQuery = q.Trim().ToLower();
            query = query.Where(x =>
                x.Name.ToLower().Contains(normalizedQuery) ||
                x.Code.ToLower().Contains(normalizedQuery) ||
                (x.Description != null && x.Description.ToLower().Contains(normalizedQuery)));
        }
        if (categoryId is not null) query = query.Where(x => x.CategoryId == categoryId);
        if (locationId is not null) query = query.Where(x => x.LocationId == locationId);
        if (supplierId is not null) query = query.Where(x => x.SupplierId == supplierId);
        if (industryId is not null) query = query.Where(x => x.IndustryId == industryId);
        if (status is "ativo" or "inativo") query = query.Where(x => x.Status == status);
        var stock = database.Stocks.AsNoTracking();
        if (stockLevel is "zero" or "low" or "ok" or "attention")
        {
            var ids = stock.Select(s => new { s.ItemId, Available = s.OnHand - s.Reserved });
            var matched = database.CatalogItems.Where(i =>
                stockLevel == "zero"
                    ? !ids.Any(s => s.ItemId == i.Id && s.Available > 0)
                    : stockLevel == "low"
                        ? ids.Any(s => s.ItemId == i.Id && s.Available > 0 && s.Available <= i.MinStock)
                        : stockLevel == "ok"
                            ? ids.Any(s => s.ItemId == i.Id && s.Available > i.MinStock)
                            : !ids.Any(s => s.ItemId == i.Id && s.Available > 0) ||
                              (i.MinStock > 0 && ids.Any(s => s.ItemId == i.Id && s.Available <= i.MinStock)));
            query = query.Where(i => matched.Any(m => m.Id == i.Id));
        }
        query = sort switch
        {
            "name" => query.OrderBy(x => x.Name).ThenBy(x => x.Id),
            "-name" => query.OrderByDescending(x => x.Name).ThenBy(x => x.Id),
            "code" => query.OrderBy(x => x.Code).ThenBy(x => x.Id),
            "-code" => query.OrderByDescending(x => x.Code).ThenBy(x => x.Id),
            "available" => query.OrderBy(x => database.Stocks.Where(s => s.ItemId == x.Id).Select(s => (int?)(s.OnHand - s.Reserved)).FirstOrDefault() ?? 0).ThenBy(x => x.Id),
            "-available" => query.OrderByDescending(x => database.Stocks.Where(s => s.ItemId == x.Id).Select(s => (int?)(s.OnHand - s.Reserved)).FirstOrDefault() ?? 0).ThenBy(x => x.Id),
            "on_hand" => query.OrderBy(x => database.Stocks.Where(s => s.ItemId == x.Id).Select(s => (int?)s.OnHand).FirstOrDefault() ?? 0).ThenBy(x => x.Id),
            "-on_hand" => query.OrderByDescending(x => database.Stocks.Where(s => s.ItemId == x.Id).Select(s => (int?)s.OnHand).FirstOrDefault() ?? 0).ThenBy(x => x.Id),
            "unit_value" => query.OrderBy(x => x.UnitValue ?? 0m).ThenBy(x => x.Id),
            "-unit_value" => query.OrderByDescending(x => x.UnitValue ?? 0m).ThenBy(x => x.Id),
            "created_at" => query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id),
            "-created_at" => query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id),
            "updated_at" => query.OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id),
            "-updated_at" => query.OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Id),
            _ => query.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id)
        };
        page = Math.Max(1, page);
        var requestedPageSize = perPage ?? pageSize;
        pageSize = requestedPageSize == 0 ? 25 : Math.Clamp(requestedPageSize, 1, 100);
        var total = await query.CountAsync(ct);
        var data = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var lastPage = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        return Ok(new { ok = true, data, meta = new { page, per_page = pageSize, total, last_page = lastPage } });
    }

    [HttpGet("{id:guid}"), Authorize(Policy = "items.view")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var item = await database.CatalogItems.AsNoTracking().Include(x => x.Category).Include(x => x.Industry).Include(x => x.Location).Include(x => x.Supplier).FirstOrDefaultAsync(x => x.Id == id, ct);
        return item is null ? NotFound() : Ok(item);
    }

    [HttpGet("{id:guid}/photo"), HttpGet("~/api/items/{id:guid}/photo"), Authorize(Policy = "items.view")]
    public async Task<IActionResult> Photo(Guid id, [FromQuery] string? size, CancellationToken ct)
    {
        var paths = await database.CatalogItems.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new { item.PhotoPath, item.ThumbPath })
            .SingleOrDefaultAsync(ct);
        if (paths is null) return NotFound();

        var file = photoStorage.OpenRead(size == "thumb" ? paths.ThumbPath : paths.PhotoPath);
        if (file is null) return NotFound();
        Response.Headers.CacheControl = "private, max-age=86400";
        Response.Headers.Vary = "Authorization";
        return File(file, "image/jpeg");
    }

    [HttpPost("{id:guid}/photo"), Authorize(Policy = "items.manage"), Consumes("multipart/form-data")]
    [RequestSizeLimit(ItemPhotoStorage.MaximumUploadBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = ItemPhotoStorage.MaximumUploadBytes + 64 * 1024)]
    public async Task<IActionResult> UploadPhoto(Guid id, [FromForm(Name = "photo")] IFormFile? photo, CancellationToken ct)
    {
        if (!await database.CatalogItems.AsNoTracking().AnyAsync(item => item.Id == id && item.DeletedAt == null, ct))
            return NotFound();
        if (photo is null)
            return ValidationProblem(new Dictionary<string, string[]> { ["photo"] = ["Selecione uma imagem."] });

        StoredItemPhoto stored;
        try
        {
            stored = await photoStorage.StoreAsync(id, photo, ct);
        }
        catch (ItemPhotoValidationException exception)
        {
            return ValidationProblem(new Dictionary<string, string[]> { ["photo"] = [exception.Message] });
        }

        IDbContextTransaction? transaction = null;
        CatalogItem? updatedItem = null;
        string? oldPhotoPath = null;
        string? oldThumbPath = null;
        try
        {
            if (database.Database.IsRelational())
                transaction = await database.Database.BeginTransactionAsync(ct);

            updatedItem = await LoadPhotoItemForUpdateAsync(id, ct);
            if (updatedItem is null || updatedItem.DeletedAt is not null)
            {
                if (transaction is not null) await transaction.RollbackAsync(ct);
                photoStorage.Delete(stored.PhotoPath, stored.ThumbPath);
                return NotFound();
            }

            oldPhotoPath = updatedItem.PhotoPath;
            oldThumbPath = updatedItem.ThumbPath;
            var now = DateTimeOffset.UtcNow;
            updatedItem.PhotoPath = stored.PhotoPath;
            updatedItem.ThumbPath = stored.ThumbPath;
            updatedItem.UpdatedAt = now;
            AddPhotoAudit("photo_update", updatedItem, now);
            await database.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(ct);
            photoStorage.Delete(stored.PhotoPath, stored.ThumbPath);
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }

        photoStorage.Delete(oldPhotoPath, oldThumbPath);
        return Ok(updatedItem);
    }

    [HttpDelete("{id:guid}/photo"), Authorize(Policy = "items.manage")]
    public async Task<IActionResult> DeletePhoto(Guid id, CancellationToken ct)
    {
        IDbContextTransaction? transaction = null;
        CatalogItem? item = null;
        string? oldPhotoPath = null;
        string? oldThumbPath = null;
        try
        {
            if (database.Database.IsRelational())
                transaction = await database.Database.BeginTransactionAsync(ct);

            item = await LoadPhotoItemForUpdateAsync(id, ct);
            if (item is null || item.DeletedAt is not null) return NotFound();
            if (item.PhotoPath is null && item.ThumbPath is null) return Ok(item);

            oldPhotoPath = item.PhotoPath;
            oldThumbPath = item.ThumbPath;
            var now = DateTimeOffset.UtcNow;
            item.PhotoPath = null;
            item.ThumbPath = null;
            item.UpdatedAt = now;
            AddPhotoAudit("photo_delete", item, now);
            await database.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(ct);
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }

        photoStorage.Delete(oldPhotoPath, oldThumbPath);
        return Ok(item);
    }

    [HttpGet("{id:guid}/history"), Authorize(Policy = "items.view")]
    public async Task<IActionResult> History(Guid id, [FromQuery] int page = 1,
        [FromQuery(Name = "per_page")] int perPage = 30, CancellationToken ct = default)
    {
        if (!await database.CatalogItems.AsNoTracking().AnyAsync(item => item.Id == id, ct))
            return NotFound();

        page = Math.Clamp(page, 1, 1_000_000);
        perPage = Math.Clamp(perPage == 0 ? 30 : perPage, 1, 100);

        var movements =
            from movement in database.StockMovements.AsNoTracking()
            join user in database.Users.AsNoTracking() on movement.UserId equals (Guid?)user.Id
            where movement.ItemId == id
            select new
            {
                kind = "movement",
                id = movement.Id,
                at = movement.CreatedAt,
                user_id = (Guid?)user.Id,
                user_name = (string?)user.FullName,
                action = movement.Type,
                quantity = (int?)movement.Quantity,
                balance_after = (int?)movement.BalanceAfter,
                reason = movement.Reason,
                purpose = movement.Purpose,
                recipient = movement.Recipient,
                purchase_ticket_no = movement.PurchaseTicketNo,
                notes = movement.Notes,
                before_json = (string?)null,
                after_json = (string?)null
            };
        var changes = database.AuditLogs.AsNoTracking()
            .Where(log => log.EntityType == "item" && log.EntityId == id
                && log.Action != "stock_entrada" && log.Action != "stock_saida" && log.Action != "stock_ajuste")
            .Select(log => new
            {
                kind = "change",
                id = log.Id,
                at = log.CreatedAt,
                user_id = log.UserId,
                user_name = log.UserName,
                action = log.Action,
                quantity = (int?)null,
                balance_after = (int?)null,
                reason = (string?)null,
                purpose = (string?)null,
                recipient = (string?)null,
                purchase_ticket_no = (string?)null,
                notes = (string?)null,
                before_json = log.BeforeJson,
                after_json = log.AfterJson
            });
        var history = movements.Concat(changes);
        var total = await history.CountAsync(ct);
        var entries = await history
            .OrderByDescending(entry => entry.at)
            .ThenByDescending(entry => entry.id)
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .ToListAsync(ct);
        var data = entries.Select(entry => new
        {
            kind = entry.kind,
            id = entry.id,
            at = entry.at,
            user = new { id = entry.user_id, name = entry.user_name },
            action = entry.action,
            action_label = ItemHistoryActionLabel(entry.kind, entry.action),
            quantity = entry.quantity,
            balance_after = entry.balance_after,
            reason = entry.reason,
            purpose = entry.purpose,
            recipient = entry.recipient,
            purchase_ticket_no = entry.purchase_ticket_no,
            notes = entry.notes,
            before = ParseSnapshot(entry.before_json),
            after = ParseSnapshot(entry.after_json)
        });

        return Ok(new
        {
            ok = true,
            data,
            meta = new { page, per_page = perPage, total, last_page = Math.Max(1, (int)Math.Ceiling(total / (double)perPage)) }
        });
    }

    [HttpPost, Authorize(Policy = "items.manage")]
    public async Task<IActionResult> Create(ItemWrite request, CancellationToken ct)
    {
        var textValidation = ValidateItemTextFields(request.Description, request.Notes);
        if (textValidation is not null) return textValidation;
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        var initialQuantity = request.InitialQuantity ?? 0;
        if (initialQuantity > 0 && !User.HasClaim("permission", "stock.entry"))
            return Forbid();
        if (initialQuantity > 0 && request.Status == "inativo")
            return ValidationProblem(new Dictionary<string, string[]> { ["initialQuantity"] = ["Não é possível lançar saldo inicial em um brinde inativo."] });
        if (request.IndustryId is not null && request.IndustryIdSnake is not null && request.IndustryId != request.IndustryIdSnake)
            return ValidationProblem(new Dictionary<string, string[]> { ["industry_id"] = ["Informe apenas um valor de indústria."] });
        var industryId = request.IndustryIdSnake ?? request.IndustryId;
        if (!await database.Categories.AnyAsync(x => x.Id == request.CategoryId && x.DeletedAt == null, ct))
            return ValidationProblem(new Dictionary<string, string[]> { ["categoryId"] = ["A categoria informada não existe."] });
        if (industryId is not null && !await database.Industries.AnyAsync(x => x.Id == industryId && x.DeletedAt == null, ct))
            return ValidationProblem(new Dictionary<string, string[]> { ["industry_id"] = ["Indústria inválida."] });
        if (request.LocationId is not null && !await database.Locations.AnyAsync(x => x.Id == request.LocationId && x.DeletedAt == null, ct))
            return ValidationProblem(new Dictionary<string, string[]> { ["locationId"] = ["Local inválido."] });
        if (request.SupplierId is not null && !await database.Suppliers.AnyAsync(x => x.Id == request.SupplierId && x.DeletedAt == null, ct))
            return ValidationProblem(new Dictionary<string, string[]> { ["supplierId"] = ["Fornecedor inválido."] });
        await using var transaction = database.Database.IsRelational() ? await database.Database.BeginTransactionAsync(ct) : null;
        var code = string.IsNullOrWhiteSpace(request.Code) ? await AllocateNextCodeAsync(ct) : request.Code.Trim().ToUpperInvariant();
        if (!ValidCode(code)) return ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["Código inválido."] });
        if (await database.CatalogItems.AnyAsync(x => x.Code == code, ct))
            return ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["Já existe um brinde com este código."] });
        if (!ValidKind(request.Kind)) return ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Tipo de brinde inválido."] });
        var itemActorId = CurrentActorId();
        var item = new CatalogItem { Code = code, Name = request.Name.Trim(), Description = request.Description, CategoryId = request.CategoryId, IndustryId = industryId, LocationId = request.LocationId, SupplierId = request.SupplierId, Kind = request.Kind ?? "fisico", UnitValue = request.UnitValue, MinStock = request.MinStock ?? 0, Status = request.Status ?? "ativo", EntryDate = request.EntryDate, Notes = request.Notes, CreatedBy = itemActorId, UpdatedBy = itemActorId };
        if (item.Status is not ("ativo" or "inativo")) return ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["Status inválido."] });
        database.CatalogItems.Add(item);
        if (initialQuantity > 0)
        {
            var userId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedUserId) ? parsedUserId : (Guid?)null;
            var initialLocationId = item.LocationId ?? await database.Locations.AsNoTracking()
                .Where(location => location.Active && location.DeletedAt == null)
                .OrderBy(location => location.Kind == "cd" ? 0 : 1).ThenBy(location => location.Id)
                .Select(location => (Guid?)location.Id).FirstOrDefaultAsync(ct);
            database.Stocks.Add(new InventoryStock { ItemId = item.Id, Item = item, OnHand = initialQuantity });
            if (initialLocationId is Guid stockLocationId)
                database.StockPositions.Add(new StockPosition { ItemId = item.Id, LocationId = stockLocationId, Quantity = initialQuantity });
            database.StockMovements.Add(new StockMovement
            {
                ItemId = item.Id,
                Item = item,
                UserId = userId,
                Type = "entrada",
                Quantity = initialQuantity,
                BalanceAfter = initialQuantity,
                Reason = "Saldo inicial",
                Notes = "Saldo inicial informado no cadastro do brinde.",
                LocationId = initialLocationId
            });
        }
        var actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var auditActorId) ? auditActorId : (Guid?)null;
        item.UpdatedBy = actorId;
        var userAgent = Request.Headers.UserAgent.ToString();
        database.AuditLogs.Add(new AuditLog
        {
            UserId = actorId,
            UserName = User.FindFirstValue(ClaimTypes.Name),
            Action = "create",
            EntityType = "item",
            EntityId = item.Id,
            EntityLabel = $"{item.Code} - {item.Name}",
            AfterJson = JsonSerializer.Serialize(new
            {
                code = item.Code,
                name = item.Name,
                description = item.Description,
                category_id = item.CategoryId,
                industry_id = item.IndustryId,
                location_id = item.LocationId,
                supplier_id = item.SupplierId,
                kind = item.Kind,
                unit_value = item.UnitValue,
                min_stock = item.MinStock,
                status = item.Status,
                entry_date = item.EntryDate,
                notes = item.Notes,
                initial_quantity = initialQuantity
            }),
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = userAgent.Length > 255 ? userAgent[..255] : userAgent,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await database.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        await database.Entry(item).Reference(x => x.Category).LoadAsync(ct);
        await database.Entry(item).Reference(x => x.Industry).LoadAsync(ct);
        await database.Entry(item).Reference(x => x.Location).LoadAsync(ct);
        await database.Entry(item).Reference(x => x.Supplier).LoadAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = item.Id }, item);
    }

    [HttpPut("{id:guid}"), Authorize(Policy = "items.manage")]
    public async Task<IActionResult> Update(Guid id, ItemUpdate request, CancellationToken ct)
    {
        var descriptionValidation = ValidateItemUpdateText(request.Description, "description", out var hasDescription, out var description);
        if (descriptionValidation is not null) return descriptionValidation;
        var notesValidation = ValidateItemUpdateText(request.Notes, "notes", out var hasNotes, out var notes);
        if (notesValidation is not null) return notesValidation;
        var entryDateValidation = ValidateItemUpdateEntryDate(request.EntryDate, out var hasEntryDate, out var entryDate);
        if (entryDateValidation is not null) return entryDateValidation;
        var minStockValidation = ValidateItemUpdateMinStock(request.MinStock, out var hasMinStock, out var minStock);
        if (minStockValidation is not null) return minStockValidation;
        var unitValueValidation = ValidateItemUpdateUnitValue(request.UnitValue, out var hasUnitValue, out var unitValue);
        if (unitValueValidation is not null) return unitValueValidation;
        var hasStatus = request.Status.ValueKind != JsonValueKind.Undefined;
        var status = request.Status.ValueKind == JsonValueKind.String ? request.Status.GetString() : null;
        if (hasStatus && status is not ("ativo" or "inativo"))
            return ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["Status inválido."] });
        var hasName = request.Name.ValueKind != JsonValueKind.Undefined;
        var name = request.Name.ValueKind == JsonValueKind.String ? request.Name.GetString() : null;
        if (hasName && string.IsNullOrWhiteSpace(name))
            return ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["O nome é obrigatório."] });
        if (hasName && name!.Length > 150)
            return ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["O nome deve ter no máximo 150 caracteres."] });
        var item = await database.CatalogItems.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (item is null) return NotFound();
        var before = ItemAuditSnapshot(item);
        if (status == "inativo" && item.Status != "inativo" && await database.Stocks.AnyAsync(stock => stock.ItemId == id && stock.Reserved > 0, ct))
            return Conflict(new { error = "ITEM_HAS_RESERVATIONS" });
        if (hasName) item.Name = name!.Trim();
        if (!string.IsNullOrWhiteSpace(request.Code)) { var code = request.Code.Trim().ToUpperInvariant(); if (!ValidCode(code) || await database.CatalogItems.AnyAsync(x => x.Id != id && x.Code == code, ct)) return ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["Código inválido ou já utilizado."] }); item.Code = code; }
        if (hasDescription) item.Description = description;
        if (request.CategoryId.ValueKind != JsonValueKind.Undefined)
        {
            if (!TryReadNullableGuid(request.CategoryId, out var categoryId) || categoryId is null)
                return ValidationProblem(new Dictionary<string, string[]> { ["categoryId"] = ["A categoria é obrigatória."] });
            if (!await database.Categories.AnyAsync(x => x.Id == categoryId && x.DeletedAt == null, ct))
                return ValidationProblem(new Dictionary<string, string[]> { ["categoryId"] = ["A categoria informada não existe."] });
            item.CategoryId = categoryId.Value;
        }
        if (request.LocationId.ValueKind != JsonValueKind.Undefined)
        {
            if (!TryReadNullableGuid(request.LocationId, out var locationId))
                return ValidationProblem(new Dictionary<string, string[]> { ["locationId"] = ["Local inválido."] });
            if (locationId is not null && !await database.Locations.AnyAsync(x => x.Id == locationId && x.DeletedAt == null, ct))
                return ValidationProblem(new Dictionary<string, string[]> { ["locationId"] = ["Local inválido."] });
            item.LocationId = locationId;
        }
        if (request.SupplierId.ValueKind != JsonValueKind.Undefined)
        {
            if (!TryReadNullableGuid(request.SupplierId, out var supplierId))
                return ValidationProblem(new Dictionary<string, string[]> { ["supplierId"] = ["Fornecedor inválido."] });
            if (supplierId is not null && !await database.Suppliers.AnyAsync(x => x.Id == supplierId && x.DeletedAt == null, ct))
                return ValidationProblem(new Dictionary<string, string[]> { ["supplierId"] = ["Fornecedor inválido."] });
            item.SupplierId = supplierId;
        }
        var industryIdInput = request.IndustryIdSnake.ValueKind != JsonValueKind.Undefined
            ? request.IndustryIdSnake
            : request.IndustryId;
        if (industryIdInput.ValueKind != JsonValueKind.Undefined)
        {
            if (!TryReadNullableGuid(industryIdInput, out var industryId))
                return ValidationProblem(new Dictionary<string, string[]> { ["industry_id"] = ["Indústria inválida."] });
            if (industryId is not null && !await database.Industries.AnyAsync(x => x.Id == industryId && x.DeletedAt == null, ct))
                return ValidationProblem(new Dictionary<string, string[]> { ["industry_id"] = ["Indústria inválida."] });
            item.IndustryId = industryId;
        }
        if (request.Kind is not null) { if (!ValidKind(request.Kind)) return ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Tipo de brinde inválido."] }); item.Kind = request.Kind; }
        if (hasUnitValue) item.UnitValue = unitValue;
        if (hasMinStock) item.MinStock = minStock;
        if (hasStatus) item.Status = status!;
        if (hasEntryDate) item.EntryDate = entryDate;
        if (hasNotes) item.Notes = notes;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        var actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var auditActorId) ? auditActorId : (Guid?)null;
        item.UpdatedBy = actorId;
        var after = ItemAuditSnapshot(item);
        var beforeChanges = new Dictionary<string, object?>();
        var afterChanges = new Dictionary<string, object?>();
        foreach (var (field, value) in after)
        {
            if (Equals(before[field], value))
                continue;
            beforeChanges[field] = before[field];
            afterChanges[field] = value;
        }
        if (afterChanges.Count > 0)
        {
            var userAgent = Request.Headers.UserAgent.ToString();
            database.AuditLogs.Add(new AuditLog
            {
                UserId = actorId,
                UserName = User.FindFirstValue(ClaimTypes.Name),
                Action = "update",
                EntityType = "item",
                EntityId = item.Id,
                EntityLabel = $"{item.Code} - {item.Name}",
                BeforeJson = JsonSerializer.Serialize(beforeChanges),
                AfterJson = JsonSerializer.Serialize(afterChanges),
                Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = userAgent.Length > 255 ? userAgent[..255] : userAgent,
                CreatedAt = item.UpdatedAt
            });
        }
        await database.SaveChangesAsync(ct);
        await database.Entry(item).Reference(x => x.Category).LoadAsync(ct);
        await database.Entry(item).Reference(x => x.Industry).LoadAsync(ct);
        await database.Entry(item).Reference(x => x.Location).LoadAsync(ct);
        await database.Entry(item).Reference(x => x.Supplier).LoadAsync(ct);
        return Ok(item);
    }

    [HttpPost("{id:guid}/activate"), Authorize(Policy = "items.manage")]
    public Task<IActionResult> Activate(Guid id, CancellationToken ct) => SetStatus(id, "ativo", ct);
    [HttpPost("{id:guid}/deactivate"), Authorize(Policy = "items.manage")]
    public Task<IActionResult> Deactivate(Guid id, CancellationToken ct) => SetStatus(id, "inativo", ct);

    [HttpDelete("{id:guid}"), Authorize(Policy = "items.delete")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        IDbContextTransaction? transaction = null;
        try
        {
            if (database.Database.IsRelational())
                transaction = await database.Database.BeginTransactionAsync(ct);

            CatalogItem? item;
            if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                item = await database.CatalogItems
                    .FromSqlInterpolated($"SELECT * FROM \"CatalogItems\" WHERE \"Id\" = {id} AND \"DeletedAt\" IS NULL FOR UPDATE")
                    .SingleOrDefaultAsync(ct);
            }
            else
            {
                item = await database.CatalogItems.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
            }

            if (item is null) return NotFound();
            var stock = await database.Stocks.FirstOrDefaultAsync(x => x.ItemId == id, ct);
            if (stock?.Reserved > 0) return Conflict(new { error = "ITEM_HAS_RESERVATIONS" });
            if (stock?.OnHand > 0) return Conflict(new { error = "ITEM_HAS_STOCK" });

            var before = ItemTrashAuditSnapshot(item);
            var actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedActorId) ? parsedActorId : (Guid?)null;
            item.DeletedAt = DateTimeOffset.UtcNow;
            item.UpdatedAt = item.DeletedAt.Value;
            item.DeletedBy = actorId;
            var userAgent = Request.Headers.UserAgent.ToString();
            database.AuditLogs.Add(new AuditLog
            {
                UserId = actorId,
                UserName = User.FindFirstValue(ClaimTypes.Name),
                Action = "delete",
                EntityType = "item",
                EntityId = item.Id,
                EntityLabel = $"{item.Code} - {item.Name}",
                BeforeJson = JsonSerializer.Serialize(before),
                Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = userAgent.Length > 255 ? userAgent[..255] : userAgent,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await database.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return Ok(new { id, deleted = true });
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    [HttpPost("{id:guid}/restore"), Authorize(Policy = "items.delete")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    {
        var item = await database.CatalogItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return NotFound();
        if (item.DeletedAt is null) return Conflict(new { error = "NOT_IN_TRASH" });
        item.DeletedAt = null; item.DeletedBy = null; item.UpdatedAt = DateTimeOffset.UtcNow;
        var userAgent = Request.Headers.UserAgent.ToString();
        database.AuditLogs.Add(new AuditLog
        {
            UserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ? actorId : null,
            UserName = User.FindFirstValue(ClaimTypes.Name),
            Action = "restore",
            EntityType = "item",
            EntityId = item.Id,
            EntityLabel = $"{item.Code} - {item.Name}",
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = userAgent.Length > 255 ? userAgent[..255] : userAgent,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await database.SaveChangesAsync(ct);
        return Ok(item);
    }

    [HttpPost("{id:guid}/purge"), Authorize(Policy = "items.purge")]
    public async Task<IActionResult> Purge(Guid id, ItemPurgeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Confirm))
            return ValidationProblem(new Dictionary<string, string[]> { ["confirm"] = ["Informe o código do brinde para confirmar."] });

        IDbContextTransaction? transaction = null;
        string? photoPathToDelete = null;
        string? thumbPathToDelete = null;
        try
        {
            if (database.Database.IsRelational())
                transaction = await database.Database.BeginTransactionAsync(ct);

            CatalogItem? item;
            if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                item = await database.CatalogItems
                    .FromSqlInterpolated($"SELECT * FROM \"CatalogItems\" WHERE \"Id\" = {id} FOR UPDATE")
                    .SingleOrDefaultAsync(ct);
            }
            else
            {
                item = await database.CatalogItems.FirstOrDefaultAsync(x => x.Id == id, ct);
            }

            if (item is null) return NotFound();
            if (item.DeletedAt is null) return Conflict(new { error = "NOT_IN_TRASH" });
            if (!string.Equals(request.Confirm.Trim(), item.Code, StringComparison.OrdinalIgnoreCase))
                return ValidationProblem(new Dictionary<string, string[]> { ["confirm"] = [$"Digite \"{item.Code}\" para confirmar a exclusão definitiva."] });

            var uses = new List<string>();
            if (await database.StockMovements.AnyAsync(movement => movement.ItemId == id, ct))
                uses.Add("movimentações de estoque");
            if (await database.RequestItems.AnyAsync(requestItem => requestItem.ItemId == id, ct))
                uses.Add("solicitações");
            if (await database.EventAllocations.AnyAsync(allocation => allocation.ItemId == id, ct))
                uses.Add("cotas de eventos");
            if (uses.Count > 0)
                return Conflict(new { error = "IN_USE", uses });

            var stock = await database.Stocks.SingleOrDefaultAsync(entry => entry.ItemId == id, ct);
            if (stock is { OnHand: > 0 } or { Reserved: > 0 })
                return Conflict(new { error = "ITEM_HAS_STOCK" });

            photoPathToDelete = item.PhotoPath;
            thumbPathToDelete = item.ThumbPath;
            database.AuditLogs.Add(new AuditLog
            {
                UserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ? actorId : null,
                UserName = User.FindFirstValue(ClaimTypes.Name),
                Action = "purge",
                EntityType = "item",
                EntityId = item.Id,
                EntityLabel = $"{item.Code} - {item.Name}",
                BeforeJson = JsonSerializer.Serialize(new { item.Id, item.Code, item.Name, item.CategoryId, item.LocationId, item.SupplierId, item.Status, item.DeletedAt }),
                Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = Request.Headers.UserAgent.ToString() is { Length: > 255 } agent ? agent[..255] : Request.Headers.UserAgent.ToString(),
                CreatedAt = DateTimeOffset.UtcNow
            });
            if (stock is not null)
                database.Stocks.Remove(stock);
            database.CatalogItems.Remove(item);
            await database.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            photoStorage.Delete(photoPathToDelete, thumbPathToDelete);
            return Ok(new { id, purged = true });
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(ct);
            throw;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private IActionResult ValidationProblem(Dictionary<string, string[]> errors) => UnprocessableEntity(new ValidationProblemDetails(errors));

    private async Task<string> PreviewNextCodeAsync(CancellationToken ct)
    {
        var lastValue = await database.SequenceCounters.AsNoTracking()
            .Where(sequence => sequence.Name == "item" && sequence.Period == "all")
            .Select(sequence => (int?)sequence.LastValue)
            .SingleOrDefaultAsync(ct) ?? 0;
        var next = lastValue + 1;
        string code;
        do
        {
            code = $"BRD-{next:D5}";
            next++;
        } while (await database.CatalogItems.AnyAsync(item => item.Code == code, ct));
        return code;
    }

    private async Task<string> AllocateNextCodeAsync(CancellationToken ct)
    {
        while (true)
        {
            var next = await NextItemSequenceValueAsync(ct);
            var code = $"BRD-{next:D5}";
            if (!await database.CatalogItems.AnyAsync(item => item.Code == code, ct))
                return code;
        }
    }

    private async Task<int> NextItemSequenceValueAsync(CancellationToken ct)
    {
        if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            var connection = database.Database.GetDbConnection();
            var closeConnection = connection.State != ConnectionState.Open;
            if (closeConnection)
                await database.Database.OpenConnectionAsync(ct);
            try
            {
                await using var command = connection.CreateCommand();
                command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
                command.CommandText = """
                    INSERT INTO "Sequences" ("Name", "Period", "LastValue")
                    VALUES (@name, @period, 1)
                    ON CONFLICT ("Name", "Period")
                    DO UPDATE SET "LastValue" = "Sequences"."LastValue" + 1
                    RETURNING "LastValue"
                    """;
                var nameParameter = command.CreateParameter();
                nameParameter.ParameterName = "name";
                nameParameter.Value = "item";
                command.Parameters.Add(nameParameter);
                var periodParameter = command.CreateParameter();
                periodParameter.ParameterName = "period";
                periodParameter.Value = "all";
                command.Parameters.Add(periodParameter);
                var value = await command.ExecuteScalarAsync(ct);
                return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            finally
            {
                if (closeConnection)
                    await database.Database.CloseConnectionAsync();
            }
        }

        var existing = await database.SequenceCounters.SingleOrDefaultAsync(
            sequence => sequence.Name == "item" && sequence.Period == "all", ct);
        if (existing is not null)
        {
            existing.LastValue = checked(existing.LastValue + 1);
            return existing.LastValue;
        }

        database.SequenceCounters.Add(new SequenceCounter { Name = "item", Period = "all", LastValue = 1 });
        return 1;
    }

    private async Task<CatalogItem?> LoadPhotoItemForUpdateAsync(Guid id, CancellationToken ct)
    {
        if (database.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            return await database.CatalogItems
                .FromSqlInterpolated($"SELECT * FROM \"CatalogItems\" WHERE \"Id\" = {id} FOR UPDATE")
                .SingleOrDefaultAsync(ct);
        return await database.CatalogItems.SingleOrDefaultAsync(item => item.Id == id, ct);
    }

    private void AddPhotoAudit(string action, CatalogItem item, DateTimeOffset createdAt)
    {
        item.UpdatedBy = CurrentActorId();
        var userAgent = Request.Headers.UserAgent.ToString();
        var label = $"{item.Code} - {item.Name}";
        database.AuditLogs.Add(new AuditLog
        {
            UserId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ? actorId : null,
            UserName = User.FindFirstValue(ClaimTypes.Name),
            Action = action,
            EntityType = "item",
            EntityId = item.Id,
            EntityLabel = label.Length > 190 ? label[..190] : label,
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = userAgent.Length > 255 ? userAgent[..255] : userAgent,
            CreatedAt = createdAt
        });
    }

    private async Task<IActionResult> SetStatus(Guid id, string status, CancellationToken ct)
    {
        var item = await database.CatalogItems.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (item is null) return NotFound();
        if (item.Status == status) return Ok(item);
        if (status == "inativo" && await database.Stocks.AnyAsync(stock => stock.ItemId == id && stock.Reserved > 0, ct))
            return Conflict(new { error = "ITEM_HAS_RESERVATIONS" });
        var previousStatus = item.Status;
        item.Status = status;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        var actorId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var auditActorId) ? auditActorId : (Guid?)null;
        item.UpdatedBy = actorId;
        var userAgent = Request.Headers.UserAgent.ToString();
        database.AuditLogs.Add(new AuditLog
        {
            UserId = actorId,
            UserName = User.FindFirstValue(ClaimTypes.Name),
            Action = status == "ativo" ? "activate" : "deactivate",
            EntityType = "item",
            EntityId = item.Id,
            EntityLabel = $"{item.Code} - {item.Name}",
            BeforeJson = JsonSerializer.Serialize(new { status = previousStatus }),
            AfterJson = JsonSerializer.Serialize(new { status }),
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = userAgent.Length > 255 ? userAgent[..255] : userAgent,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await database.SaveChangesAsync(ct);
        return Ok(item);
    }

    private IActionResult? ValidateItemTextFields(string? description, string? notes)
    {
        var errors = new Dictionary<string, string[]>();
        if (description?.Length > 5000) errors["description"] = ["A descrição deve ter no máximo 5000 caracteres."];
        if (notes?.Length > 5000) errors["notes"] = ["As observações devem ter no máximo 5000 caracteres."];
        return errors.Count == 0 ? null : ValidationProblem(errors);
    }

    private IActionResult? ValidateItemUpdateText(JsonElement value, string field, out bool provided, out string? text)
    {
        provided = value.ValueKind != JsonValueKind.Undefined;
        text = null;
        if (!provided || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String)
            return ValidationProblem(new Dictionary<string, string[]> { [field] = ["O campo deve ser um texto."] });
        text = value.GetString();
        var label = field == "description" ? "A descrição" : "As observações";
        return text?.Length > 5000
            ? ValidationProblem(new Dictionary<string, string[]> { [field] = [$"{label} deve ter no máximo 5000 caracteres."] })
            : null;
    }

    private IActionResult? ValidateItemUpdateEntryDate(JsonElement value, out bool provided, out DateOnly? entryDate)
    {
        provided = value.ValueKind != JsonValueKind.Undefined;
        entryDate = null;
        if (!provided || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String)
        {
            var raw = value.GetString()?.Trim();
            if (string.IsNullOrEmpty(raw)) return null;
            if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                entryDate = parsed;
                return null;
            }
        }
        return ValidationProblem(new Dictionary<string, string[]> { ["entryDate"] = ["Data inválida (use AAAA-MM-DD)."] });
    }

    private IActionResult? ValidateItemUpdateMinStock(JsonElement value, out bool provided, out int minStock)
    {
        provided = value.ValueKind != JsonValueKind.Undefined;
        minStock = 0;
        if (!provided || value.ValueKind == JsonValueKind.Null) return null;
        var valid = value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out minStock);
        if (value.ValueKind == JsonValueKind.String)
        {
            var raw = value.GetString()?.Trim();
            if (string.IsNullOrEmpty(raw)) return null;
            valid = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out minStock);
        }
        if (!valid)
            return ValidationProblem(new Dictionary<string, string[]> { ["minStock"] = ["Informe um número inteiro."] });
        return minStock is < 0 or > 1_000_000
            ? ValidationProblem(new Dictionary<string, string[]> { ["minStock"] = ["O estoque mínimo deve estar entre 0 e 1000000."] })
            : null;
    }

    private IActionResult? ValidateItemUpdateUnitValue(JsonElement value, out bool provided, out decimal? unitValue)
    {
        provided = value.ValueKind != JsonValueKind.Undefined;
        unitValue = null;
        if (!provided || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var parsed))
            return ValidationProblem(new Dictionary<string, string[]> { ["unitValue"] = ["Informe um valor numérico válido."] });
        if (parsed is < 0 or > 9_999_999_999m)
            return ValidationProblem(new Dictionary<string, string[]> { ["unitValue"] = ["O valor unitário deve estar entre 0 e 9999999999."] });
        unitValue = parsed;
        return null;
    }

    private static bool TryReadNullableGuid(JsonElement value, out Guid? id)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            id = null;
            return true;
        }

        if (value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var parsed))
        {
            id = parsed;
            return true;
        }

        id = null;
        return false;
    }

    private Guid? CurrentActorId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ? actorId : null;

    private static bool ValidKind(string? kind) => kind is null or "fisico" or "voucher" or "cartao" or "outro";

    private static bool ValidCode(string code) =>
        code.Length is > 0 and <= 40 &&
        char.IsAsciiLetterOrDigit(code[0]) &&
        code.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '/');

    private static Dictionary<string, object?> ItemAuditSnapshot(CatalogItem item) => new()
    {
        ["code"] = item.Code,
        ["name"] = item.Name,
        ["description"] = item.Description,
        ["category_id"] = item.CategoryId,
        ["industry_id"] = item.IndustryId,
        ["location_id"] = item.LocationId,
        ["supplier_id"] = item.SupplierId,
        ["kind"] = item.Kind,
        ["unit_value"] = item.UnitValue,
        ["min_stock"] = item.MinStock,
        ["status"] = item.Status,
        ["entry_date"] = item.EntryDate,
        ["notes"] = item.Notes
    };

    private static object ItemTrashAuditSnapshot(CatalogItem item) => new
    {
        id = item.Id,
        code = item.Code,
        name = item.Name,
        description = item.Description,
        category_id = item.CategoryId,
        industry_id = item.IndustryId,
        location_id = item.LocationId,
        supplier_id = item.SupplierId,
        kind = item.Kind,
        unit_value = item.UnitValue,
        min_stock = item.MinStock,
        status = item.Status,
        entry_date = item.EntryDate,
        notes = item.Notes,
        created_at = item.CreatedAt,
        updated_at = item.UpdatedAt,
        deleted_at = item.DeletedAt
    };

    private static JsonElement? ParseSnapshot(string? json)
    {
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string ItemHistoryActionLabel(string kind, string action) => kind == "movement"
        ? action switch
        {
            "entrada" => "Entrada",
            "saida" => "Saída",
            "ajuste" => "Ajuste",
            "transferencia" => "Transferência",
            "estorno" => "Estorno / correção",
            _ => action
        }
        : action switch
        {
            "create" => "Cadastro",
            "update" => "Alteração",
            "delete" => "Exclusão (lixeira)",
            "restore" => "Restauração",
            "purge" => "Exclusão definitiva",
            "activate" => "Ativação",
            "deactivate" => "Inativação",
            "photo_update" => "Foto alterada",
            "photo_delete" => "Foto removida",
            _ => action
        };
}

public sealed class CategoryWrite
{
    [StringLength(100)] public string? Name { get; set; }
    [StringLength(255)] public string? Description { get; set; }
    public bool? Active { get; set; }
}

public sealed class CategoryPurgeRequest
{
    [StringLength(150)] public string? Confirm { get; set; }
}

public sealed class ItemWrite
{
    [Required, StringLength(150, MinimumLength = 1)] public string Name { get; set; } = string.Empty;
    [StringLength(40)] public string? Code { get; set; }
    public string? Description { get; set; }
    [Required] public Guid CategoryId { get; set; }
    [JsonPropertyName("industryId")] public Guid? IndustryId { get; set; }
    [JsonPropertyName("industry_id")] public Guid? IndustryIdSnake { get; set; }
    public Guid? LocationId { get; set; }
    public Guid? SupplierId { get; set; }
    public string? Kind { get; set; }
    [Range(0, 9999999999)] public decimal? UnitValue { get; set; }
    [Range(0, 1000000)] public int? MinStock { get; set; }
    [Range(0, 1000000)] public int? InitialQuantity { get; set; }
    public string? Status { get; set; }
    public DateOnly? EntryDate { get; set; }
    public string? Notes { get; set; }
}

public sealed class ItemUpdate
{
    public JsonElement Name { get; set; }
    [StringLength(40)] public string? Code { get; set; }
    public JsonElement Description { get; set; }
    public JsonElement CategoryId { get; set; }
    [JsonPropertyName("industryId")] public JsonElement IndustryId { get; set; }
    [JsonPropertyName("industry_id")] public JsonElement IndustryIdSnake { get; set; }
    public JsonElement LocationId { get; set; }
    public JsonElement SupplierId { get; set; }
    public string? Kind { get; set; }
    public JsonElement UnitValue { get; set; }
    public JsonElement MinStock { get; set; }
    public JsonElement Status { get; set; }
    public JsonElement EntryDate { get; set; }
    public JsonElement Notes { get; set; }
}
