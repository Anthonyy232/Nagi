using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Service for fetching high-quality artist imagery from Fanart.tv.
/// </summary>
public class FanartTvService : IFanartTvService
{
    private const string BaseUrl = "https://webservice.fanart.tv/v3/music";
    private const string ApiKeyName = "fanarttv";

    private readonly HttpClient _httpClient;
    private readonly IApiKeyService _apiKeyService;
    private readonly ILogger<FanartTvService> _logger;

    private bool _isApiDisabled;

    public FanartTvService(
        IHttpClientFactory httpClientFactory,
        IApiKeyService apiKeyService,
        ILogger<FanartTvService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _apiKeyService = apiKeyService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ServiceResult<FanartTvArtistImages>> GetArtistImagesAsync(
        string musicBrainzId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(musicBrainzId))
            return ServiceResult<FanartTvArtistImages>.FromSuccessNotFound();

        if (_isApiDisabled)
            return ServiceResult<FanartTvArtistImages>.FromPermanentError("Fanart.tv API is disabled for this session.");

        try
        {
            var apiKey = await _apiKeyService.GetApiKeyAsync(ApiKeyName, cancellationToken);
            if (string.IsNullOrEmpty(apiKey))
            {
                _logger.LogWarning("Fanart.tv API key not available.");
                return ServiceResult<FanartTvArtistImages>.FromTemporaryError("API key not available.");
            }

            var url = $"{BaseUrl}/{musicBrainzId}?api_key={apiKey}";
            _logger.LogDebug("Fetching Fanart.tv images for MBID: {MBID}", musicBrainzId);

            using var response = await _httpClient.GetAsync(url, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug("No Fanart.tv images found for MBID: {MBID}", musicBrainzId);
                return ServiceResult<FanartTvArtistImages>.FromSuccessNotFound();
            }

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("Fanart.tv rate limit reached. Disabling for this session.");
                _isApiDisabled = true;
                return ServiceResult<FanartTvArtistImages>.FromPermanentError("Rate limited.");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Fanart.tv request failed with status {StatusCode}", response.StatusCode);
                return ServiceResult<FanartTvArtistImages>.FromTemporaryError($"HTTP {response.StatusCode}");
            }

            var result = await response.Content.ReadFromJsonAsync<FanartTvResponse>(
                cancellationToken: cancellationToken);

            if (result is null)
                return ServiceResult<FanartTvArtistImages>.FromSuccessNotFound();

            var images = new FanartTvArtistImages(
                BackgroundUrl: result.ArtistBackgrounds?.FirstOrDefault()?.Url,
                LogoUrl: result.HdMusicLogos?.FirstOrDefault()?.Url ?? result.MusicLogos?.FirstOrDefault()?.Url,
                BannerUrl: result.MusicBanners?.FirstOrDefault()?.Url,
                ThumbUrl: result.ArtistThumbs?.FirstOrDefault()?.Url
            );

            // Check if we actually got any usable images
            if (images.BackgroundUrl is null && images.LogoUrl is null && 
                images.BannerUrl is null && images.ThumbUrl is null)
            {
                _logger.LogDebug("Fanart.tv returned empty images for MBID: {MBID}", musicBrainzId);
                return ServiceResult<FanartTvArtistImages>.FromSuccessNotFound();
            }

            _logger.LogInformation("Found Fanart.tv images for MBID: {MBID}", musicBrainzId);
            return ServiceResult<FanartTvArtistImages>.FromSuccess(images);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error fetching Fanart.tv images for MBID: {MBID}", musicBrainzId);
            return ServiceResult<FanartTvArtistImages>.FromTemporaryError(ex.Message);
        }
    }

    // DTOs for JSON deserialization
    private sealed class FanartTvResponse
    {
        [JsonPropertyName("artistbackground")]
        public List<FanartImage>? ArtistBackgrounds { get; set; }

        [JsonPropertyName("hdmusiclogo")]
        public List<FanartImage>? HdMusicLogos { get; set; }

        [JsonPropertyName("musiclogo")]
        public List<FanartImage>? MusicLogos { get; set; }

        [JsonPropertyName("musicbanner")]
        public List<FanartImage>? MusicBanners { get; set; }

        [JsonPropertyName("artistthumb")]
        public List<FanartImage>? ArtistThumbs { get; set; }
    }

    private sealed class FanartImage
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("likes")]
        public string? Likes { get; set; }
    }
}
