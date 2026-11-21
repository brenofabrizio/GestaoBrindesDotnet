using System.Security.Claims;
using Gbrindes.Web.Data;
using Gbrindes.Web.Inventory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Route("api/v1/stock/by-industry")]
[Authorize(Policy = "stock.view")]
public sealed class StockByIndustryController(ApplicationDbContext database) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery(Name = "industry_id")] string? industryId, CancellationToken ct)
    {
        Guid? selectedIndustryId = Guid.TryParse(industryId, out var parsedIndustryId) ? parsedIndustryId : null;
        if (Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            var userIndustryId = await database.Users.AsNoTracking()
                .Where(user => user.Id == userId)
                .Select(user => user.IndustryId)
                .SingleOrDefaultAsync(ct);
            if (userIndustryId.HasValue)
                selectedIndustryId = userIndustryId;
        }

        var rows = await (
            from movement in database.StockMovements.AsNoTracking()
            join item in database.CatalogItems.AsNoTracking() on movement.ItemId equals item.Id
            join industry in database.Industries.AsNoTracking() on (movement.IndustryId ?? item.IndustryId) equals (Guid?)industry.Id
            where item.DeletedAt == null
                && (movement.Type == "entrada" || movement.Type == "saida" || movement.Type == "ajuste" || movement.Type == "estorno")
                && (!selectedIndustryId.HasValue || (movement.IndustryId ?? item.IndustryId) == selectedIndustryId.Value)
            group movement by new
            {
                IndustryId = industry.Id,
                IndustryName = industry.Name,
                ItemId = item.Id,
                ItemCode = item.Code,
                ItemName = item.Name,
                ItemKind = item.Kind
            }
            into grouped
            orderby grouped.Key.IndustryName, grouped.Key.ItemName
            select new
            {
                industry = new { id = grouped.Key.IndustryId, name = grouped.Key.IndustryName },
                item = new { id = grouped.Key.ItemId, code = grouped.Key.ItemCode, name = grouped.Key.ItemName, kind = grouped.Key.ItemKind },
                received = grouped.Sum(movement => movement.Type == "entrada" ? movement.Quantity : 0),
                withdrawn = grouped.Sum(movement => movement.Type == "saida" ? -movement.Quantity : 0),
                saldo = grouped.Sum(movement => movement.Quantity)
            }).ToListAsync(ct);

        var unattributedReceiptAdditions = await StockIndustryAttribution.GetUnassignedReceiptAdditionsAsync(database, ct);
        var attributedRows = rows.Select(row =>
        {
            var addition = unattributedReceiptAdditions.TryGetValue((row.industry.id, row.item.id), out var quantity) ? quantity : 0;
            return new
            {
                row.industry,
                row.item,
                received = row.received + addition,
                row.withdrawn,
                saldo = row.saldo + addition
            };
        }).ToList();

        return Ok(new { ok = true, data = attributedRows });
    }
}
