using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Nagi.Core.OnlineProviders;
using Nagi.Core.OnlineProviders.Pipelines;

namespace Nagi.Core.Scrobbling;

/// <param name="ApiKey">The API key the call was signed with.</param>
internal readonly record struct LastFmReply(HttpResponseMessage Response, string Body, string ApiKey);

/// <summary>
///     Sends Last.fm's signed calls, the ones sign-in and scrobbling make, through the Last.fm pipeline. When Last.fm
///     rejects the API key or the signature, the key and secret are fetched again and the call is sent once more.
/// </summary>
internal sealed class LastFmSignedClient
{
    private readonly HttpClient _httpClient;
    private readonly ProviderPipelineProvider _pipelines;
    private readonly IApiKeyService _apiKeys;
    private readonly ILogger _logger;

    public LastFmSignedClient(HttpClient httpClient, ProviderPipelineProvider pipelines, IApiKeyService apiKeys,
        ILogger logger)
    {
        _httpClient = httpClient;
        _pipelines = pipelines;
        _apiKeys = apiKeys;
        _logger = logger;
    }

    /// <param name="parameters">
    ///     The call's parameters, without the API key and signature. A "format" parameter isn't signed, as Last.fm
    ///     requires.
    /// </param>
    /// <param name="fallback">The result when the keys can't be fetched or the call fails before a reply.</param>
    public async Task<T> SendAsync<T>(HttpMethod method, IReadOnlyDictionary<string, string> parameters,
        Func<LastFmReply, T> read, T fallback, CancellationToken cancellationToken = default)
    {
        var operation = $"call to '{parameters["method"]}'";
        var keys = await GetKeysAsync(refresh: false, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; keys is { } current; attempt++)
        {
            var signed = Sign(parameters, current.ApiKey, current.ApiSecret);
            var retry = attempt == 0;
            var (result, keyRejected) = await _pipelines.ExecuteWithFallbackAsync(
                ProviderCatalog.LastFm.Policy.ProviderId,
                async ct =>
                {
                    _logger.LogDebug("Calling Last.fm method '{Method}'.", parameters["method"]);
                    // A retry sends the request again, which needs a new message.
                    using var request = CreateRequest(method, signed);
                    return await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                },
                async (response, ct) =>
                {
                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    return retry && ErrorCode(body) is { } code && LastFmApiHelper.IsKeyError(code)
                        ? (fallback, true)
                        : (read(new LastFmReply(response, body, current.ApiKey)), false);
                },
                (fallback, false), _logger, operation, cancellationToken).ConfigureAwait(false);
            if (!keyRejected) return result;

            _logger.LogWarning("Last.fm rejected its API key or secret; fetching them again for {Operation}.", operation);
            keys = await GetKeysAsync(refresh: true, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogError("Cannot make the Last.fm {Operation}; the API key or secret is unavailable.", operation);
        return fallback;
    }

    /// <summary>
    ///     The MD5 signature of a signed call: the parameters sorted by name and concatenated, followed by the secret.
    /// </summary>
    private static string CreateSignature(IEnumerable<KeyValuePair<string, string>> parameters, string secret)
    {
        var text = string.Concat(parameters.Where(p => p.Key != "format").OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key + p.Value)) + secret;
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static Dictionary<string, string> Sign(IReadOnlyDictionary<string, string> parameters, string apiKey,
        string apiSecret)
    {
        var signed = new Dictionary<string, string>(parameters) { ["api_key"] = apiKey };
        signed["api_sig"] = CreateSignature(signed, apiSecret);
        return signed;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Dictionary<string, string> parameters)
    {
        if (method == HttpMethod.Post)
            return new HttpRequestMessage(method, LastFmApiHelper.BaseUrl) { Content = new FormUrlEncodedContent(parameters) };

        var query = string.Join('&', parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return new HttpRequestMessage(method, $"{LastFmApiHelper.BaseUrl}?{query}");
    }

    private async Task<(string ApiKey, string ApiSecret)?> GetKeysAsync(bool refresh, CancellationToken cancellationToken)
    {
        Task<string?> Get(string name) => refresh
            ? _apiKeys.RefreshApiKeyAsync(name, cancellationToken)
            : _apiKeys.GetApiKeyAsync(name, cancellationToken);

        var apiKeyTask = Get(ApiKeyNames.LastFm);
        var apiSecretTask = Get(ApiKeyNames.LastFmSecret);
        var apiKey = await apiKeyTask.ConfigureAwait(false);
        var apiSecret = await apiSecretTask.ConfigureAwait(false);
        return string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret) ? null : (apiKey, apiSecret);
    }

    // Last.fm answers in JSON or XML, as the call asked, and reports an error in the body whatever the status.
    private static int? ErrorCode(string body)
    {
        try
        {
            if (body.TrimStart().StartsWith('{'))
            {
                using var document = JsonDocument.Parse(body);
                return document.RootElement.TryGetProperty("error", out var error) && error.TryGetInt32(out var code)
                    ? code
                    : null;
            }

            return (int?)XElement.Parse(body).Element("error")?.Attribute("code");
        }
        catch (Exception ex) when (ex is JsonException or XmlException or FormatException)
        {
            return null;
        }
    }
}
