using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <param name="ReleaseId">MusicBrainz release ID from the album's tags, if any.</param>
/// <param name="ReleaseGroupId">MusicBrainz release group ID from the album's tags, if any.</param>
public record AlbumCoverQuery(string Title, string ArtistName, string? ReleaseId = null, string? ReleaseGroupId = null);

public interface IAlbumCoverService
{
    /// <summary>
    ///     Asks the enabled album cover sources one at a time, in priority order, and returns the first cover found.
    /// </summary>
    /// <returns>
    ///     A cover URL; not found once every source has answered; or a retryable failure when a source failed
    ///     before any cover was found.
    /// </returns>
    Task<ServiceResult<string>> FindCoverUrlAsync(AlbumCoverQuery album,
        IReadOnlyList<ServiceProviderSetting> enabledProviders, CancellationToken cancellationToken = default);
}
