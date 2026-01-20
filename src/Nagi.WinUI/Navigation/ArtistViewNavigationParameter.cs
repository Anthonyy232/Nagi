using System;

namespace Nagi.WinUI.Navigation;

/// <summary>
///     Encapsulates parameters for navigating to the artist detail view.
/// </summary>
public record ArtistViewNavigationParameter
{
    /// <summary>
    ///     The unique identifier of the artist.
    /// </summary>
    public Guid ArtistId { get; init; }

    /// <summary>
    ///     The name of the artist, for display purposes.
    /// </summary>
    public string ArtistName { get; init; } = string.Empty;
}