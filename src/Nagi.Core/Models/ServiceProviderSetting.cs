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

/// <summary>
///     Represents the configuration for a single external service provider.
/// </summary>
public class ServiceProviderSetting
{
    /// <summary>
    ///     Gets or sets the unique identifier for this service (e.g., "lrclib", "netease", "lastfm").
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the display name shown in the UI (e.g., "LRCLIB", "NetEase Music 163", "Last.fm").
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the service category.
    /// </summary>
    public ServiceCategory Category { get; set; }

    /// <summary>
    ///     Gets or sets whether this service is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    ///     Gets or sets the priority order (lower = higher priority, 0 = highest).
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    ///     Gets or sets the optional description for the service.
    /// </summary>
    public string? Description { get; set; }
}
