using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

public interface IDeezerService
{
    /// <summary>
    ///     Finds the picture of the Deezer artist whose name matches <paramref name="artistName" />.
    /// </summary>
    /// <returns>The image URL, no match, or a retryable provider failure.</returns>
    Task<ServiceResult<string>> GetArtistImageUrlAsync(string artistName, CancellationToken cancellationToken = default);
}
