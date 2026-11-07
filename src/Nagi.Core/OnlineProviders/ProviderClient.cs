using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Nagi.Core.OnlineProviders.Pipelines;

namespace Nagi.Core.OnlineProviders;

/// <summary>What a provider reports in a response body, for providers that report failures there.</summary>
internal enum BodyStatus
{
    Ok,
    NotFound,
    Failed,
    KeyRejected
}

/// <summary>A read response, or a rejection of the API key the request was sent with.</summary>
internal readonly record struct ProviderReply<T>(ServiceResult<T> Result, bool KeyRejected = false) where T : class
{
    public static implicit operator ProviderReply<T>(ServiceResult<T> result) => new(result);
}

/// <summary>
///     Sends the requests of one provider through its pipeline, with the provider's API key if it has one, and turns
///     every outcome into a <see cref="ServiceResult{T}" />. A 404 means not found; any other failure status, an open
///     circuit, a transport error or a timeout is a retryable error. A rejected key is fetched again and the request
///     retried once.
/// </summary>
internal sealed class ProviderClient
{
    private readonly ProviderPolicy _policy;
    private readonly HttpClient _httpClient;
    private readonly ProviderPipelineProvider _pipelines;
    private readonly ILogger _logger;
    private readonly IApiKeyService? _apiKeys;
    private readonly string? _apiKeyName;

    public ProviderClient(ProviderPolicy policy, HttpClient httpClient, ProviderPipelineProvider pipelines, ILogger logger,
        IApiKeyService? apiKeys = null, string? apiKeyName = null)
    {
        _policy = policy;
        _httpClient = httpClient;
        _pipelines = pipelines;
        _logger = logger;
        _apiKeys = apiKeys;
        _apiKeyName = apiKeyName;
    }

    private string ProviderId => _policy.ProviderId;

    public HttpRequestHeaders DefaultRequestHeaders => _httpClient.DefaultRequestHeaders;

    public Task<ServiceResult<T>> GetJsonAsync<TBody, T>(string url, Func<TBody, ServiceResult<T>> map, string operation,
        CancellationToken cancellationToken, Func<TBody, BodyStatus>? checkBody = null) where T : class =>
        SendJsonAsync(_ => new HttpRequestMessage(HttpMethod.Get, url), map, operation, cancellationToken, checkBody);

    /// <param name="url">Builds the URL around the provider's API key.</param>
    public Task<ServiceResult<T>> GetJsonAsync<TBody, T>(Func<string, string> url, Func<TBody, ServiceResult<T>> map,
        string operation, CancellationToken cancellationToken, Func<TBody, BodyStatus>? checkBody = null) where T : class =>
        SendJsonAsync(apiKey => new HttpRequestMessage(HttpMethod.Get, url(apiKey!)), map, operation, cancellationToken, checkBody);

