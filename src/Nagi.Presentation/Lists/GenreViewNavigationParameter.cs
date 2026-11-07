using Nagi.Presentation.Navigation;

namespace Nagi.Presentation.Lists;

public record GenreViewNavigationParameter : INavigationParameter
{
    public Guid GenreId { get; init; }
    public string GenreName { get; init; } = string.Empty;
}
