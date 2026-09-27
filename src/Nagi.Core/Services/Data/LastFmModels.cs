using System.Text.Json.Serialization;

namespace Nagi.Core.Services.Data;

public class LastFmArtistResponse
{
    [JsonPropertyName("artist")] public LastFmArtist? Artist { get; set; }

    [JsonPropertyName("error")] public int? ErrorCode { get; set; }

    [JsonPropertyName("message")] public string? Message { get; set; }
}

public class LastFmArtist
{
    [JsonPropertyName("image")] public List<LastFmImage> Image { get; set; } = new();

    [JsonPropertyName("bio")] public LastFmBio? Bio { get; set; }
}

public class LastFmImage
{
    [JsonPropertyName("#text")] public string Url { get; set; } = string.Empty;

    [JsonPropertyName("size")] public string Size { get; set; } = string.Empty;
}

public class LastFmBio
{
    [JsonPropertyName("summary")] public string Summary { get; set; } = string.Empty;
}
