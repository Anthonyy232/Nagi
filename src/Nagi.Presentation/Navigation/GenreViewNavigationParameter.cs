namespace Nagi.Presentation.Navigation;

public record GenreViewNavigationParameter : INavigationParameter
{
    public Guid GenreId { get; init; }
    public string GenreName { get; init; } = string.Empty;
}
