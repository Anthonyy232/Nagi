using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

public class FanartTvService : IFanartTvService
{
    private const string BaseUrl = "https://webservice.fanart.tv/v3/music";

    private readonly ProviderClient _client;

    public FanartTvService(
        IHttpClientFactory httpClientFactory,
        ProviderPipelineProvider pipelines,
        IApiKeyService apiKeyService,
        ILogger<FanartTvService> logger)
    {
        _client = new ProviderClient(ProviderCatalog.FanartTv.Policy, httpClientFactory.CreateClient(), pipelines, logger,
            apiKeyService, ApiKeyNames.FanartTv);
    }

    public Task<ServiceResult<FanartTvArtistImages>> GetArtistImagesAsync(
        string musicBrainzId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(musicBrainzId))
            return Task.FromResult(ServiceResult<FanartTvArtistImages>.FromSuccessNotFound());

        return _client.GetJsonAsync<FanartTvResponse, FanartTvArtistImages>(
            apiKey => $"{BaseUrl}/{musicBrainzId}?api_key={apiKey}", response =>
            {
                var images = new FanartTvArtistImages(
                    BackgroundUrl: response.ArtistBackgrounds?.FirstOrDefault()?.Url,
                    ThumbUrl: response.ArtistThumbs?.FirstOrDefault()?.Url);
                return images.BackgroundUrl is null && images.ThumbUrl is null
                    ? ServiceResult<FanartTvArtistImages>.FromSuccessNotFound()
                    : ServiceResult<FanartTvArtistImages>.FromSuccess(images);
            }, $"images for MBID {musicBrainzId}", cancellationToken);
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
