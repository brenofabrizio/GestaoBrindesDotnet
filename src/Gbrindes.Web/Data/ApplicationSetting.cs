using Gbrindes.Web.Identity;

namespace Gbrindes.Web.Data;

public sealed class ApplicationSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ApplicationUser? UpdatedByUser { get; set; }
}
