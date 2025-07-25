using System;

namespace Nagi.WinUI.Navigation;

/// <summary>
///     Encapsulates parameters for navigating to the artist detail view.
/// </summary>
public class ArtistViewNavigationParameter
{
    /// <summary>
    ///     The unique identifier of the artist.
    /// </summary>
    public Guid ArtistId { get; set; }

    /// <summary>
    ///     The name of the artist, for display purposes.
    /// </summary>
    public string ArtistName { get; set; } = string.Empty;
}