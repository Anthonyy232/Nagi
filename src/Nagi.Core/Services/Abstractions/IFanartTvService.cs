using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Service for fetching high-quality artist imagery from Fanart.tv.
/// </summary>
public interface IFanartTvService
{
    /// <summary>
    ///     Fetches artist images (backgrounds, logos, banners) using a MusicBrainz ID.
    /// </summary>
    /// <param name="musicBrainzId">The MusicBrainz artist ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result containing image URLs if successful.</returns>
    Task<ServiceResult<FanartTvArtistImages>> GetArtistImagesAsync(string musicBrainzId, CancellationToken cancellationToken = default);
}

/// <summary>
///     Container for Fanart.tv artist images.
/// </summary>
public record FanartTvArtistImages(
    string? BackgroundUrl,
    string? LogoUrl,
    string? BannerUrl,
    string? ThumbUrl
);
