using System;

namespace Nagi.WinUI.Navigation;

public record GenreViewNavigationParameter
{
    public Guid GenreId { get; init; }
    public string GenreName { get; init; } = string.Empty;
}
