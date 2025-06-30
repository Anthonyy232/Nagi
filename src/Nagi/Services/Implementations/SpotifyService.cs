using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nagi.Services.Abstractions;
using Nagi.Services.Data;

namespace Nagi.Services;

/// <summary>
///     A service for interacting with the Spotify Web API, including token management and data retrieval.
/// </summary>
public class SpotifyService : ISpotifyService
{
    private const string SpotifyAccountsBaseUrl = "https://accounts.spotify.com/";
    private const string SpotifyApiBaseUrl = "https://api.spotify.com/v1/";
    private const string ApiKeyName = "spotify";
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IApiKeyService _apiKeyService;

    private readonly HttpClient _httpClient;

    private string? _accessToken;
    private DateTime _accessTokenExpiration;

    public SpotifyService(IHttpClientFactory httpClientFactory, IApiKeyService apiKeyService)
    {
        _httpClient = httpClientFactory.CreateClient();
        _apiKeyService = apiKeyService;
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(_accessToken) && _accessTokenExpiration > DateTime.UtcNow.AddMinutes(5))
            return _accessToken;

        return await FetchAndCacheAccessTokenAsync(cancellationToken);
    }

    public async Task<string?> GetArtistImageUrlAsync(string artistName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artistName)) return null;

        var token = await GetAccessTokenAsync(cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            Debug.WriteLine("Cannot fetch artist image; Spotify access token is unavailable.");
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{SpotifyApiBaseUrl}search?q={Uri.EscapeDataString(artistName)}&type=artist&limit=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                Debug.WriteLine(
                    $"Spotify artist search failed. Status: {response.StatusCode}, Content: {errorContent}");
                return null;
            }

            var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            var spotifySearchResponse = JsonSerializer.Deserialize<SpotifySearchResponse>(jsonResponse, _jsonOptions);

            var artist = spotifySearchResponse?.Artists?.Items?.FirstOrDefault();
            if (artist?.Images == null || !artist.Images.Any()) return null;

            var largestImage = artist.Images
                .OrderByDescending(img => img.Height * img.Width)
                .FirstOrDefault(img => !string.IsNullOrEmpty(img.Url));

            return largestImage?.Url;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"Failed to deserialize Spotify artist search response for '{artistName}': {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Exception while fetching Spotify artist image for '{artistName}': {ex.Message}");
            return null;
        }
    }

    private async Task<string?> FetchAndCacheAccessTokenAsync(CancellationToken cancellationToken)
    {
        var spotifyCredentials = await _apiKeyService.GetApiKeyAsync(ApiKeyName, cancellationToken);
        if (string.IsNullOrEmpty(spotifyCredentials))
        {
            Debug.WriteLine($"Cannot get Spotify access token; credentials for '{ApiKeyName}' are unavailable.");
            return null;
        }

        var parts = spotifyCredentials.Split(':');
        if (parts.Length != 2)
        {
            Debug.WriteLine(
                $"Error: Spotify credentials for '{ApiKeyName}' are not in the expected 'ClientId:ClientSecret' format.");
            return null;
        }

        var clientId = parts[0];
        var clientSecret = parts[1];

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{SpotifyAccountsBaseUrl}api/token");
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"))
            );
            request.Content = new StringContent("grant_type=client_credentials", Encoding.UTF8,
                "application/x-www-form-urlencoded");

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                Debug.WriteLine(
                    $"Error fetching Spotify access token. Status: {response.StatusCode}, Content: {errorContent}");
                return null;
            }

            var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(jsonResponse);
            var root = doc.RootElement;

            if (root.TryGetProperty("access_token", out var accessTokenElement) &&
                root.TryGetProperty("expires_in", out var expiresInElement))
            {
                _accessToken = accessTokenElement.GetString();
                var expiresInSeconds = expiresInElement.GetInt32();
                _accessTokenExpiration = DateTime.UtcNow.AddSeconds(expiresInSeconds);
                return _accessToken;
            }

            Debug.WriteLine("Spotify token response is missing 'access_token' or 'expires_in'.");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Exception while fetching Spotify access token: {ex.Message}");
            return null;
        }
    }
}