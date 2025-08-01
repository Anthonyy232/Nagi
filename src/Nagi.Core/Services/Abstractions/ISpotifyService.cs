﻿using System.Threading;
using System.Threading.Tasks;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
/// Defines a contract for a service that interacts with the Spotify Web API.
/// </summary>
public interface ISpotifyService {
    /// <summary>
    /// Asynchronously retrieves an access token for making authenticated requests to the Spotify API.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the Spotify access token,
    /// or null if it could not be obtained.
    /// </returns>
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously retrieves the URL for an artist's image from Spotify.
    /// </summary>
    /// <param name="artistName">The name of the artist to look up.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains a
    /// <see cref="ServiceResult{SpotifyImageResult}"/> object detailing the outcome of the API call.
    /// </returns>
    Task<ServiceResult<SpotifyImageResult>> GetArtistImageUrlAsync(string artistName, CancellationToken cancellationToken = default);
}