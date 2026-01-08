using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http;
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
    private const int MaxRetries = 3;
    private const int RateLimitDelayMultiplier = 5;

    private readonly HttpClient _httpClient;
    private readonly IApiKeyService _apiKeyService;
    private readonly ILogger<FanartTvService> _logger;

    private volatile bool _isApiDisabled;

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

        var operationName = $"Fanart.tv images for MBID {musicBrainzId}";

        try
        {
            var result = await HttpRetryHelper.ExecuteWithRetryAsync<ServiceResult<FanartTvArtistImages>>(
                async attempt =>
                {
                    var apiKey = await _apiKeyService.GetApiKeyAsync(ApiKeyName, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(apiKey))
                    {
                        _logger.LogWarning("Fanart.tv API key not available.");
                        return RetryResult<ServiceResult<FanartTvArtistImages>>.Success(
                            ServiceResult<FanartTvArtistImages>.FromTemporaryError("API key not available."));
                    }

                    var url = $"{BaseUrl}/{musicBrainzId}?api_key={apiKey}";
                    _logger.LogDebug("Fetching Fanart.tv images for MBID: {MBID} (Attempt {Attempt}/{MaxRetries})", 
                        musicBrainzId, attempt, MaxRetries);

                    using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        _logger.LogDebug("No Fanart.tv images found for MBID: {MBID}", musicBrainzId);
                        return RetryResult<ServiceResult<FanartTvArtistImages>>.Success(
                            ServiceResult<FanartTvArtistImages>.FromSuccessNotFound());
                    }

                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        _logger.LogWarning("Fanart.tv rate limit reached for MBID: {MBID}. Attempt {Attempt}/{MaxRetries}", 
                            musicBrainzId, attempt, MaxRetries);
                        
                        if (attempt >= MaxRetries)
                        {
                            _logger.LogError("Fanart.tv rate limit reached repeatedly. Disabling for this session.");
                            _isApiDisabled = true;
                            return RetryResult<ServiceResult<FanartTvArtistImages>>.Success(
                                ServiceResult<FanartTvArtistImages>.FromPermanentError("Rate limited."));
                        }

                        return RetryResult<ServiceResult<FanartTvArtistImages>>.RateLimitFailure(RateLimitDelayMultiplier);
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Fanart.tv request failed with status {StatusCode} for MBID: {MBID}. Attempt {Attempt}/{MaxRetries}", 
                            response.StatusCode, musicBrainzId, attempt, MaxRetries);
                        
                        if (HttpRetryHelper.IsRetryableStatusCode(response.StatusCode))
                            return RetryResult<ServiceResult<FanartTvArtistImages>>.TransientFailure();

                        return RetryResult<ServiceResult<FanartTvArtistImages>>.Success(
                            ServiceResult<FanartTvArtistImages>.FromTemporaryError($"HTTP {response.StatusCode}"));
                    }

                    var apiResult = await response.Content.ReadFromJsonAsync<FanartTvResponse>(
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (apiResult is null)
                        return RetryResult<ServiceResult<FanartTvArtistImages>>.Success(
                            ServiceResult<FanartTvArtistImages>.FromSuccessNotFound());

                    var images = new FanartTvArtistImages(
                        BackgroundUrl: apiResult.ArtistBackgrounds?.FirstOrDefault()?.Url,
                        LogoUrl: apiResult.HdMusicLogos?.FirstOrDefault()?.Url ?? apiResult.MusicLogos?.FirstOrDefault()?.Url,
                        BannerUrl: apiResult.MusicBanners?.FirstOrDefault()?.Url,
                        ThumbUrl: apiResult.ArtistThumbs?.FirstOrDefault()?.Url
                    );

                    // Check if we actually got any usable images
                    if (images.BackgroundUrl is null && images.LogoUrl is null && 
                        images.BannerUrl is null && images.ThumbUrl is null)
                    {
                        _logger.LogDebug("Fanart.tv returned empty images for MBID: {MBID}", musicBrainzId);
                        return RetryResult<ServiceResult<FanartTvArtistImages>>.Success(
                            ServiceResult<FanartTvArtistImages>.FromSuccessNotFound());
                    }

                    _logger.LogInformation("Found Fanart.tv images for MBID: {MBID}", musicBrainzId);
                    return RetryResult<ServiceResult<FanartTvArtistImages>>.Success(
                        ServiceResult<FanartTvArtistImages>.FromSuccess(images));
                },
                _logger,
                operationName,
                cancellationToken,
                MaxRetries
            ).ConfigureAwait(false);

            return result ?? ServiceResult<FanartTvArtistImages>.FromTemporaryError("Failed after exhausting retries.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error fetching Fanart.tv images for MBID: {MBID} after exhausting retries.", musicBrainzId);
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
