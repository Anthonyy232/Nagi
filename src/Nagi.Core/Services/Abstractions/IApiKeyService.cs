namespace Nagi.Core.Services.Abstractions;

public interface IApiKeyService
{
    /// <summary>
    ///     Retrieves the specified API key.
    ///     The key is fetched from the source on the first request and cached for subsequent calls.
    /// </summary>
    /// <param name="keyName">The unique name of the key to retrieve (e.g., "lastfm").</param>
    /// <returns>The API key, or null if it could not be retrieved.</returns>
    Task<string?> GetApiKeyAsync(string keyName, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Forces a refresh of a specific cached API key by re-fetching it from the source.
    /// </summary>
    /// <param name="keyName">The unique name of the key to refresh.</param>
    /// <returns>The API key, or null if it could not be retrieved.</returns>
    Task<string?> RefreshApiKeyAsync(string keyName, CancellationToken cancellationToken = default);
}