    /// <param name="checkBody">
    ///     For a provider that reports failures in the body: the body is then read whatever the status, and this
    ///     decides what it means.
    /// </param>
    public Task<ServiceResult<T>> SendJsonAsync<TBody, T>(Func<string?, HttpRequestMessage> request,
        Func<TBody, ServiceResult<T>> map, string operation, CancellationToken cancellationToken,
        Func<TBody, BodyStatus>? checkBody = null) where T : class =>
        SendAsync(request, async (response, ct) =>
        {
            if (checkBody is null)
            {
                if (response.StatusCode == HttpStatusCode.NotFound) return ServiceResult<T>.FromSuccessNotFound();
                if (_apiKeyName is not null && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return new ProviderReply<T>(ServiceResult<T>.FromTemporaryError(), KeyRejected: true);
                if (!response.IsSuccessStatusCode) return Failed<T>(response.StatusCode, operation);
            }

            var body = await response.Content.ReadFromJsonAsync<TBody>(ct).ConfigureAwait(false);
            if (body is null)
                return response.IsSuccessStatusCode
                    ? ServiceResult<T>.FromSuccessNotFound()
                    : Failed<T>(response.StatusCode, operation);

            switch (checkBody?.Invoke(body) ?? BodyStatus.Ok)
            {
                case BodyStatus.NotFound:
                    return ServiceResult<T>.FromSuccessNotFound();
                case BodyStatus.KeyRejected:
                    return new ProviderReply<T>(ServiceResult<T>.FromTemporaryError(), KeyRejected: true);
                case BodyStatus.Failed:
                    _logger.LogWarning("{ProviderId} reported a failure ({StatusCode}) for {Operation}",
                        ProviderId, response.StatusCode, operation);
                    return ServiceResult<T>.FromTemporaryError();
            }

            return response.IsSuccessStatusCode ? map(body) : Failed<T>(response.StatusCode, operation);
        }, operation, cancellationToken);

    public Task<ServiceResult<byte[]>> GetBytesAsync(Uri url, string operation, CancellationToken cancellationToken) =>
        SendAsync(_ => new HttpRequestMessage(HttpMethod.Get, url), async (response, ct) =>
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return ServiceResult<byte[]>.FromSuccessNotFound();
            if (!response.IsSuccessStatusCode) return Failed<byte[]>(response.StatusCode, operation);
            return ServiceResult<byte[]>.FromSuccess(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
        }, operation, cancellationToken);

    /// <param name="request">Builds the request around the API key, which is null for a provider without one.</param>
    public async Task<ServiceResult<T>> SendAsync<T>(Func<string?, HttpRequestMessage> request,
        Func<HttpResponseMessage, CancellationToken, Task<ProviderReply<T>>> read, string operation,
        CancellationToken cancellationToken) where T : class
    {
        // Checked before the key too, so a failing provider doesn't cost a key request.
        if (_pipelines.IsCircuitOpen(ProviderId)) return ServiceResult<T>.FromTemporaryError();

        string? apiKey = null;
        if (_apiKeyName is not null)
        {
            apiKey = await _apiKeys!.GetApiKeyAsync(_apiKeyName, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(apiKey))
            {
                _logger.LogWarning("{ProviderId} API key is not available; skipping {Operation}.", ProviderId, operation);
                return ServiceResult<T>.FromTemporaryError();
            }
        }

        var reply = await SendOnceAsync(request, apiKey, read, operation, cancellationToken).ConfigureAwait(false);
        if (!reply.KeyRejected) return reply.Result;

        _logger.LogWarning("{ProviderId} rejected its API key; fetching it again for {Operation}.", ProviderId, operation);
        apiKey = await _apiKeys!.RefreshApiKeyAsync(_apiKeyName!, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(apiKey)) return ServiceResult<T>.FromTemporaryError();

        reply = await SendOnceAsync(request, apiKey, read, operation, cancellationToken).ConfigureAwait(false);
        return reply.Result;
    }

    private async Task<ProviderReply<T>> SendOnceAsync<T>(Func<string?, HttpRequestMessage> request, string? apiKey,
        Func<HttpResponseMessage, CancellationToken, Task<ProviderReply<T>>> read, string operation,
        CancellationToken cancellationToken) where T : class =>
        await _pipelines.ExecuteWithFallbackAsync<ProviderReply<T>>(ProviderId,
            async ct =>
            {
                // A retry sends the request again, which needs a new message.
                using var message = request(apiKey);
                _logger.LogDebug("Requesting {Operation} from {ProviderId}", operation, ProviderId);
                return await _httpClient.SendAsync(message, ct).ConfigureAwait(false);
            },
            (response, ct) => new ValueTask<ProviderReply<T>>(read(response, ct)),
            fallback: ServiceResult<T>.FromTemporaryError(),
            _logger, operation, cancellationToken).ConfigureAwait(false);

    private ProviderReply<T> Failed<T>(HttpStatusCode statusCode, string operation) where T : class
    {
        _logger.LogWarning("{ProviderId} returned {StatusCode} for {Operation}", ProviderId, statusCode, operation);
        return ServiceResult<T>.FromTemporaryError();
    }
}
