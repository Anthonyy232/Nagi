namespace Nagi.Core.Artwork;

/// <param name="MusicBrainzId">The ID MusicBrainz found for an artist that had none, otherwise null.</param>
/// <param name="IsConclusive">
///     No source failed, so missing data is final rather than worth retrying. True when no enabled source
///     can look the artist up.
/// </param>
public record ArtistMetadataResult(string? MusicBrainzId, string? ImageUrl, string? Biography, bool IsConclusive);

public interface IArtistMetadataService
{
    /// <summary>
    ///     Asks the enabled artist sources and keeps the first image and the first biography in priority order.
    /// </summary>
    /// <returns>
    ///     Null when online metadata is turned off, before or during the lookup, or no metadata source is enabled.
    /// </returns>
    Task<ArtistMetadataResult?> FindAsync(string artistName, string? musicBrainzId,
        CancellationToken cancellationToken = default);
}
