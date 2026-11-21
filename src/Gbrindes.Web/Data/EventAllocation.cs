namespace Gbrindes.Web.Data;

public sealed class EventAllocation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EventId { get; set; }

    public Guid IndustryId { get; set; }

    public Guid ItemId { get; set; }

    public int QuantityAllocated { get; set; }

    public int QuantityWithdrawn { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
