using Gbrindes.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Route("api/v1/stock/positions")]
[Authorize(Policy = "stock.view")]
public sealed class StockPositionsController(ApplicationDbContext database) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery(Name = "item_id")] Guid? itemId, CancellationToken ct)
    {
        if (itemId == Guid.Empty)
            return BadRequest(new { ok = false, error = new { code = "VALIDATION_ERROR", message = "Informe um brinde válido." } });

        var positions = await (
            from position in database.StockPositions.AsNoTracking()
            join item in database.CatalogItems.AsNoTracking() on position.ItemId equals item.Id
            join location in database.Locations.AsNoTracking() on position.LocationId equals location.Id
            where (!itemId.HasValue || position.ItemId == itemId.Value) && position.Quantity > 0 && item.DeletedAt == null
            orderby location.Kind, location.Name
            select new
            {
                item = new { id = item.Id, code = item.Code, name = item.Name },
                location = new { id = location.Id, name = location.Name, kind = location.Kind },
                qty = position.Quantity
            }).ToListAsync(ct);

        return Ok(new { ok = true, data = positions });
    }
}
