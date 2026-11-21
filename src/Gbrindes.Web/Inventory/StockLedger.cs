namespace Gbrindes.Web.Inventory;

public enum StockMovementType { Entry, Exit, Adjustment, Transfer, Reversal }

public sealed record StockMovement(Guid Id, Guid ItemId, StockMovementType Type, decimal Quantity,
    Guid? FromLocationId, Guid? ToLocationId, string Reason, string IdempotencyKey, DateTimeOffset CreatedAt);

public sealed class StockLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<(Guid ItemId, Guid LocationId), decimal> _balances = [];
    private readonly List<StockMovement> _movements = [];
    private readonly Dictionary<string, StockMovement> _byIdempotencyKey = new(StringComparer.Ordinal);

    public IReadOnlyList<StockMovement> Movements
    {
        get { lock (_gate) return _movements.ToArray(); }
    }

    public decimal Balance(Guid itemId, Guid locationId)
    {
        lock (_gate) return _balances.GetValueOrDefault((itemId, locationId));
    }

    public StockMovement Apply(InventoryItem item, StockMovementType type, decimal quantity,
        Guid? fromLocationId, Guid? toLocationId, string reason, string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (quantity == 0 || (type != StockMovementType.Adjustment && quantity < 0))
            throw new ArgumentOutOfRangeException(nameof(quantity));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required.", nameof(reason));
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
        ValidateLocations(type, fromLocationId, toLocationId);
        var normalizedReason = reason.Trim();

        lock (_gate)
        {
            if (_byIdempotencyKey.TryGetValue(idempotencyKey, out var existing))
            {
                var matches = existing.ItemId == item.Id
                    && existing.Type == type
                    && existing.Quantity == quantity
                    && existing.FromLocationId == fromLocationId
                    && existing.ToLocationId == toLocationId
                    && existing.Reason == normalizedReason;
                if (!matches)
                    throw new InvalidOperationException("The idempotency key was already used with a different request.");

                return existing;
            }

            var changes = type switch
            {
                StockMovementType.Entry => new[] { (toLocationId!.Value, quantity) },
                StockMovementType.Exit => new[] { (fromLocationId!.Value, -quantity) },
                StockMovementType.Adjustment => new[] { (toLocationId!.Value, quantity) },
                StockMovementType.Transfer => new[] { (fromLocationId!.Value, -quantity), (toLocationId!.Value, quantity) },
                StockMovementType.Reversal => throw new NotSupportedException("Reversal requires a referenced movement and is not supported by this operation."),
                _ => throw new ArgumentOutOfRangeException(nameof(type))
            };

            foreach (var (location, delta) in changes)
                if (_balances.GetValueOrDefault((item.Id, location)) + delta < 0)
                    throw new InvalidOperationException("Stock cannot become negative.");

            foreach (var (location, delta) in changes)
                _balances[(item.Id, location)] = _balances.GetValueOrDefault((item.Id, location)) + delta;

            var movement = new StockMovement(Guid.NewGuid(), item.Id, type, quantity, fromLocationId, toLocationId,
                normalizedReason, idempotencyKey, DateTimeOffset.UtcNow);
            _movements.Add(movement);
            _byIdempotencyKey.Add(idempotencyKey, movement);
            return movement;
        }
    }

    public StockMovement Adjust(InventoryItem item, decimal delta, Guid locationId, string reason, string idempotencyKey) =>
        Apply(item, StockMovementType.Adjustment, delta, null, locationId, reason, idempotencyKey);

    private static void ValidateLocations(StockMovementType type, Guid? from, Guid? to)
    {
        var valid = type switch
        {
            StockMovementType.Entry or StockMovementType.Adjustment => from is null && to.HasValue,
            StockMovementType.Exit => from.HasValue && to is null,
            StockMovementType.Transfer => from.HasValue && to.HasValue && from != to,
            StockMovementType.Reversal => false,
            _ => false
        };
        if (!valid) throw new ArgumentException("Locations do not match the movement type.");
    }
}
