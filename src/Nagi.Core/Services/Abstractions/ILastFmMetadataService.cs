using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

public interface ILastFmMetadataService
{
    Task<ServiceResult<ArtistInfo>>
        GetArtistInfoAsync(string artistName, string? languageCode = null, CancellationToken cancellationToken = default);
}
