namespace Nagi.Core.Models;

public enum ServiceCategory
{
    Lyrics,

    Metadata
}

public class ServiceProviderSetting
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public ServiceCategory Category { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    ///     Priority order: lower is higher priority, 0 is highest.
    /// </summary>
    public int Order { get; set; }

    public string? Description { get; set; }
}
