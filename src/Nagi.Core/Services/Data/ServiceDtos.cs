namespace Nagi.Core.Services.Data;

/// <summary>
///     Represents structured artist information retrieved from a metadata service.
/// </summary>
public class ArtistInfo
{
    /// <summary>
    ///     A summary of the artist's biography.
    /// </summary>
    public string? Biography { get; set; }

    /// <summary>
    ///     A URL to an image of the artist.
    /// </summary>
    public string? ImageUrl { get; set; }
}

/// <summary>
///     Represents the result of a Spotify image search.
/// </summary>
public class SpotifyImageResult
{
    /// <summary>
    ///     The URL of the found image.
    /// </summary>
    public string ImageUrl { get; set; } = string.Empty;
}