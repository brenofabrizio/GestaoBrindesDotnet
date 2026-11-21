using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Gbrindes.Web.Data;
using Gbrindes.Web.Inventory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StockMovementEntity = Gbrindes.Web.Data.StockMovement;

namespace Gbrindes.Web.Controllers;

[ApiController]
[Route("api/v1/stock/transfers")]
[Authorize]
public sealed class StockTransfersController(StockTransferService transfers) : ControllerBase
{
    [HttpPost, Authorize(Policy = "stock.transfer")]
    public Task<IActionResult> Create(StockTransferRequest request, CancellationToken ct) => ExecuteTransferAsync(request, ct);

    [HttpPost("~/api/stock/transfers"), Authorize(Policy = "stock.transfer_or_exit")]
    public Task<IActionResult> LegacyCreate(StockTransferRequest request, CancellationToken ct) => ExecuteTransferAsync(request, ct);

    private async Task<IActionResult> ExecuteTransferAsync(StockTransferRequest request, CancellationToken ct)
    {
        var key = Request.Headers["Idempotency-Key"].ToString();
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z0-9_-]{8,80}$"))
            return BadRequest(new { ok = false, error = new { code = "IDEMPOTENCY_KEY_REQUIRED", message = "Cabeçalho Idempotency-Key ausente ou inválido." } });
        try
        {
            var userId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsedId) ? parsedId : (Guid?)null;
            var result = await transfers.TransferAsync(request.ItemId, request.Quantity, request.IndustryId,
                request.FromLocationId, request.ToLocationId, request.EventId, request.Notes, key,
                userId, User.Identity?.Name, ct);
            return StatusCode(StatusCodes.Status201Created, new
            {
                ok = true,
                data = new
                {
                    movements = new[] { Movement(result.Outgoing), Movement(result.Incoming) },
                    stock = new { on_hand = result.Stock.OnHand, reserved = result.Stock.Reserved, available = result.Stock.OnHand - result.Stock.Reserved },
                    from_location_id = request.FromLocationId ?? result.Outgoing.FromLocationId,
                    to_location_id = result.Incoming.ToLocationId,
                    quantity = request.Quantity,
                    event_id = request.EventId,
                    industry_id = request.IndustryId
                }
            });
        }
        catch (InventoryFailureException ex)
        {
            return StatusCode(ex.StatusCode, new { ok = false, error = new { code = ex.Code, message = ex.Message } });
        }
    }

    private static object Movement(StockMovementEntity movement) => new
    {
        id = movement.Id,
        item_id = movement.ItemId,
        type = movement.Type,
        quantity = movement.Quantity,
        balance_after = movement.BalanceAfter,
        location_id = movement.LocationId,
        from_location_id = movement.FromLocationId,
        to_location_id = movement.ToLocationId,
        transfer_id = movement.TransferId,
        created_at = movement.CreatedAt
    };
}

public sealed class StockTransferRequest
{
    [JsonPropertyName("item_id"), Required] public Guid ItemId { get; set; }
    [Range(1, 1_000_000)] public int Quantity { get; set; }
    [JsonPropertyName("industry_id"), Required] public Guid IndustryId { get; set; }
    [JsonPropertyName("from_location_id")] public Guid? FromLocationId { get; set; }
    [JsonPropertyName("to_location_id")] public Guid? ToLocationId { get; set; }
    [JsonPropertyName("event_id")] public Guid? EventId { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
}
