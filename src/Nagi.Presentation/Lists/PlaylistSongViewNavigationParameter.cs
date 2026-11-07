using Nagi.Presentation.Navigation;

namespace Nagi.Presentation.Lists;

public record PlaylistSongViewNavigationParameter : INavigationParameter
{
    public string Title { get; init; } = string.Empty;

    public Guid PlaylistId { get; init; }

    public string? CoverImageUri { get; init; }
}
