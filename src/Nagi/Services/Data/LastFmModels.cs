using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nagi.Services.Data;

/// <summary>
///     Represents the root object of a Last.fm artist.getinfo API response.
/// </summary>
public class LastFmArtistResponse
{
    [JsonPropertyName("artist")] public LastFmArtist? Artist { get; set; }
}

/// <summary>
///     Represents the detailed artist information provided by the Last.fm API.
/// </summary>
public class LastFmArtist
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("image")] public List<LastFmImage> Image { get; set; } = new();

    [JsonPropertyName("bio")] public LastFmBio? Bio { get; set; }
}

/// <summary>
///     Represents an image URL for an artist, with a specific size.
/// </summary>
public class LastFmImage
{
    [JsonPropertyName("#text")] public string Url { get; set; } = string.Empty;

    [JsonPropertyName("size")] public string Size { get; set; } = string.Empty;
}

/// <summary>
///     Represents the biography of an artist.
/// </summary>
public class LastFmBio
{
    [JsonPropertyName("summary")] public string Summary { get; set; } = string.Empty;
}

/// <summary>
///     Represents a structured error response from the Last.fm API.
/// </summary>
public class LastFmErrorResponse
{
    [JsonPropertyName("error")] public int ErrorCode { get; set; }

    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
}