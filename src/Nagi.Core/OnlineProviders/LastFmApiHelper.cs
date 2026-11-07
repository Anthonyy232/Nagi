using System.Security.Cryptography;
using System.Text;

namespace Nagi.Core.OnlineProviders;

internal static class LastFmApiHelper
{
    public const string BaseUrl = "https://ws.audioscrobbler.com/2.0/";

    /// <summary>
    ///     Creates the MD5 signature of an authenticated Last.fm call: the parameters sorted by name and
    ///     concatenated, followed by the secret.
    /// </summary>
    /// <returns>The signature as a lowercase hex string.</returns>
    public static string CreateSignature(IDictionary<string, string> parameters, string secret)
    {
        var sb = new StringBuilder();

        foreach (var kvp in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append(kvp.Key);
            sb.Append(kvp.Value);
        }

        sb.Append(secret);

        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Gets the API key and secret that signed calls need.</summary>
    /// <returns>Both, or null if either couldn't be fetched.</returns>
    public static async Task<(string ApiKey, string ApiSecret)?> GetKeyAndSecretAsync(IApiKeyService apiKeys,
        CancellationToken cancellationToken = default)
    {
        var apiKeyTask = apiKeys.GetApiKeyAsync(ApiKeyNames.LastFm, cancellationToken);
        var apiSecretTask = apiKeys.GetApiKeyAsync(ApiKeyNames.LastFmSecret, cancellationToken);
        await Task.WhenAll(apiKeyTask, apiSecretTask).ConfigureAwait(false);

        var apiKey = await apiKeyTask.ConfigureAwait(false);
        var apiSecret = await apiSecretTask.ConfigureAwait(false);
        return string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret) ? null : (apiKey, apiSecret);
    }
}
