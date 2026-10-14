using System;

namespace Nagi.Presentation.Navigation;

public record SmartPlaylistSongViewNavigationParameter : INavigationParameter
{
    public string Title { get; init; } = string.Empty;

    /// <summary>
    ///     Gets or sets the unique identifier of the smart playlist whose matching songs are to be displayed.
    ///     A null value indicates that no specific smart playlist is targeted.
    /// </summary>
    public Guid? SmartPlaylistId { get; init; }

    public string? CoverImageUri { get; init; }
}
