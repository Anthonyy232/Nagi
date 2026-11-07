namespace Nagi.Core.OnlineProviders;

public interface IFanartTvService
{
    Task<ServiceResult<FanartTvArtistImages>> GetArtistImagesAsync(string musicBrainzId, CancellationToken cancellationToken = default);
}

public record FanartTvArtistImages(
    string? BackgroundUrl,
    string? ThumbUrl
);
