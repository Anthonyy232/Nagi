using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

public interface IFanartTvService
{
    /// <summary>
    ///     Fetches artist images (backgrounds, logos, banners) using a MusicBrainz ID.
    /// </summary>
    Task<ServiceResult<FanartTvArtistImages>> GetArtistImagesAsync(string musicBrainzId, CancellationToken cancellationToken = default);
}

public record FanartTvArtistImages(
    string? BackgroundUrl,
    string? LogoUrl,
    string? BannerUrl,
    string? ThumbUrl
);
