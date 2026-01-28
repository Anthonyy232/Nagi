using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Helpers;

namespace Nagi.Core.Services.Implementations;

// File-local DTOs for deserializing the Spotify API search response.
// This encapsulates the data structures specific to this service.
file class SpotifySearchResponse
{
    public SpotifyArtistCollection? Artists { get; set; }
}

file class SpotifyArtistCollection
{
    public IEnumerable<SpotifyArtist>? Items { get; set; }
}

file class SpotifyArtist
{
    public IEnumerable<SpotifyImage>? Images { get; set; }
}

file class SpotifyImage
{
    public string? Url { get; set; }
    public int Height { get; set; }
    public int Width { get; set; }
}

/// <summary>
///     A service for interacting with the Spotify Web API, including token management and data retrieval.
/// </summary>
public class SpotifyService : ISpotifyService, IDisposable
{
    private const string SpotifyAccountsBaseUrl = "https://accounts.spotify.com/";
    private const string SpotifyApiBaseUrl = "https://api.spotify.com/v1/";
    private const string ApiKeyName = ServiceProviderIds.Spotify;
    private const int MaxRetries = 3;
    private const int RateLimitDelayMultiplier = 5;
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IApiKeyService _apiKeyService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<SpotifyService> _logger;

    private string? _accessToken;
    private DateTime _accessTokenExpiration;
    private volatile bool _isApiPermanentlyDisabled;
    private bool _isDisposed;

    public SpotifyService(IHttpClientFactory httpClientFactory, IApiKeyService apiKeyService,
        ILogger<SpotifyService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _apiKeyService = apiKeyService;
        _logger = logger;
    }

    /// <summary>
    ///     Releases the resources used by the <see cref="SpotifyService" />.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;
        _httpClient.Dispose();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        // Return the cached token if it's valid and not expiring soon.
        if (!string.IsNullOrEmpty(_accessToken) && _accessTokenExpiration > DateTime.UtcNow.AddMinutes(5))
            return _accessToken;
        return await FetchAndCacheAccessTokenAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ServiceResult<SpotifyImageResult>> GetArtistImageUrlAsync(string artistName,
        CancellationToken cancellationToken = default)
    {
        if (_isApiPermanentlyDisabled)
            return ServiceResult<SpotifyImageResult>.FromPermanentError(
                "API is disabled for this session.");
        if (string.IsNullOrWhiteSpace(artistName))
            return ServiceResult<SpotifyImageResult>.FromPermanentError("Artist name cannot be empty.");

        var operationName = $"Spotify artist search for {artistName}";

        try
        {
            var result = await HttpRetryHelper.ExecuteWithRetryAsync<ServiceResult<SpotifyImageResult>>(
                async attempt =>
                {
                    var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(token))
                        return RetryResult<ServiceResult<SpotifyImageResult>>.Success(
                            ServiceResult<SpotifyImageResult>.FromTemporaryError("Could not retrieve Spotify access token."));

                    var normalizedArtist = ArtistNameHelper.NormalizeStringCore(artistName) ?? artistName;
                    var requestUrl = $"{SpotifyApiBaseUrl}search?q={Uri.EscapeDataString(normalizedArtist)}&type=artist&limit=1";
                    using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                    _logger.LogDebug("Searching Spotify for artist: {ArtistName} (Attempt {Attempt}/{MaxRetries})", 
                        artistName, attempt, MaxRetries);

                    using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        _logger.LogWarning("Spotify API rate limit hit for '{ArtistName}'. Attempt {Attempt}/{MaxRetries}", 
                            artistName, attempt, MaxRetries);
                        
                        if (attempt >= MaxRetries)
                        {
                            _logger.LogError("Spotify rate limit reached repeatedly. Disabling for this session.");
                            _isApiPermanentlyDisabled = true;
                            return RetryResult<ServiceResult<SpotifyImageResult>>.Success(
                                ServiceResult<SpotifyImageResult>.FromPermanentError("Rate limited."));
                        }

                        return RetryResult<ServiceResult<SpotifyImageResult>>.RateLimitFailure(RateLimitDelayMultiplier);
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        _logger.LogWarning(
                            "Spotify artist search failed. Status: {StatusCode}, Content: {ErrorContent}. Attempt {Attempt}/{MaxRetries}",
                            response.StatusCode, errorContent, attempt, MaxRetries);
                        
                        if (HttpRetryHelper.IsRetryableStatusCode(response.StatusCode))
                            return RetryResult<ServiceResult<SpotifyImageResult>>.TransientFailure();

                        return RetryResult<ServiceResult<SpotifyImageResult>>.Success(
                            ServiceResult<SpotifyImageResult>.FromTemporaryError(
                                $"Spotify artist search failed. Status: {response.StatusCode}"));
                    }

                    var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    var searchResponse = JsonSerializer.Deserialize<SpotifySearchResponse>(jsonResponse, _jsonOptions);

                    var artist = searchResponse?.Artists?.Items?.FirstOrDefault();
                    if (artist?.Images is null || !artist.Images.Any())
                        return RetryResult<ServiceResult<SpotifyImageResult>>.Success(
                            ServiceResult<SpotifyImageResult>.FromSuccessNotFound());

                    // Find the largest image available by area.
                    var largestImage = artist.Images
                        .Where(img => !string.IsNullOrEmpty(img.Url))
                        .OrderByDescending(img => img.Height * img.Width)
                        .FirstOrDefault();

                    return largestImage != null
                        ? RetryResult<ServiceResult<SpotifyImageResult>>.Success(
                            ServiceResult<SpotifyImageResult>.FromSuccess(new SpotifyImageResult { ImageUrl = largestImage.Url! }))
                        : RetryResult<ServiceResult<SpotifyImageResult>>.Success(
                            ServiceResult<SpotifyImageResult>.FromSuccessNotFound());
                },
                _logger,
                operationName,
                cancellationToken,
                MaxRetries
            ).ConfigureAwait(false);

