using System;

namespace Nagi.WinUI.Navigation;

/// <summary>
///     Encapsulates parameters required for navigating to the smart playlist song view.
/// </summary>
public record SmartPlaylistSongViewNavigationParameter
{
    /// <summary>
    ///     Gets or sets the title to display on the song view page, typically the smart playlist's name.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    ///     Gets or sets the unique identifier of the smart playlist whose matching songs are to be displayed.
    ///     A null value indicates that no specific smart playlist is targeted.
    /// </summary>
    public Guid? SmartPlaylistId { get; init; }

    /// <summary>
    ///     Gets or sets the URI to the smart playlist's cover image.
    /// </summary>
    public string? CoverImageUri { get; init; }
}
