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
