namespace Nagi.Presentation.Navigation;

public record FolderSongViewNavigationParameter : INavigationParameter
{
    public string Title { get; init; } = string.Empty;

    public Guid FolderId { get; init; }

    /// <summary>
    ///     The directory within the folder to show; null or empty shows the folder's root.
    /// </summary>
    public string? DirectoryPath { get; init; }
}
