using System;

namespace Nagi.WinUI.Navigation;

/// <summary>
///     Encapsulates parameters for navigating to the album detail view.
/// </summary>
public record AlbumViewNavigationParameter
{
    /// <summary>
    ///     The unique identifier of the album.
    /// </summary>
    public Guid AlbumId { get; init; }

    /// <summary>
    ///     The title of the album, for display purposes.
    /// </summary>
    public string AlbumTitle { get; init; } = string.Empty;

    /// <summary>
    ///     The name of the album's artist, for display purposes.
    /// </summary>
    public string ArtistName { get; init; } = string.Empty;
}