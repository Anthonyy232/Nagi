using Nagi.Presentation.Navigation;

namespace Nagi.Presentation.Lists;

public record FolderSongViewNavigationParameter : INavigationParameter
{
    public string Title { get; init; } = string.Empty;

    public Guid FolderId { get; init; }
}
