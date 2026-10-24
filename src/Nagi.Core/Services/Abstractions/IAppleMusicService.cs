using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

public interface IAppleMusicService
{
    /// <summary>
    ///     Finds the cover of the Apple Music album whose title and artist match.
    /// </summary>
    Task<ServiceResult<string>> GetAlbumCoverUrlAsync(string albumTitle, string artistName,
        CancellationToken cancellationToken = default);
}
