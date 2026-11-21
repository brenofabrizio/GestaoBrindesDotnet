using Gbrindes.Web.Inventory;

namespace Gbrindes.Tests;

public sealed class InventoryTests
{
    [Fact]
    public void Entry_then_exit_updates_balance_and_records_immutable_movements()
    {
        var item = new InventoryItem(Guid.NewGuid(), Guid.NewGuid(), "SKU-1", "Caneca", "un");
        var ledger = new StockLedger();
        var location = Guid.NewGuid();

        ledger.Apply(item, StockMovementType.Entry, 10, null, location, "recebimento", "entrada-1");
        ledger.Apply(item, StockMovementType.Exit, 3, location, null, "entrega", "saida-1");

        Assert.Equal(7, ledger.Balance(item.Id, location));
        Assert.Equal(2, ledger.Movements.Count);
        Assert.Throws<InvalidOperationException>(() => ledger.Apply(item, StockMovementType.Exit, 8, location, null, "excesso", "saida-2"));
        Assert.Equal(7, ledger.Balance(item.Id, location));
        Assert.Equal(2, ledger.Movements.Count);
    }

    [Fact]
    public void Reusing_an_idempotency_key_with_a_different_payload_is_rejected()
    {
        var item = new InventoryItem(Guid.NewGuid(), Guid.NewGuid(), "SKU-2", "Garrafa", "un");
        var ledger = new StockLedger();
        var location = Guid.NewGuid();

        ledger.Apply(item, StockMovementType.Entry, 10, null, location, "recebimento A", "same-key");

        Assert.Throws<InvalidOperationException>(() =>
            ledger.Apply(item, StockMovementType.Entry, 11, null, location, "recebimento B", "same-key"));
        Assert.Equal(10, ledger.Balance(item.Id, location));
        Assert.Single(ledger.Movements);
    }

    [Fact]
    public void Negative_inventory_adjustment_reduces_balance_and_records_a_signed_delta()
    {
        var item = new InventoryItem(Guid.NewGuid(), Guid.NewGuid(), "SKU-3", "Bolsa", "un");
        var ledger = new StockLedger();
        var location = Guid.NewGuid();
        ledger.Apply(item, StockMovementType.Entry, 8, null, location, "recebimento", "entry-key");

        var movement = ledger.Adjust(item, -3, location, "contagem física", "adjustment-key");

        Assert.Equal(5, ledger.Balance(item.Id, location));
        Assert.Equal(StockMovementType.Adjustment, movement.Type);
        Assert.Equal(-3, movement.Quantity);
    }
}
