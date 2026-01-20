using Nagi.Core.Models;

namespace Nagi.Core.Helpers;

/// <summary>
///     Provides centralized normalization logic for artist names to ensure consistency across the application.
/// </summary>
public static class ArtistNameHelper
{
    /// <summary>
    ///     Normalizes an artist name by trimming whitespace. 
    ///     Returns <see cref="Artist.UnknownArtistName"/> if the name is null or whitespace.
    /// </summary>
    public static string Normalize(string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? Artist.UnknownArtistName : name.Trim();
    }
}
