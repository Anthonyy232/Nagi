using System;

namespace Nagi.WinUI.Navigation;

/// <summary>
///     Encapsulates parameters required for navigating to the playlist song view.
/// </summary>
public class PlaylistSongViewNavigationParameter
{
    /// <summary>
    ///     Gets or sets the title to display on the song view page, typically the playlist's name.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the unique identifier of the playlist whose songs are to be displayed.
    ///     A null value indicates that no specific playlist is targeted.
    /// </summary>
    public Guid? PlaylistId { get; set; }
}