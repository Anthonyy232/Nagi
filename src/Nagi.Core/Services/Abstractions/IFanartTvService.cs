using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

public interface IFanartTvService
{
    Task<ServiceResult<FanartTvArtistImages>> GetArtistImagesAsync(string musicBrainzId, CancellationToken cancellationToken = default);
}

public record FanartTvArtistImages(
    string? BackgroundUrl,
    string? ThumbUrl
);
