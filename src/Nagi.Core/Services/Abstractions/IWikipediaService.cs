using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

public interface IWikipediaService
{
    /// <summary>
    ///     Gets the introduction of the Wikipedia article that Wikidata links to the MusicBrainz artist ID,
    ///     in <paramref name="languageCode" /> when that article exists and in English otherwise.
    /// </summary>
    /// <returns>Plain text ending with an attribution line, no article, or a retryable provider failure.</returns>
    Task<ServiceResult<string>> GetArtistBiographyAsync(
        string musicBrainzId, string? languageCode = null, CancellationToken cancellationToken = default);
}
