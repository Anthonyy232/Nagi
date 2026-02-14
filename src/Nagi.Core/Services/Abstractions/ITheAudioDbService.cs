using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Service for fetching artist metadata from TheAudioDB.
/// </summary>
public interface ITheAudioDbService
{
    /// <summary>
    ///     Fetches artist metadata (biography, images) using a MusicBrainz artist ID.
    /// </summary>
    /// <param name="musicBrainzId">The MusicBrainz artist ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result containing artist metadata if successful.</returns>
    Task<ServiceResult<TheAudioDbArtistInfo>> GetArtistMetadataAsync(
        string musicBrainzId,
        string? languageCode = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Container for TheAudioDB artist metadata.
/// </summary>
/// <param name="Biography">English biography of the artist.</param>
/// <param name="ThumbUrl">Artist thumbnail image URL.</param>
/// <param name="FanartUrl">Artist fanart image URL.</param>
/// <param name="WideThumbUrl">Wide banner-style image URL.</param>
/// <param name="LogoUrl">Artist logo (transparent PNG) URL.</param>
public record TheAudioDbArtistInfo(
    string? Biography,
    string? ThumbUrl,
    string? FanartUrl,
    string? WideThumbUrl,
    string? LogoUrl
);
