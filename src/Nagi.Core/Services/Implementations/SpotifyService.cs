using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

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
    private const string ApiKeyName = "spotify";
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IApiKeyService _apiKeyService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<SpotifyService> _logger;

    private string? _accessToken;
    private DateTime _accessTokenExpiration;
    private bool _isApiPermanentlyDisabled;
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
        return await FetchAndCacheAccessTokenAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ServiceResult<SpotifyImageResult>> GetArtistImageUrlAsync(string artistName,
        CancellationToken cancellationToken = default)
    {
        if (_isApiPermanentlyDisabled)
            return ServiceResult<SpotifyImageResult>.FromPermanentError(
                "Spotify API is disabled for this session due to rate limiting.");
        if (string.IsNullOrWhiteSpace(artistName))
            return ServiceResult<SpotifyImageResult>.FromPermanentError("Artist name cannot be empty.");

        var token = await GetAccessTokenAsync(cancellationToken);
        if (string.IsNullOrEmpty(token))
            return ServiceResult<SpotifyImageResult>.FromTemporaryError("Could not retrieve Spotify access token.");

        var requestUrl = $"{SpotifyApiBaseUrl}search?q={Uri.EscapeDataString(artistName)}&type=artist&limit=1";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var response = await _httpClient.SendAsync(request, cancellationToken);

            // If rate limited, disable the service for the current session to avoid further errors.
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("Spotify API rate limit hit. Disabling service for the current session.");
                _isApiPermanentlyDisabled = true;
                return ServiceResult<SpotifyImageResult>.FromPermanentError("Spotify API rate limit exceeded.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Spotify artist search failed. Status: {StatusCode}, Content: {ErrorContent}",
                    response.StatusCode, errorContent);
                return ServiceResult<SpotifyImageResult>.FromTemporaryError(
                    $"Spotify artist search failed. Status: {response.StatusCode}");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            var searchResponse = JsonSerializer.Deserialize<SpotifySearchResponse>(jsonResponse, _jsonOptions);

            var artist = searchResponse?.Artists?.Items?.FirstOrDefault();
            if (artist?.Images is null || !artist.Images.Any())
                return ServiceResult<SpotifyImageResult>.FromSuccessNotFound();

            // Find the largest image available by area.
            var largestImage = artist.Images
                .Where(img => !string.IsNullOrEmpty(img.Url))
                .OrderByDescending(img => img.Height * img.Width)
                .FirstOrDefault();

            return largestImage != null
                ? ServiceResult<SpotifyImageResult>.FromSuccess(new SpotifyImageResult { ImageUrl = largestImage.Url! })
                : ServiceResult<SpotifyImageResult>.FromSuccessNotFound();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize Spotify response for artist '{ArtistName}'.", artistName);
            return ServiceResult<SpotifyImageResult>.FromPermanentError(
                $"Failed to deserialize Spotify response for '{artistName}'.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Exception while fetching Spotify artist image for '{ArtistName}'.", artistName);
            return ServiceResult<SpotifyImageResult>.FromTemporaryError(
                $"Exception while fetching Spotify artist image for '{artistName}'.");
        }
    }

    /// <summary>
    ///     Fetches a new client credentials access token from the Spotify API and caches it.
    /// </summary>
    private async Task<string?> FetchAndCacheAccessTokenAsync(CancellationToken cancellationToken)
    {
        var spotifyCredentials = await _apiKeyService.GetApiKeyAsync(ApiKeyName, cancellationToken);
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

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{SpotifyAccountsBaseUrl}api/token");
            var authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
            request.Content = new StringContent("grant_type=client_credentials", Encoding.UTF8,
                "application/x-www-form-urlencoded");

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError(
                    "Error fetching Spotify access token. Status: {StatusCode}, Content: {ErrorContent}",
                    response.StatusCode, errorContent);
                return null;
            }

            var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
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
                return _accessToken;
            }

            _logger.LogError("Spotify token response is missing 'access_token' or 'expires_in'.");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Exception while fetching Spotify access token.");
            return null;
        }
    }
}