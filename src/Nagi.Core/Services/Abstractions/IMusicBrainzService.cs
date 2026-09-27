namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Service for resolving artist identities via the MusicBrainz database.
/// </summary>
public interface IMusicBrainzService
{
    /// <summary>
    ///     Searches MusicBrainz for an artist by name and returns the best matching MBID.
    /// </summary>
    /// <param name="artistName">The artist name to search for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching MBID, no match, or a retryable provider failure.</returns>
    Task<Data.ServiceResult<string>> SearchArtistAsync(string artistName, CancellationToken cancellationToken = default);
}
