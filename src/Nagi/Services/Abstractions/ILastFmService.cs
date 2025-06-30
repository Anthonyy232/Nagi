using System.Threading;
using System.Threading.Tasks;
using Nagi.Services.Data;

namespace Nagi.Services.Abstractions;

/// <summary>
///     Defines a contract for a service that fetches artist information from the Last.fm API.
/// </summary>
public interface ILastFmService
{
    /// <summary>
    ///     Asynchronously retrieves information about a specific artist from Last.fm.
    /// </summary>
    /// <param name="artistName">The name of the artist to look up.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     A task that represents the asynchronous operation. The task result contains an
    ///     <see cref="ArtistInfo" /> object, or null if the artist could not be found or an error occurred.
    /// </returns>
    Task<ArtistInfo?> GetArtistInfoAsync(string artistName, CancellationToken cancellationToken = default);
}