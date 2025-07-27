using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
/// A service for interacting with the Spotify Web API, including token management and data retrieval.
/// </summary>
public class SpotifyService : ISpotifyService, IDisposable {
    private const string SpotifyAccountsBaseUrl = "https://accounts.spotify.com/";
    private const string SpotifyApiBaseUrl = "https://api.spotify.com/v1/";
    private const string ApiKeyName = "spotify";
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IApiKeyService _apiKeyService;
    private readonly HttpClient _httpClient;

    private string? _accessToken;
    private DateTime _accessTokenExpiration;

    // A circuit breaker flag to permanently disable API calls for the session if a rate limit is hit.
    private bool _isApiPermanentlyDisabled;
    private bool _isDisposed;

    public SpotifyService(IHttpClientFactory httpClientFactory, IApiKeyService apiKeyService) {
        _httpClient = httpClientFactory.CreateClient();
        _apiKeyService = apiKeyService;
    }

    /// <summary>
    /// Gets a valid Spotify access token, refreshing it if it's expired or nearing expiration.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A valid access token, or null if one could not be obtained.</returns>
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) {
        if (!string.IsNullOrEmpty(_accessToken) && _accessTokenExpiration > DateTime.UtcNow.AddMinutes(5)) {
            return _accessToken;
        }

        return await FetchAndCacheAccessTokenAsync(cancellationToken);
    }

    /// <summary>
    /// Retrieves the URL for an artist's image from Spotify.
    /// </summary>
    /// <param name="artistName">The name of the artist to search for.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The URL of the artist's largest available image, or null if not found or an error occurs.</returns>
    public async Task<string?> GetArtistImageUrlAsync(string artistName, CancellationToken cancellationToken = default) {
        if (_isApiPermanentlyDisabled) {
            return null;
        }

        if (string.IsNullOrWhiteSpace(artistName)) return null;

        var token = await GetAccessTokenAsync(cancellationToken);
        if (string.IsNullOrEmpty(token)) {
            Debug.WriteLine("Cannot fetch artist image; Spotify access token is unavailable.");
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{SpotifyApiBaseUrl}search?q={Uri.EscapeDataString(artistName)}&type=artist&limit=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try {
            var response = await _httpClient.SendAsync(request, cancellationToken);

            // If the rate limit is exceeded, permanently disable the service for this session to prevent further issues.
            if (response.StatusCode == HttpStatusCode.TooManyRequests) {
                Debug.WriteLine("Spotify rate limit hit. Disabling Spotify API requests for this session.");
                _isApiPermanentlyDisabled = true;
                return null;
            }

            if (!response.IsSuccessStatusCode) {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                Debug.WriteLine(
                    $"Spotify artist search failed. Status: {response.StatusCode}, Content: {errorContent}");
                return null;
            }

            var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            var spotifySearchResponse = JsonSerializer.Deserialize<SpotifySearchResponse>(jsonResponse, _jsonOptions);

            var artist = spotifySearchResponse?.Artists?.Items?.FirstOrDefault();
            if (artist?.Images is null || !artist.Images.Any()) return null;

            // Select the image with the largest area.
            var largestImage = artist.Images
                .OrderByDescending(img => img.Height * img.Width)
                .FirstOrDefault(img => !string.IsNullOrEmpty(img.Url));

            return largestImage?.Url;
        }
        catch (OperationCanceledException) {
            // Re-throw if the operation was cancelled by the caller.
            throw;
        }
        catch (JsonException ex) {
            Debug.WriteLine($"Failed to deserialize Spotify artist search response for '{artistName}': {ex.Message}");
            return null;
        }
        catch (Exception ex) {
            Debug.WriteLine($"Exception while fetching Spotify artist image for '{artistName}': {ex.Message}");
            return null;
        }
    }

    // Fetches a new access token using client credentials and caches it.
    private async Task<string?> FetchAndCacheAccessTokenAsync(CancellationToken cancellationToken) {
        var spotifyCredentials = await _apiKeyService.GetApiKeyAsync(ApiKeyName, cancellationToken);
        if (string.IsNullOrEmpty(spotifyCredentials)) {
            Debug.WriteLine($"Cannot get Spotify access token; credentials for '{ApiKeyName}' are unavailable.");
            return null;
        }

        var parts = spotifyCredentials.Split(':');
        if (parts.Length != 2) {
            Debug.WriteLine(
                $"Error: Spotify credentials for '{ApiKeyName}' are not in the expected 'ClientId:ClientSecret' format.");
            return null;
        }

        var clientId = parts[0];
        var clientSecret = parts[1];

        try {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{SpotifyAccountsBaseUrl}api/token");
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"))
            );
            request.Content = new StringContent("grant_type=client_credentials", Encoding.UTF8,
                "application/x-www-form-urlencoded");

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode) {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                Debug.WriteLine(
                    $"Error fetching Spotify access token. Status: {response.StatusCode}, Content: {errorContent}");
                return null;
            }

            var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(jsonResponse);
            var root = doc.RootElement;

            if (root.TryGetProperty("access_token", out var accessTokenElement) &&
                root.TryGetProperty("expires_in", out var expiresInElement)) {
                _accessToken = accessTokenElement.GetString();
                var expiresInSeconds = expiresInElement.GetInt32();
                _accessTokenExpiration = DateTime.UtcNow.AddSeconds(expiresInSeconds);
                return _accessToken;
            }

            Debug.WriteLine("Spotify token response is missing 'access_token' or 'expires_in'.");
            return null;
        }
        catch (OperationCanceledException) {
            throw;
        }
        catch (Exception ex) {
            Debug.WriteLine($"Exception while fetching Spotify access token: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Releases the unmanaged resources used by the <see cref="SpotifyService"/> and optionally releases the managed resources.
    /// </summary>
    public void Dispose() {
        if (_isDisposed) return;

        _httpClient.Dispose();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}