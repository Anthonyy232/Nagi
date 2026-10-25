using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

public interface ICoverArtArchiveService
{
    /// <summary>Gets the front cover of a MusicBrainz release.</summary>
    Task<ServiceResult<string>> GetReleaseCoverUrlAsync(string releaseId, CancellationToken cancellationToken = default);

    /// <summary>Gets the front cover MusicBrainz picked to represent a release group.</summary>
    Task<ServiceResult<string>> GetReleaseGroupCoverUrlAsync(string releaseGroupId, CancellationToken cancellationToken = default);
}
