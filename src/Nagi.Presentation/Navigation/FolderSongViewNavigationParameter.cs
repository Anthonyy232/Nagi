namespace Nagi.Presentation.Navigation;

public record FolderSongViewNavigationParameter : INavigationParameter
{
    public string Title { get; init; } = string.Empty;

    public Guid FolderId { get; init; }

    /// <summary>
    ///     Gets or sets the directory path within the folder to display.
    ///     If null or empty, the root of the folder is displayed.
    /// </summary>
    public string? DirectoryPath { get; init; }
}
