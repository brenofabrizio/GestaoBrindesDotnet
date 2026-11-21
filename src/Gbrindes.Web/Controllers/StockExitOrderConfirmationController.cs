using System.Security.Claims;
using Gbrindes.Web.Inventory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Route("api/v1/stock/exit-orders")]
[Authorize(Policy = "stock.exit_confirm")]
public sealed class StockExitOrderConfirmationController(StockExitOrderService exitOrders) : ControllerBase
{
    [HttpPost("{id:guid}/confirm")]
    public async Task<IActionResult> Confirm(Guid id, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        var key = Request.Headers["Idempotency-Key"].ToString();
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z0-9_-]{8,80}$"))
            return BadRequest(new { ok = false, error = new { code = "IDEMPOTENCY_KEY_REQUIRED", message = "Cabeçalho Idempotency-Key ausente ou inválido." } });

        try
        {
            var result = await exitOrders.ConfirmAsync(id, userId, User.Identity?.Name ?? string.Empty, key, ct);
            if (result.Replayed)
                Response.Headers["Idempotent-Replayed"] = "true";
            var order = result.Order;
            return Ok(new
            {
                ok = true,
                data = new
                {
                    order = new
                    {
                        id = order.Id,
                        code = order.Code,
                        itemId = order.ItemId,
                        quantity = order.Quantity,
                        purpose = order.Purpose,
                        recipient = order.Recipient,
                        purchaseTicketNo = order.PurchaseTicketNo,
                        documentRef = order.DocumentRef,
                        notes = order.Notes,
                        status = order.Status,
                        authorizedBy = order.AuthorizedBy,
                        authorizedAt = order.AuthorizedAt,
                        movementId = order.MovementId,
                        confirmedBy = order.ConfirmedBy,
                        confirmedAt = order.ConfirmedAt,
                        attachment_url = order.AttachmentUrl
                    },
                    stock = new
                    {
                        onHand = result.OnHand,
                        reserved = result.Reserved,
                        available = result.OnHand - result.Reserved
                    }
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
