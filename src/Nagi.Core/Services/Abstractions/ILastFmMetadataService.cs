using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

public interface ILastFmMetadataService
{
    Task<ServiceResult<ArtistInfo>>
        GetArtistInfoAsync(string artistName, string? languageCode = null, CancellationToken cancellationToken = default);

    /// <summary>Gets the cover of the album Last.fm knows by this title and artist.</summary>
    Task<ServiceResult<string>> GetAlbumCoverUrlAsync(string albumTitle, string artistName,
        CancellationToken cancellationToken = default);
}
