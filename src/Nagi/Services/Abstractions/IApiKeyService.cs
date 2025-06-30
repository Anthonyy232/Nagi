using System.Threading;
using System.Threading.Tasks;

namespace Nagi.Services.Abstractions;

/// <summary>
///     Defines a contract for a service that retrieves and caches API keys from a secure source.
/// </summary>
public interface IApiKeyService
{
    /// <summary>
    ///     Asynchronously retrieves the specified API key.
    ///     The key is fetched from the source on the first request and cached for subsequent calls.
    /// </summary>
    /// <param name="keyName">The unique name of the key to retrieve (e.g., "lastfm", "spotify").</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     A task that represents the asynchronous operation. The task result contains the API key string,
    ///     or null if it could not be retrieved.
    /// </returns>
    Task<string?> GetApiKeyAsync(string keyName, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Asynchronously forces a refresh of a specific cached API key by re-fetching it from the source.
    /// </summary>
    /// <param name="keyName">The unique name of the key to refresh.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     A task that represents the asynchronous operation. The task result contains the newly fetched API key string,
    ///     or null if it could not be retrieved.
    /// </returns>
    Task<string?> RefreshApiKeyAsync(string keyName, CancellationToken cancellationToken = default);
}