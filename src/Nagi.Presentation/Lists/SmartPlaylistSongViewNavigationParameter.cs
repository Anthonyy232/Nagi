using Nagi.Presentation.Navigation;

namespace Nagi.Presentation.Lists;

public record SmartPlaylistSongViewNavigationParameter : INavigationParameter
{
    public string Title { get; init; } = string.Empty;

    public Guid SmartPlaylistId { get; init; }

    public string? CoverImageUri { get; init; }
}
