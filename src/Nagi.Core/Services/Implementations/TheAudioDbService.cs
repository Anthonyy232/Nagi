using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

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

        if (_pipelines.IsCircuitOpen(ServiceProviderIds.TheAudioDb))
            return ServiceResult<TheAudioDbArtistInfo>.FromTemporaryError();

        var apiKey = await _apiKeyService.GetApiKeyAsync(ApiKeyName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("TheAudioDB API key not available.");
            return ServiceResult<TheAudioDbArtistInfo>.FromTemporaryError();
        }

        var url = $"{BaseUrl}/{apiKey}/artist-mb.php?i={musicBrainzId}";
        var isoCode = languageCode?.Length > 2 ? languageCode[..2] : (languageCode ?? "Default");

        return await _pipelines.ExecuteWithFallbackAsync(
            ServiceProviderIds.TheAudioDb,
            async ct =>
            {
                _logger.LogDebug("Fetching TheAudioDB metadata for MBID: {MBID} (Language: {LanguageCode})",
                    musicBrainzId, isoCode);
                return await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogDebug("No TheAudioDB data found for MBID: {MBID}", musicBrainzId);
                    return ServiceResult<TheAudioDbArtistInfo>.FromSuccessNotFound();
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("TheAudioDB request failed with status {StatusCode} for MBID: {MBID}",
                        response.StatusCode, musicBrainzId);
                    return ServiceResult<TheAudioDbArtistInfo>.FromTemporaryError();
                }

                var apiResult = await response.Content.ReadFromJsonAsync<TheAudioDbResponse>(cancellationToken: ct)
                    .ConfigureAwait(false);

                var artist = apiResult?.Artists?.FirstOrDefault();
                if (artist is null)
                {
                    _logger.LogDebug("TheAudioDB returned null/empty artist data for MBID: {MBID}", musicBrainzId);
                    return ServiceResult<TheAudioDbArtistInfo>.FromSuccessNotFound();
                }

                var bio = GetLocalizedBiography(artist.ExtensionData, languageCode);
                var isLocalizedBio = !string.IsNullOrEmpty(bio);
                bio ??= artist.BiographyEN;

                var info = new TheAudioDbArtistInfo(
                    Biography: ArtistNameHelper.NormalizeStringCore(bio),
                    ThumbUrl: ArtistNameHelper.NormalizeStringCore(artist.ArtistThumb),
                    FanartUrl: ArtistNameHelper.NormalizeStringCore(artist.ArtistFanart)
                               ?? ArtistNameHelper.NormalizeStringCore(artist.ArtistFanart2)
                               ?? ArtistNameHelper.NormalizeStringCore(artist.ArtistFanart3)
                );

                if (info.Biography is null && info.ThumbUrl is null && info.FanartUrl is null)
                {
                    _logger.LogDebug("TheAudioDB returned empty metadata for MBID: {MBID}", musicBrainzId);
                    return ServiceResult<TheAudioDbArtistInfo>.FromSuccessNotFound();
                }

                _logger.LogInformation("Found TheAudioDB metadata for MBID: {MBID} (Language: {LanguageCode}): Bio={HasBio} ({BioSource}), Thumb={HasThumb}, Fanart={HasFanart}",
                    musicBrainzId, isoCode, !string.IsNullOrEmpty(info.Biography), isLocalizedBio ? isoCode : "en",
                    !string.IsNullOrEmpty(info.ThumbUrl), !string.IsNullOrEmpty(info.FanartUrl));
                return ServiceResult<TheAudioDbArtistInfo>.FromSuccess(info);
            },
            fallback: ServiceResult<TheAudioDbArtistInfo>.FromTemporaryError(),
            _logger,
            $"fetch metadata for MBID {musicBrainzId}",
            cancellationToken).ConfigureAwait(false);
    }

    private static string? GetLocalizedBiography(Dictionary<string, JsonElement> extensionData, string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
            return null;

        var isoCode = languageCode.Length > 2 ? languageCode[..2] : languageCode;

        return extensionData.TryGetValue($"strBiography{isoCode}", out var biography) &&
               biography.ValueKind == JsonValueKind.String
            ? ArtistNameHelper.NormalizeStringCore(biography.GetString())
            : null;
    }
}
