using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines a contract for a service that fetches artist information from the Last.fm API.
/// </summary>
public interface ILastFmMetadataService
{
    /// <summary>
    ///     Asynchronously retrieves information about a specific artist from Last.fm.
    /// </summary>
    /// <param name="artistName">The name of the artist to look up.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    ///     A task that represents the asynchronous operation. The task result contains a
    ///     <see cref="ServiceResult{ArtistInfo}" /> object detailing the outcome of the API call.
    /// </returns>
    Task<ServiceResult<ArtistInfo>>
        GetArtistInfoAsync(string artistName, CancellationToken cancellationToken = default);
}