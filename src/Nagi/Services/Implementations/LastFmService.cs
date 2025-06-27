using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nagi.Services.Abstractions;
using Nagi.Services.Data;
using Nagi.Services.Data.LastFm;

namespace Nagi.Services {
    public class LastFmService : ILastFmService {
        private const string LastFmApiBaseUrl = "https://ws.audioscrobbler.com/2.0/";
        private const string GetInfoMethod = "artist.getinfo";
        private const string ImageSizeExtraLarge = "extralarge";
        private const string ImageSizeLarge = "large";
        private const int MaxRetries = 1; // Try once, then retry once more. Total 2 attempts.
        private const int InvalidApiKeyErrorCode = 10;

        private readonly HttpClient _httpClient;
        private readonly IApiKeyService _apiKeyService;

        // Use modern options for better performance and adherence to web standards.
        private static readonly JsonSerializerOptions _jsonOptions = new() {
            PropertyNameCaseInsensitive = true
        };

        public LastFmService(IHttpClientFactory httpClientFactory, IApiKeyService apiKeyService) {
            _httpClient = httpClientFactory.CreateClient();
            _apiKeyService = apiKeyService;
        }

        public async Task<ArtistInfo?> GetArtistInfoAsync(string artistName, CancellationToken cancellationToken = default) {
            string? apiKey = await _apiKeyService.GetLastFmApiKeyAsync(cancellationToken);
            if (string.IsNullOrEmpty(apiKey)) {
                Debug.WriteLine("[LastFmService] Cannot get artist info, Last.fm API key is unavailable.");
                return null;
            }

            for (int i = 0; i <= MaxRetries; i++) {
                cancellationToken.ThrowIfCancellationRequested();

                try {
                    var requestUrl = $"{LastFmApiBaseUrl}?method={GetInfoMethod}&artist={Uri.EscapeDataString(artistName)}&api_key={apiKey}&format=json";
                    using var response = await _httpClient.GetAsync(requestUrl, cancellationToken);

                    // Use a stream for more efficient deserialization
                    await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);

                    if (response.IsSuccessStatusCode) {
                        var lastFmResponse = await JsonSerializer.DeserializeAsync<LastFmArtistResponse>(contentStream, _jsonOptions, cancellationToken);
                        if (lastFmResponse?.Artist == null) {
                            Debug.WriteLine($"[LastFmService] Artist '{artistName}' not found on Last.fm or invalid response.");
                            return null;
                        }
                        return ProcessApiResponse(lastFmResponse.Artist);
                    }

                    // If not successful, try to parse a structured error from Last.fm
                    var errorResponse = await JsonSerializer.DeserializeAsync<LastFmErrorResponse>(contentStream, _jsonOptions, cancellationToken);

                    // Check if the error is an invalid key and if we have retries left
                    if (errorResponse?.ErrorCode == InvalidApiKeyErrorCode && i < MaxRetries) {
                        Debug.WriteLine("[LastFmService] Invalid API key detected. Refreshing key and retrying...");
                        apiKey = await _apiKeyService.RefreshApiKeyAsync(cancellationToken);
                        if (string.IsNullOrEmpty(apiKey)) {
                            Debug.WriteLine("[LastFmService] Failed to refresh API key. Aborting.");
                            return null; // Abort if refresh fails
                        }
                        continue; // Go to the next loop iteration to retry
                    }

                    // For any other error, or if we've run out of retries, log and exit.
                    Debug.WriteLine($"[LastFmService] API call failed for '{artistName}'. Status: {response.StatusCode}. Error: {errorResponse?.Message ?? "Unknown"}");
                    return null;
                }
                catch (JsonException ex) {
                    Debug.WriteLine($"[LastFmService] Failed to deserialize Last.fm response for '{artistName}': {ex.Message}");
                    return null;
                }
                catch (OperationCanceledException) {
                    // No need to log here, as this is an expected cancellation.
                    // The calling code should handle this if necessary.
                    throw; // Re-throw so the cancellation propagates.
                }
            }

            // This point is reached only if all retries fail.
            return null;
        }

        /// <summary>
        /// Helper method to map the raw API model to our clean DTO.
        /// </summary>
        private ArtistInfo ProcessApiResponse(LastFmArtist artist) {
            // Clean up the biography text which often includes a link.
            var bio = artist.Bio?.Summary ?? string.Empty;
            var linkIndex = bio.IndexOf("<a href=\"https://www.last.fm", StringComparison.Ordinal);
            if (linkIndex > 0) {
                bio = bio.Substring(0, linkIndex).Trim();
            }

            if (string.IsNullOrWhiteSpace(bio)) {
                bio = "No biography available for this artist.";
            }

            // Find the best available image URL, preferring 'extralarge'.
            var imageUrl = artist.Image?.FirstOrDefault(i => i.Size == ImageSizeExtraLarge && !string.IsNullOrEmpty(i.Url))?.Url
                        ?? artist.Image?.FirstOrDefault(i => i.Size == ImageSizeLarge && !string.IsNullOrEmpty(i.Url))?.Url
                        ?? artist.Image?.LastOrDefault(i => !string.IsNullOrEmpty(i.Url))?.Url;

            return new ArtistInfo {
                Biography = bio,
                ImageUrl = imageUrl
            };
        }
    }
}