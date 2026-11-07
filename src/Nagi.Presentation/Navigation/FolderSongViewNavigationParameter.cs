namespace Nagi.Presentation.Navigation;

public record FolderSongViewNavigationParameter : INavigationParameter
{
    public string Title { get; init; } = string.Empty;

    public Guid FolderId { get; init; }

    public bool IsServer { get; init; }
}
