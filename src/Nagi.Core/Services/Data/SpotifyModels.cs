using System.Text.Json.Serialization;

namespace Nagi.Core.Services.Data;

/// <summary>
///     Represents the top-level response from a Spotify search query.
/// </summary>
public class SpotifySearchResponse
{
    /// <summary>
    ///     A container for the artist results.
    /// </summary>
    [JsonPropertyName("artists")]
    public SpotifyArtists? Artists { get; set; }
}

/// <summary>
///     Represents a paged list of artists from the Spotify API.
/// </summary>
public class SpotifyArtists
{
    /// <summary>
    ///     An array of artist items.
    /// </summary>
    [JsonPropertyName("items")]
    public SpotifyArtistItem[]? Items { get; set; }
}

/// <summary>
///     Represents a single artist item from the Spotify API.
/// </summary>
public class SpotifyArtistItem
{
    /// <summary>
    ///     The name of the artist.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    ///     An array of images for the artist in various sizes.
    /// </summary>
    [JsonPropertyName("images")]
    public SpotifyImage[]? Images { get; set; }
}

/// <summary>
///     Represents an image from the Spotify API.
/// </summary>
public class SpotifyImage
{
    /// <summary>
    ///     The source URL of the image.
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    ///     The height of the image in pixels.
    /// </summary>
    [JsonPropertyName("height")]
    public int Height { get; set; }

    /// <summary>
    ///     The width of the image in pixels.
    /// </summary>
    [JsonPropertyName("width")]
    public int Width { get; set; }
}