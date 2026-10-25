using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

public interface IDeezerService
{
    /// <summary>
    ///     Finds the picture of the Deezer artist whose name matches <paramref name="artistName" />.
    /// </summary>
    Task<ServiceResult<string>> GetArtistImageUrlAsync(string artistName, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Finds the cover of the Deezer album whose title and artist match.
    /// </summary>
    Task<ServiceResult<string>> GetAlbumCoverUrlAsync(string albumTitle, string artistName,
        CancellationToken cancellationToken = default);
}
