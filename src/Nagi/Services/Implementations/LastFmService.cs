using Nagi.Services.Abstractions;
using Nagi.Services.Data;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nagi.Services {
    /// <summary>
    /// A service for fetching artist information from the Last.fm API.
    /// </summary>
    public class LastFmService : ILastFmService {
        private const string LastFmApiBaseUrl = "https://ws.audioscrobbler.com/2.0/";
        private const string GetInfoMethod = "artist.getinfo";
        private const string ImageSizeExtraLarge = "extralarge";
        private const string ImageSizeLarge = "large";
        private const int MaxRetries = 1;
        private const int InvalidApiKeyErrorCode = 10;
        private const string ApiKeyName = "lastfm";

        private readonly HttpClient _httpClient;
        private readonly IApiKeyService _apiKeyService;
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

        public LastFmService(IHttpClientFactory httpClientFactory, IApiKeyService apiKeyService) {
            _httpClient = httpClientFactory.CreateClient();
            _apiKeyService = apiKeyService;
        }

        public async Task<ArtistInfo?> GetArtistInfoAsync(string artistName, CancellationToken cancellationToken = default) {
            string? apiKey = await _apiKeyService.GetApiKeyAsync(ApiKeyName, cancellationToken);
            if (string.IsNullOrEmpty(apiKey)) {
                Debug.WriteLine($"Cannot get artist info; API key '{ApiKeyName}' is unavailable.");
                return null;
            }

            for (int attempt = 0; attempt <= MaxRetries; attempt++) {
                cancellationToken.ThrowIfCancellationRequested();

                try {
                    var requestUrl = $"{LastFmApiBaseUrl}?method={GetInfoMethod}&artist={Uri.EscapeDataString(artistName)}&api_key={apiKey}&format=json";
                    using var response = await _httpClient.GetAsync(requestUrl, cancellationToken);

                    await using var memoryStream = new MemoryStream();
                    await response.Content.CopyToAsync(memoryStream, cancellationToken);
                    memoryStream.Position = 0;

                    if (response.IsSuccessStatusCode) {
                        var lastFmResponse = await JsonSerializer.DeserializeAsync<LastFmArtistResponse>(memoryStream, _jsonOptions, cancellationToken);
                        if (lastFmResponse?.Artist == null) {
                            return null;
                        }
                        return ProcessApiResponse(lastFmResponse.Artist);
                    }

                    var errorResponse = await JsonSerializer.DeserializeAsync<LastFmErrorResponse>(memoryStream, _jsonOptions, cancellationToken);

                    if (errorResponse?.ErrorCode == InvalidApiKeyErrorCode && attempt < MaxRetries) {
                        Debug.WriteLine($"Invalid Last.fm API key detected. Refreshing key '{ApiKeyName}' and retrying.");
                        apiKey = await _apiKeyService.RefreshApiKeyAsync(ApiKeyName, cancellationToken);
                        if (string.IsNullOrEmpty(apiKey)) {
                            Debug.WriteLine($"Failed to refresh API key '{ApiKeyName}'. Aborting.");
                            return null;
                        }
                        continue;
                    }

                    Debug.WriteLine($"Last.fm API call for '{artistName}' failed. Status: {response.StatusCode}, Error: {errorResponse?.Message ?? "Unknown"}");
                    return null;
                }
                catch (JsonException ex) {
                    Debug.WriteLine($"Failed to deserialize Last.fm response for '{artistName}': {ex.Message}");
                    return null;
                }
                catch (OperationCanceledException) {
                    throw;
                }
            }

            return null;
        }

        private ArtistInfo ProcessApiResponse(LastFmArtist artist) {
            string bio = artist.Bio?.Summary ?? string.Empty;
            var linkIndex = bio.IndexOf("<a href=\"https://www.last.fm", StringComparison.Ordinal);
            if (linkIndex > 0) {
                bio = bio.Substring(0, linkIndex).Trim();
            }
            if (string.IsNullOrWhiteSpace(bio)) {
                bio = "No biography available for this artist.";
            }

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