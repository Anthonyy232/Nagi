using System.Threading;
using System.Threading.Tasks;

namespace Nagi.Services.Abstractions {
    /// <summary>
    /// Defines a contract for a service that securely retrieves and caches API keys.
    /// </summary>
    public interface IApiKeyService {
        /// <summary>
        /// Asynchronously gets the Last.fm API key.
        /// Implementations should cache the key after the first successful retrieval.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>The API key, or null if retrieval fails.</returns>
        Task<string?> GetLastFmApiKeyAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Asynchronously forces a refresh of the API key.
        /// Implementations should invalidate any cached key and fetch a new one.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>The newly fetched API key, or null if retrieval fails.</returns>
        Task<string?> RefreshApiKeyAsync(CancellationToken cancellationToken = default);
    }
}