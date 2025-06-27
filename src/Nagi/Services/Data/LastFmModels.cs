using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nagi.Services.Data.LastFm {
    // Used to deserialize the root of the Last.fm response
    public class LastFmArtistResponse {
        [JsonPropertyName("artist")]
        public LastFmArtist? Artist { get; set; }
    }

    public class LastFmArtist {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("image")]
        public List<LastFmImage> Image { get; set; } = new();

        [JsonPropertyName("bio")]
        public LastFmBio? Bio { get; set; }
    }

    public class LastFmImage {
        [JsonPropertyName("#text")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public string Size { get; set; } = string.Empty;
    }

    public class LastFmBio {
        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;
    }

    public class LastFmErrorResponse {
        [JsonPropertyName("error")]
        public int ErrorCode { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }
}