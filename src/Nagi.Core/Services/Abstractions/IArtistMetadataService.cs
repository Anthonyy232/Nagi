using Nagi.Core.Models;

namespace Nagi.Core.Services.Abstractions;

/// <param name="MusicBrainzId">The ID MusicBrainz found for an artist that had none, otherwise null.</param>
/// <param name="IsConclusive">
///     At least one source answered and none failed, so missing data is final rather than worth retrying.
/// </param>
public record ArtistMetadataResult(string? MusicBrainzId, string? ImageUrl, string? Biography, bool IsConclusive);

public interface IArtistMetadataService
{
    /// <summary>
    ///     Asks the enabled artist sources and keeps the first image and the first biography in priority order.
    /// </summary>
    Task<ArtistMetadataResult> FindAsync(string artistName, string? musicBrainzId,
        IReadOnlyList<ServiceProviderSetting> enabledProviders, CancellationToken cancellationToken = default);
}
