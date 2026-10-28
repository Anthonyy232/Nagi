using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Helpers;

namespace Nagi.Core.Services.Implementations;

file class TheAudioDbResponse
{
    [JsonPropertyName("artists")]
    public List<TheAudioDbArtist>? Artists { get; set; }
}

file class TheAudioDbArtist
{
    [JsonPropertyName("strBiographyEN")]
    public string? BiographyEN { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("strArtistThumb")]
    public string? ArtistThumb { get; set; }

    [JsonPropertyName("strArtistFanart")]
    public string? ArtistFanart { get; set; }

    [JsonPropertyName("strArtistFanart2")]
    public string? ArtistFanart2 { get; set; }

    [JsonPropertyName("strArtistFanart3")]
    public string? ArtistFanart3 { get; set; }
}

/// <summary>
///     Service for fetching artist metadata from TheAudioDB. Rate limiting (free tier
///     is 30 req/min), retry, and circuit breaker are handled by
///     <see cref="IProviderPipelineProvider"/>.
/// </summary>
public class TheAudioDbService : ITheAudioDbService, IDisposable
{
    private const string BaseUrl = "https://www.theaudiodb.com/api/v1/json";
    private const string ApiKeyName = ServiceProviderIds.TheAudioDb;

    private readonly HttpClient _httpClient;
    private readonly IProviderPipelineProvider _pipelines;
    private readonly IApiKeyService _apiKeyService;
    private readonly ILogger<TheAudioDbService> _logger;

    public TheAudioDbService(
        IHttpClientFactory httpClientFactory,
        IProviderPipelineProvider pipelines,
        IApiKeyService apiKeyService,
        ILogger<TheAudioDbService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _pipelines = pipelines;
        _apiKeyService = apiKeyService;
        _logger = logger;
    }

    public void Dispose() => _httpClient.Dispose();

    public async Task<ServiceResult<TheAudioDbArtistInfo>> GetArtistMetadataAsync(
        string musicBrainzId,
        string? languageCode = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(musicBrainzId))
            return ServiceResult<TheAudioDbArtistInfo>.FromSuccessNotFound();

        // Checked before the key too, so a failing provider doesn't cost a key request.
        if (_pipelines.IsCircuitOpen(ServiceProviderIds.TheAudioDb))
            return ServiceResult<TheAudioDbArtistInfo>.FromTemporaryError();

        var apiKey = await _apiKeyService.GetApiKeyAsync(ApiKeyName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("TheAudioDB API key not available.");
            return ServiceResult<TheAudioDbArtistInfo>.FromTemporaryError();
        }

        return await _pipelines.GetJsonAsync<TheAudioDbResponse, TheAudioDbArtistInfo>(_httpClient, ServiceProviderIds.TheAudioDb,
            $"{BaseUrl}/{apiKey}/artist-mb.php?i={musicBrainzId}", response =>
            {
                if (response.Artists?.FirstOrDefault() is not { } artist)
                    return ServiceResult<TheAudioDbArtistInfo>.FromSuccessNotFound();

                var info = new TheAudioDbArtistInfo(
                    Biography: ArtistNameHelper.NormalizeStringCore(GetLocalizedBiography(artist.ExtensionData, languageCode) ?? artist.BiographyEN),
                    ThumbUrl: ArtistNameHelper.NormalizeStringCore(artist.ArtistThumb),
                    FanartUrl: ArtistNameHelper.NormalizeStringCore(artist.ArtistFanart)
                               ?? ArtistNameHelper.NormalizeStringCore(artist.ArtistFanart2)
                               ?? ArtistNameHelper.NormalizeStringCore(artist.ArtistFanart3));

                return info.Biography is null && info.ThumbUrl is null && info.FanartUrl is null
                    ? ServiceResult<TheAudioDbArtistInfo>.FromSuccessNotFound()
                    : ServiceResult<TheAudioDbArtistInfo>.FromSuccess(info);
            }, _logger, $"metadata for MBID {musicBrainzId}", cancellationToken).ConfigureAwait(false);
    }

    // Biography fields are named by an upper-case code, which for a few languages is a country rather than the language.
    private static readonly Dictionary<string, string> BiographyFieldCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "CN",
        ["ja"] = "JP",
        ["sv"] = "SE",
        ["he"] = "IL",
        ["nb"] = "NO",
        ["nn"] = "NO"
    };

    private static string? GetLocalizedBiography(Dictionary<string, JsonElement> extensionData, string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode)) return null;
        var language = languageCode.Split('-')[0];
        var fieldCode = BiographyFieldCodes.GetValueOrDefault(language, language.ToUpperInvariant());

        return extensionData.TryGetValue($"strBiography{fieldCode}", out var biography) &&
               biography.ValueKind == JsonValueKind.String
            ? ArtistNameHelper.NormalizeStringCore(biography.GetString())
            : null;
    }
}
