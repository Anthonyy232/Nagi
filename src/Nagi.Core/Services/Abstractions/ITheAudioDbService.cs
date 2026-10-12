using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

public interface ITheAudioDbService
{
    /// <summary>
    ///     Fetches artist metadata (biography, images) using a MusicBrainz artist ID.
    /// </summary>
    Task<ServiceResult<TheAudioDbArtistInfo>> GetArtistMetadataAsync(
        string musicBrainzId,
        string? languageCode = null,
        CancellationToken cancellationToken = default);
}

/// <param name="Biography">Localized biography, falling back to English when unavailable.</param>
/// <param name="WideThumbUrl">Wide banner-style image URL.</param>
/// <param name="LogoUrl">Artist logo (transparent PNG) URL.</param>
public record TheAudioDbArtistInfo(
    string? Biography,
    string? ThumbUrl,
    string? FanartUrl,
    string? WideThumbUrl,
    string? LogoUrl
);
