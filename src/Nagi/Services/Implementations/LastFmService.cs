using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nagi.Services.Abstractions;
using Nagi.Services.Data;

namespace Nagi.Services;

/// <summary>
/// Service to fetch artist information from the Last.fm API.
/// Includes logic to handle API key errors and retries.
/// </summary>
public class LastFmService : ILastFmService {
    private const string LastFmApiBaseUrl = "https://ws.audioscrobbler.com/2.0/";
    private const string GetInfoMethod = "artist.getinfo";
    private const string ImageSizeExtraLarge = "extralarge";
    private const string ImageSizeLarge = "large";
    private const int MaxRetries = 1;
    private const int InvalidApiKeyErrorCode = 10;

    private readonly HttpClient _httpClient;
    private readonly IApiKeyService _apiKeyService;
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public LastFmService(IHttpClientFactory httpClientFactory, IApiKeyService apiKeyService) {
        _httpClient = httpClientFactory.CreateClient();
        _apiKeyService = apiKeyService;
    }

    public async Task<ArtistInfo?> GetArtistInfoAsync(string artistName, CancellationToken cancellationToken = default) {
        string? apiKey = await _apiKeyService.GetLastFmApiKeyAsync(cancellationToken);
        if (string.IsNullOrEmpty(apiKey)) {
            // This is a configuration issue, not a runtime error.
            // Logging it once is sufficient.
            Debug.WriteLine("Cannot get artist info; Last.fm API key is unavailable.");
            return null;
        }

        for (int attempt = 0; attempt <= MaxRetries; attempt++) {
            cancellationToken.ThrowIfCancellationRequested();

            try {
                var requestUrl = $"{LastFmApiBaseUrl}?method={GetInfoMethod}&artist={Uri.EscapeDataString(artistName)}&api_key={apiKey}&format=json";
                using var response = await _httpClient.GetAsync(requestUrl, cancellationToken);
                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

                if (response.IsSuccessStatusCode) {
                    var lastFmResponse = await JsonSerializer.DeserializeAsync<LastFmArtistResponse>(contentStream, _jsonOptions, cancellationToken);
                    if (lastFmResponse?.Artist == null) {
                        Debug.WriteLine($"Artist '{artistName}' not found on Last.fm or response was invalid.");
                        return null;
                    }
                    return ProcessApiResponse(lastFmResponse.Artist);
                }

                // If the call failed, attempt to parse a structured error.
                var errorResponse = await JsonSerializer.DeserializeAsync<LastFmErrorResponse>(contentStream, _jsonOptions, cancellationToken);

                // If the API key is invalid and we have retries left, refresh the key and try again.
                if (errorResponse?.ErrorCode == InvalidApiKeyErrorCode && attempt < MaxRetries) {
                    Debug.WriteLine("Invalid Last.fm API key detected. Refreshing key and retrying.");
                    apiKey = await _apiKeyService.RefreshApiKeyAsync(cancellationToken);
                    if (string.IsNullOrEmpty(apiKey)) {
                        Debug.WriteLine("Failed to refresh Last.fm API key. Aborting.");
                        return null;
                    }
                    continue;
                }

                Debug.WriteLine($"API call for '{artistName}' failed. Status: {response.StatusCode}, Error: {errorResponse?.Message ?? "Unknown"}");
                return null;
            }
            catch (JsonException ex) {
                Debug.WriteLine($"Failed to deserialize Last.fm response for '{artistName}': {ex.Message}");
                return null;
            }
            catch (OperationCanceledException) {
                // Propagate the cancellation so the calling operation is cancelled.
                throw;
            }
        }

        // This point is only reached if all retry attempts fail.
        return null;
    }

    /// <summary>
    /// Maps the raw Last.fm API response to the application's ArtistInfo DTO.
    /// </summary>
    private ArtistInfo ProcessApiResponse(LastFmArtist artist) {
        string bio = artist.Bio?.Summary ?? string.Empty;

        // The Last.fm API often includes a "Read more on Last.fm" link, which should be removed.
        var linkIndex = bio.IndexOf("<a href=\"https://www.last.fm", StringComparison.Ordinal);
        if (linkIndex > 0) {
            bio = bio.Substring(0, linkIndex).Trim();
        }

        if (string.IsNullOrWhiteSpace(bio)) {
            bio = "No biography available for this artist.";
        }

        // Find the best available image URL, preferring larger sizes.
        var imageUrl = artist.Image?.FirstOrDefault(i => i.Size == ImageSizeExtraLarge && !string.IsNullOrEmpty(i.Url))?.Url
                    ?? artist.Image?.FirstOrDefault(i => i.Size == ImageSizeLarge && !string.IsNullOrEmpty(i.Url))?.Url
                    ?? artist.Image?.LastOrDefault(i => !string.IsNullOrEmpty(i.Url))?.Url;

        return new ArtistInfo {
            Biography = bio,
            ImageUrl = imageUrl
        };
    }
}