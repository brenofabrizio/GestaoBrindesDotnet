using Microsoft.AspNetCore.Identity;

namespace Gbrindes.Web.Identity;

public sealed class ApplicationRole : IdentityRole<Guid>
{
    public string? Slug { get; set; }
    public string? Description { get; set; }

    public ApplicationRole() { }

    public ApplicationRole(string roleName) : base(roleName) { }
}
