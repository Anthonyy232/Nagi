using System;

namespace Nagi.WinUI.Navigation;

/// <summary>
///     Encapsulates parameters for navigating to the genre detail view.
/// </summary>
public class GenreViewNavigationParameter
{
    /// <summary>
    ///     The unique identifier of the genre.
    /// </summary>
    public Guid GenreId { get; set; }

    /// <summary>
    ///     The name of the genre, for display purposes.
    /// </summary>
    public string GenreName { get; set; } = string.Empty;
}