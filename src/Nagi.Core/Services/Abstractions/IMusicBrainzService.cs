namespace Nagi.Core.Services.Abstractions;

public interface IMusicBrainzService
{
    /// <summary>
    ///     Searches MusicBrainz for an artist by name and returns the best matching MBID.
    /// </summary>
    /// <returns>The matching MBID, no match, or a retryable provider failure.</returns>
    Task<Data.ServiceResult<string>> SearchArtistAsync(string artistName, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Searches MusicBrainz for an album by title and artist and returns the matching release group MBID.
    /// </summary>
    /// <returns>The matching MBID, no match, or a retryable provider failure.</returns>
    Task<Data.ServiceResult<string>> SearchReleaseGroupAsync(string albumTitle, string artistName,
        CancellationToken cancellationToken = default);
}
