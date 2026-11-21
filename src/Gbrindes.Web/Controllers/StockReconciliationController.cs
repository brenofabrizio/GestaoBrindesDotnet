using System.Security.Claims;
using Gbrindes.Web.Inventory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Route("api/v1/stock/reconcile")]
[Authorize(Policy = "stock.adjust")]
public sealed class StockReconciliationController(StockReconciliationService reconciliation) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Reconcile(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        try
        {
            var result = await reconciliation.ReconcileAllAsync(userId, ct);
            Response.Headers.CacheControl = "no-store";
            return Ok(new
            {
                ok = true,
                data = new
                {
                    total_items = result.TotalItems,
                    fixed_positions = result.FixedPositions,
                    fixed_movements = result.FixedMovements
                }
            });
        }
        catch (InventoryFailureException exception)
        {
            return StatusCode(exception.StatusCode, new
            {
                ok = false,
                error = new { code = exception.Code, message = exception.Message }
            });
        }
    }
}
