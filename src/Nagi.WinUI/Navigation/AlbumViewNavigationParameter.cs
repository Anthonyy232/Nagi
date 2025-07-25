using System;

namespace Nagi.WinUI.Navigation;

/// <summary>
///     Encapsulates parameters for navigating to the album detail view.
/// </summary>
public class AlbumViewNavigationParameter
{
    /// <summary>
    ///     The unique identifier of the album.
    /// </summary>
    public Guid AlbumId { get; set; }

    /// <summary>
    ///     The title of the album, for display purposes.
    /// </summary>
    public string AlbumTitle { get; set; } = string.Empty;

    /// <summary>
    ///     The name of the album's artist, for display purposes.
    /// </summary>
    public string ArtistName { get; set; } = string.Empty;
}