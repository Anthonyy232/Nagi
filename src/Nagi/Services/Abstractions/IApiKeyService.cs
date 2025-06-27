using System.Threading;
using System.Threading.Tasks;

namespace Nagi.Services.Abstractions {
    public interface IApiKeyService {
        /// <summary>
        /// Gets the Last.fm API key, fetching it from the server on the first call
        /// or returning a cached version on subsequent calls.
        /// </summary>
        Task<string?> GetLastFmApiKeyAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Invalidates the cached API key and requests a new one from the server.
        /// </summary>
        /// <returns>The newly fetched API key, or null if fetching fails.</returns>
        Task<string?> RefreshApiKeyAsync(CancellationToken cancellationToken = default);
    }
}