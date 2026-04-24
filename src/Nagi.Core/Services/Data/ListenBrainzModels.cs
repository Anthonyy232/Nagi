using System.Text.Json.Serialization;

namespace Nagi.Core.Services.Data;

/// <summary>
///     Payload for POST /1/submit-listens. See https://listenbrainz.readthedocs.io/en/latest/users/api/core.html
/// </summary>
public class ListenBrainzSubmitPayload
{
    [JsonPropertyName("listen_type")]
    public string ListenType { get; set; } = "single";

    [JsonPropertyName("payload")]
    public List<ListenBrainzListen> Payload { get; set; } = new();
}

public class ListenBrainzListen
{
    /// <summary>
    ///     Unix epoch seconds. Required for listen_type "single"; must be omitted for "playing_now".
    /// </summary>
    [JsonPropertyName("listened_at")]
    public long? ListenedAt { get; set; }

    [JsonPropertyName("track_metadata")]
    public ListenBrainzTrackMetadata TrackMetadata { get; set; } = new();
}

public class ListenBrainzTrackMetadata
{
    [JsonPropertyName("artist_name")]
    public string ArtistName { get; set; } = string.Empty;

    [JsonPropertyName("track_name")]
    public string TrackName { get; set; } = string.Empty;

    [JsonPropertyName("release_name")]
    public string? ReleaseName { get; set; }
}

/// <summary>Response from GET /1/validate-token.</summary>
public class ListenBrainzValidateTokenResponse
{
    [JsonPropertyName("valid")]
    public bool Valid { get; set; }

    [JsonPropertyName("user_name")]
    public string? UserName { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>
///     Result of validating a user token. Surfaces the resolved username on success so the
///     settings UI can show it.
/// </summary>
public record ValidateTokenResult(bool IsValid, string? Username, string? Error);