            return result ?? ServiceResult<SpotifyImageResult>.FromTemporaryError("Failed after exhausting retries.");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize Spotify response for artist '{ArtistName}'.", artistName);
            return ServiceResult<SpotifyImageResult>.FromPermanentError(
                "Failed to deserialize response.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Exception while fetching Spotify artist image for '{ArtistName}'.", artistName);
            return ServiceResult<SpotifyImageResult>.FromTemporaryError(
                $"Exception while fetching Spotify artist image for '{artistName}'.");
        }
    }

    /// <summary>
    ///     Fetches a new client credentials access token from the Spotify API and caches it.
    /// </summary>
    private async Task<string?> FetchAndCacheAccessTokenAsync(CancellationToken cancellationToken)
    {
        var spotifyCredentials = await _apiKeyService.GetApiKeyAsync(ApiKeyName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(spotifyCredentials))
        {
            _logger.LogWarning("Cannot get access token; credentials for API key '{ApiKeyName}' are unavailable.",
                ApiKeyName);
            return null;
        }

        var parts = spotifyCredentials.Split(':');
        if (parts.Length != 2)
        {
            _logger.LogError(
                "Spotify credentials for API key '{ApiKeyName}' are not in 'ClientId:ClientSecret' format.",
                ApiKeyName);
            return null;
        }

        var (clientId, clientSecret) = (parts[0], parts[1]);
        var operationName = "Spotify access token fetch";

        return await HttpRetryHelper.ExecuteWithRetryAsync<string>(
            async attempt =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{SpotifyAccountsBaseUrl}api/token");
                var authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
                request.Content = new StringContent("grant_type=client_credentials", Encoding.UTF8,
                    "application/x-www-form-urlencoded");

                _logger.LogDebug("Fetching Spotify access token (Attempt {Attempt}/{MaxRetries})", attempt, MaxRetries);

                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    _logger.LogError(
                        "Error fetching Spotify access token. Status: {StatusCode}, Content: {ErrorContent}. Attempt {Attempt}/{MaxRetries}",
                        response.StatusCode, errorContent, attempt, MaxRetries);
                    
                    return RetryResult<string>.FromHttpStatus(response.StatusCode);
                }

                var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(jsonResponse);
                var root = doc.RootElement;

                if (root.TryGetProperty("access_token", out var accessTokenElement) &&
                    root.TryGetProperty("expires_in", out var expiresInElement) &&
                    accessTokenElement.ValueKind == JsonValueKind.String)
                {
                    _accessToken = accessTokenElement.GetString();
                    var expiresInSeconds = expiresInElement.GetInt32();
                    _accessTokenExpiration = DateTime.UtcNow.AddSeconds(expiresInSeconds);
                    _logger.LogDebug(
                        "Successfully fetched and cached new Spotify access token, valid for {ExpiresInSeconds} seconds.",
                        expiresInSeconds);
                    return RetryResult<string>.Success(_accessToken!);
                }

                _logger.LogError("Spotify token response is missing 'access_token' or 'expires_in'.");
                return RetryResult<string>.PermanentFailure();
            },
            _logger,
            operationName,
            cancellationToken,
            MaxRetries
        ).ConfigureAwait(false);
    }
}