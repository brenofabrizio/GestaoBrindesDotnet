using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Gbrindes.Web.Inventory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Route("api/v1/stock/exit-orders")]
[Authorize(Policy = "stock.exit")]
public sealed class StockExitOrderCancellationController(StockExitOrderService exitOrders) : ControllerBase
{
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, StockExitOrderCancellationRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();
        try
        {
            var result = await exitOrders.CancelAsync(id, request.Reason, userId, User.Identity?.Name ?? string.Empty, ct);
            return Ok(new
            {
                ok = true,
                data = new
                {
                    order = new StockExitOrderView(result.Order.Id, result.Order.Code, result.Order.ItemId,
                        result.Order.Quantity, result.Order.Purpose, result.Order.Recipient, result.Order.PurchaseTicketNo,
                        result.Order.DocumentRef, result.Order.Notes, result.Order.Status, result.Order.AuthorizedBy,
                        result.Order.AuthorizedAt, result.Order.AttachmentUrl),
                    stock = new { onHand = result.OnHand, reserved = result.Reserved, available = result.OnHand - result.Reserved }
                }
            });
        }
        catch (InventoryFailureException exception)
        {
            return StatusCode(exception.StatusCode, new ProblemDetails
            {
                Status = exception.StatusCode,
                Title = exception.Code,
                Detail = exception.Message
            });
        }
    }
}

public sealed class StockExitOrderCancellationRequest
{
    [StringLength(255)]
    public string? Reason { get; set; }
}