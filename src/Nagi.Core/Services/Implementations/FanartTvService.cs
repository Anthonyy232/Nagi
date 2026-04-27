using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Service for fetching high-quality artist imagery from Fanart.tv.
/// </summary>
public class FanartTvService : IFanartTvService
{
    private const string BaseUrl = "https://webservice.fanart.tv/v3/music";
    private const string ApiKeyName = ServiceProviderIds.FanartTv;

    private readonly HttpClient _httpClient;
    private readonly IProviderPipelineProvider _pipelines;
    private readonly IApiKeyService _apiKeyService;
    private readonly ILogger<FanartTvService> _logger;

    public FanartTvService(
        IHttpClientFactory httpClientFactory,
        IProviderPipelineProvider pipelines,
        IApiKeyService apiKeyService,
        ILogger<FanartTvService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _pipelines = pipelines;
        _apiKeyService = apiKeyService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ServiceResult<FanartTvArtistImages>> GetArtistImagesAsync(
        string musicBrainzId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(musicBrainzId))
            return ServiceResult<FanartTvArtistImages>.FromSuccessNotFound();

        if (_pipelines.IsCircuitOpen(ServiceProviderIds.FanartTv))
            return ServiceResult<FanartTvArtistImages>.FromTemporaryError("Provider is temporarily unavailable.");

        var apiKey = await _apiKeyService.GetApiKeyAsync(ApiKeyName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("Fanart.tv API key not available.");
            return ServiceResult<FanartTvArtistImages>.FromTemporaryError("API key not available.");
        }

        var url = $"{BaseUrl}/{musicBrainzId}?api_key={apiKey}";

        return await _pipelines.ExecuteWithFallbackAsync(
            ServiceProviderIds.FanartTv,
            async ct =>
            {
                _logger.LogDebug("Fetching Fanart.tv images for MBID: {MBID}", musicBrainzId);
                return await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogDebug("No Fanart.tv images found for MBID: {MBID}", musicBrainzId);
                    return ServiceResult<FanartTvArtistImages>.FromSuccessNotFound();
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Fanart.tv request failed with status {StatusCode} for MBID: {MBID}",
                        response.StatusCode, musicBrainzId);
                    return ServiceResult<FanartTvArtistImages>.FromTemporaryError($"HTTP {response.StatusCode}");
                }

                var apiResult = await response.Content.ReadFromJsonAsync<FanartTvResponse>(cancellationToken: ct)
                    .ConfigureAwait(false);

                if (apiResult is null)
                    return ServiceResult<FanartTvArtistImages>.FromSuccessNotFound();

                var images = new FanartTvArtistImages(
                    BackgroundUrl: apiResult.ArtistBackgrounds?.FirstOrDefault()?.Url,
                    LogoUrl: apiResult.HdMusicLogos?.FirstOrDefault()?.Url ?? apiResult.MusicLogos?.FirstOrDefault()?.Url,
                    BannerUrl: apiResult.MusicBanners?.FirstOrDefault()?.Url,
                    ThumbUrl: apiResult.ArtistThumbs?.FirstOrDefault()?.Url
                );

                if (images.BackgroundUrl is null && images.LogoUrl is null &&
                    images.BannerUrl is null && images.ThumbUrl is null)
                {
                    _logger.LogDebug("Fanart.tv returned empty images for MBID: {MBID}", musicBrainzId);
                    return ServiceResult<FanartTvArtistImages>.FromSuccessNotFound();
                }

                _logger.LogInformation("Found Fanart.tv images for MBID: {MBID}", musicBrainzId);
                return ServiceResult<FanartTvArtistImages>.FromSuccess(images);
            },
            fallback: ServiceResult<FanartTvArtistImages>.FromTemporaryError("Provider is temporarily unavailable."),
            _logger,
            $"fetch images for MBID {musicBrainzId}",
            cancellationToken).ConfigureAwait(false);
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
