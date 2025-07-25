using System;

namespace Nagi.WinUI.Navigation;

/// <summary>
///     Provides parameters for navigating to a view that displays songs from a specific folder.
/// </summary>
public class FolderSongViewNavigationParameter
{
    /// <summary>
    ///     Gets or sets the title to display on the song view page (e.g., the folder's name).
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the unique identifier of the folder whose songs are to be displayed.
    /// </summary>
    public Guid FolderId { get; set; }
}