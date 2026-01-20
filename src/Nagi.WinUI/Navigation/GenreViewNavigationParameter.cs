using System;

namespace Nagi.WinUI.Navigation;

/// <summary>
///     Encapsulates parameters for navigating to the genre detail view.
/// </summary>
public record GenreViewNavigationParameter
{
    /// <summary>
    ///     The unique identifier of the genre.
    /// </summary>
    public Guid GenreId { get; init; }

    /// <summary>
    ///     The name of the genre, for display purposes.
    /// </summary>
    public string GenreName { get; init; } = string.Empty;
}