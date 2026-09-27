namespace Nagi.Core.Models;

/// <summary>
///     Defines categories for external service providers.
/// </summary>
public enum ServiceCategory
{
    /// <summary>
    ///     Services that provide synchronized lyrics (LRC).
    /// </summary>
    Lyrics,

    /// <summary>
    ///     Services that provide artist/album metadata like images and biographies.
    /// </summary>
    Metadata
}

public class ServiceProviderSetting
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public ServiceCategory Category { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    ///     Gets or sets the priority order (lower = higher priority, 0 = highest).
    /// </summary>
    public int Order { get; set; }

    public string? Description { get; set; }
}
