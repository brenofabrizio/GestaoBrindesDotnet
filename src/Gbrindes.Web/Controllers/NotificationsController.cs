using System.Security.Claims;
using Gbrindes.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/notifications")]
public sealed class NotificationsController(ApplicationDbContext database) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool unread = false, [FromQuery] int page = 1,
        [FromQuery(Name = "per_page")] int perPage = 25, CancellationToken ct = default)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        page = Math.Clamp(page, 1, 1_000_000);
        perPage = Math.Clamp(perPage, 1, 100);
        var inbox = database.Notifications.AsNoTracking()
            .Where(notification => notification.UserId == userId && notification.Channel == "in_app");
        var unreadCount = await inbox.CountAsync(notification => notification.ReadAt == null, ct);
        var query = unread ? inbox.Where(notification => notification.ReadAt == null) : inbox;
        var total = await query.CountAsync(ct);
        var lastPage = Math.Max(1, (int)Math.Ceiling(total / (double)perPage));
        var notifications = await query
            .OrderByDescending(notification => notification.CreatedAt)
            .ThenByDescending(notification => notification.Id)
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .Select(notification => new
            {
                id = notification.Id,
                subject = notification.Subject,
                body = notification.Body,
                link_url = notification.LinkUrl,
                related_type = notification.RelatedType,
                related_id = notification.RelatedId,
                read_at = notification.ReadAt,
                created_at = notification.CreatedAt
            })
            .ToListAsync(ct);

        return Ok(new { ok = true, data = notifications, meta = new { page, per_page = perPage, total, last_page = lastPage, unread = unreadCount } });
    }

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        var notification = await database.Notifications.SingleOrDefaultAsync(entity =>
            entity.Id == id && entity.UserId == userId && entity.Channel == "in_app", ct);
        if (notification is not null && notification.ReadAt is null)
        {
            notification.ReadAt = DateTimeOffset.UtcNow;
            await database.SaveChangesAsync(ct);
        }

        return Ok(new { ok = true, data = new { read = true } });
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        var unread = await database.Notifications
            .Where(notification => notification.UserId == userId && notification.Channel == "in_app" && notification.ReadAt == null)
            .ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var notification in unread)
            notification.ReadAt = now;
        if (unread.Count > 0)
            await database.SaveChangesAsync(ct);

        return Ok(new { ok = true, data = new { read = true } });
    }
}
