using Gbrindes.Web.Data;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Route("api/v1/dashboard")]
[Authorize(Policy = "dashboard.view")]
public sealed class DashboardController(ApplicationDbContext database) : ControllerBase
{
    [HttpGet("request-summary")]
    public async Task<IActionResult> RequestSummary([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedStart)
            ? parsedStart : new DateOnly(today.Year, today.Month, 1);
        var end = DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedEnd)
            ? parsedEnd : today;
        if (start > end) (start, end) = (end, start);

        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdValue, out var userId)) return Forbid();
        var scope = await database.Users.AsNoTracking().Where(user => user.Id == userId)
            .Select(user => new { user.IndustryId, user.DepartmentId }).SingleOrDefaultAsync(ct);

        var requests = database.Requests.AsNoTracking()
            .Where(request => request.Flow == "interna" && request.DeletedAt == null && request.Status != "rascunho");
        if (scope?.IndustryId is Guid industryId)
            requests = requests.Where(request => request.IndustryId == industryId);
        else if (!User.IsInRole("admin") && !User.HasClaim("permission", "requests.view_all"))
        {
            if (User.HasClaim("permission", "requests.view_department") && scope?.DepartmentId is Guid departmentId)
                requests = requests.Where(request => request.DepartmentId == departmentId || request.RequesterId == userId);
            else
                requests = requests.Where(request => request.RequesterId == userId);
        }

        var finalizedFrom = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var finalizedTo = end == DateOnly.MaxValue
            ? DateTimeOffset.MaxValue
            : new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var totals = await requests.GroupBy(_ => 1).Select(group => new
        {
            pending = group.Count(request => request.Status == "solicitada" || request.Status == "aguardando_aprovacao" || request.Status == "aprovada" || request.Status == "em_separacao" || request.Status == "pronta"),
            awaiting_approval = group.Count(request => request.Status == "aguardando_aprovacao"),
            to_prepare = group.Count(request => request.Status == "aprovada" || request.Status == "em_separacao"),
            ready = group.Count(request => request.Status == "pronta"),
            finalized_period = group.Count(request => request.Status == "finalizada" && request.StatusChangedAt >= finalizedFrom && request.StatusChangedAt < finalizedTo)
        }).SingleOrDefaultAsync(ct);

        return Ok(new
        {
            period = new { from = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            pending = totals?.pending ?? 0,
            awaiting_approval = totals?.awaiting_approval ?? 0,
            to_prepare = totals?.to_prepare ?? 0,
            ready = totals?.ready ?? 0,
            finalized_period = totals?.finalized_period ?? 0
        });
    }

    [HttpGet("stock-summary")]
    public async Task<IActionResult> StockSummary(CancellationToken ct)
    {
        var activeStocks = database.Stocks.AsNoTracking()
            .Where(stock => stock.Item.DeletedAt == null && stock.Item.Status == "ativo");
        var totals = await activeStocks.GroupBy(_ => 1)
            .Select(group => new
            {
                items_active = group.Count(),
                units_on_hand = group.Sum(stock => stock.OnHand),
                units_reserved = group.Sum(stock => stock.Reserved),
                stock_value = group.Sum(stock => stock.OnHand * (stock.Item.UnitValue ?? 0m)),
                low_stock = group.Count(stock => stock.Item.MinStock > 0 && stock.OnHand - stock.Reserved > 0 && stock.OnHand - stock.Reserved <= stock.Item.MinStock),
                zero_stock = group.Count(stock => stock.OnHand - stock.Reserved <= 0)
            }).SingleOrDefaultAsync(ct);

        var canViewValue = User.HasClaim("permission", "stock.view");
        return Ok(new
        {
            items_active = totals?.items_active ?? 0,
            units_on_hand = totals?.units_on_hand ?? 0,
            units_reserved = totals?.units_reserved ?? 0,
            units_available = (totals?.units_on_hand ?? 0) - (totals?.units_reserved ?? 0),
            stock_value = canViewValue ? Math.Round(totals?.stock_value ?? 0m, 2) : (decimal?)null,
            low_stock = totals?.low_stock ?? 0,
            zero_stock = totals?.zero_stock ?? 0
        });
    }

    [HttpGet("stock-alerts")]
    public async Task<IActionResult> StockAlerts(CancellationToken ct)
    {
        var rows = await database.Stocks.AsNoTracking()
            .Where(stock => stock.Item.DeletedAt == null && stock.Item.Status == "ativo")
            .Select(stock => new
            {
                item = new { id = stock.ItemId, code = stock.Item.Code, name = stock.Item.Name },
                available = stock.OnHand - stock.Reserved,
                min_stock = stock.Item.MinStock
            })
            .Where(row => row.available <= 0 || (row.min_stock > 0 && row.available <= row.min_stock))
            .OrderBy(row => row.available)
            .ThenBy(row => row.item.name)
            .Take(10)
            .ToListAsync(ct);

        return Ok(rows.Select(row =>
        {
            var level = row.available <= 0 ? "zero" : "low";
            return new
            {
                row.item,
                row.available,
                row.min_stock,
                level,
                level_label = level == "zero" ? "Sem estoque" : "Estoque baixo"
            };
        }));
    }
}
