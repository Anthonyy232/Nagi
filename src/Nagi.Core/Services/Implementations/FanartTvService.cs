using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Helpers;

namespace Nagi.Core.Services.Implementations;

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

    public async Task<ServiceResult<FanartTvArtistImages>> GetArtistImagesAsync(
        string musicBrainzId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(musicBrainzId))
            return ServiceResult<FanartTvArtistImages>.FromSuccessNotFound();

        // Checked before the key too, so a failing provider doesn't cost a key request.
        if (_pipelines.IsCircuitOpen(ServiceProviderIds.FanartTv))
            return ServiceResult<FanartTvArtistImages>.FromTemporaryError();

        var apiKey = await _apiKeyService.GetApiKeyAsync(ApiKeyName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("Fanart.tv API key not available.");
            return ServiceResult<FanartTvArtistImages>.FromTemporaryError();
        }

        return await _pipelines.GetJsonAsync<FanartTvResponse, FanartTvArtistImages>(_httpClient, ServiceProviderIds.FanartTv,
            $"{BaseUrl}/{musicBrainzId}?api_key={apiKey}", response =>
            {
                var images = new FanartTvArtistImages(
                    BackgroundUrl: response.ArtistBackgrounds?.FirstOrDefault()?.Url,
                    ThumbUrl: response.ArtistThumbs?.FirstOrDefault()?.Url);
                return images.BackgroundUrl is null && images.ThumbUrl is null
                    ? ServiceResult<FanartTvArtistImages>.FromSuccessNotFound()
                    : ServiceResult<FanartTvArtistImages>.FromSuccess(images);
            }, _logger, $"images for MBID {musicBrainzId}", cancellationToken).ConfigureAwait(false);
    }

    private sealed class FanartTvResponse
    {
        [JsonPropertyName("artistbackground")]
        public List<FanartImage>? ArtistBackgrounds { get; set; }

        [JsonPropertyName("artistthumb")]
        public List<FanartImage>? ArtistThumbs { get; set; }
    }

    private sealed class FanartImage
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

    }
}
