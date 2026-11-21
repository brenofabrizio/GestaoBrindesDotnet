using Microsoft.AspNetCore.Identity;

namespace Gbrindes.Web.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public ApplicationUser() => Id = Guid.NewGuid();

    public string FullName { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? IndustryId { get; set; }
    public DateTimeOffset? PasswordChangedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
}
