using System;

namespace Nagi.WinUI.Navigation;

public record PlaylistSongViewNavigationParameter : INavigationParameter
{
    public string Title { get; init; } = string.Empty;

    /// <summary>
    ///     Gets or sets the unique identifier of the playlist whose songs are to be displayed.
    ///     A null value indicates that no specific playlist is targeted.
    /// </summary>
    public Guid? PlaylistId { get; init; }

    public string? CoverImageUri { get; init; }
}
