using System;

namespace Nagi.WinUI.Navigation;

/// <summary>
///     Provides parameters for navigating to a view that displays songs from a specific folder or directory.
/// </summary>
public record FolderSongViewNavigationParameter
{
    /// <summary>
    ///     Gets or sets the title to display on the song view page (e.g., the folder's name).
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    ///     Gets or sets the unique identifier of the root folder whose contents are to be displayed.
    /// </summary>
    public Guid FolderId { get; init; }

    /// <summary>
    ///     Gets or sets the directory path within the folder to display.
    ///     If null or empty, the root of the folder is displayed.
    /// </summary>
    public string? DirectoryPath { get; init; }
}