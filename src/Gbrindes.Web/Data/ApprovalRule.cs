using Gbrindes.Web.Identity;

namespace Gbrindes.Web.Data;

public sealed class ApprovalRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Criterion { get; set; } = string.Empty;
    public string Operator { get; set; } = ">";
    public string Value { get; set; } = string.Empty;
    public Guid? ApproverRoleId { get; set; }
    public Guid? ApproverUserId { get; set; }
    public int Priority { get; set; } = 100;
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    public ApplicationRole? ApproverRole { get; set; }
    public ApplicationUser? ApproverUser { get; set; }
}

public sealed class RequestApproval
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public Guid? RuleId { get; set; }
    public Guid? ApproverRoleId { get; set; }
    public Guid? ApproverUserId { get; set; }
    public string Decision { get; set; } = "pendente";
    public string? Justification { get; set; }
    public Guid? DecidedById { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public InternalRequest Request { get; set; } = null!;
    public ApprovalRule? Rule { get; set; }
    public ApplicationUser? ApproverUser { get; set; }
    public ApplicationUser? DecidedBy { get; set; }
}
