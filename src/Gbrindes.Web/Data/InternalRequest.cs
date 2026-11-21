using Gbrindes.Web.Identity;

namespace Gbrindes.Web.Data;

public sealed class InternalRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string? PublicCode { get; set; }
    public Guid RequesterId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? IndustryId { get; set; }
    public string Flow { get; set; } = "interna";
    public string Status { get; set; } = "rascunho";
    public string Purpose { get; set; } = string.Empty;
    public string? Recipient { get; set; }
    public DateOnly? NeededDate { get; set; }
    public string? Notes { get; set; }
    public string? PurchaseTicketNo { get; set; }
    public string? InvoiceNo { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string? InvoicePath { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string? ImportIdentityHash { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string? ImportContentHash { get; set; }
    public bool NeedsPurchase { get; set; }
    public decimal TotalValue { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset? StatusChangedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public ApplicationUser Requester { get; set; } = null!;
    public Department? Department { get; set; }
    public Industry? Industry { get; set; }
    public ICollection<InternalRequestItem> Items { get; set; } = new List<InternalRequestItem>();
    public ICollection<InternalRequestStatusHistory> History { get; set; } = new List<InternalRequestStatusHistory>();
    public ICollection<RequestApproval> Approvals { get; set; } = new List<RequestApproval>();
}

public sealed class InternalRequestItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public Guid ItemId { get; set; }
    public int Position { get; set; }
    public int QuantityRequested { get; set; }
    public int? QuantityApproved { get; set; }
    public int QuantityReserved { get; set; }
    public int QuantityReceived { get; set; }
    public int QuantityDelivered { get; set; }
    public decimal? UnitValue { get; set; }
    public string? Notes { get; set; }
    public InternalRequest Request { get; set; } = null!;
    public CatalogItem Item { get; set; } = null!;
}

public sealed class InternalRequestStatusHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public InternalRequest Request { get; set; } = null!;
}
