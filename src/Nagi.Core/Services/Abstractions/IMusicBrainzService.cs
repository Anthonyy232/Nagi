namespace Nagi.Core.Services.Abstractions;

public interface IMusicBrainzService
{
    /// <summary>
    ///     Searches MusicBrainz for an artist by name and returns the best matching MBID.
    /// </summary>
    /// <returns>The matching MBID, no match, or a retryable provider failure.</returns>
    Task<Data.ServiceResult<string>> SearchArtistAsync(string artistName, CancellationToken cancellationToken = default);
}
