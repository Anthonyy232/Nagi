namespace Nagi.Core.Services.Abstractions;

/// <summary>The keys Nagi's API server hands out.</summary>
public static class ApiKeyNames
{
    public const string LastFm = "lastfm";

    public const string LastFmSecret = "lastfm-secret";

    public const string TheAudioDb = "theaudiodb";

    public const string FanartTv = "fanarttv";
}

public interface IApiKeyService
{
    /// <summary>Fetches the key on first use and caches it.</summary>
    /// <param name="keyName">One of <see cref="ApiKeyNames" />.</param>
    /// <returns>The key, or null if it couldn't be fetched.</returns>
    Task<string?> GetApiKeyAsync(string keyName, CancellationToken cancellationToken = default);

    /// <summary>Fetches the key again, for when a provider rejects the cached one.</summary>
    /// <returns>The key, or null if it couldn't be fetched.</returns>
    Task<string?> RefreshApiKeyAsync(string keyName, CancellationToken cancellationToken = default);
}
