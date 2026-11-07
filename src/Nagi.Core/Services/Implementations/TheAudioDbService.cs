using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Http;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

public class TheAudioDbService : ITheAudioDbService
{
    private const string BaseUrl = "https://www.theaudiodb.com/api/v1/json";

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

    private readonly ProviderClient _client;

    public TheAudioDbService(
        IHttpClientFactory httpClientFactory,
        ProviderPipelineProvider pipelines,
        IApiKeyService apiKeyService,
        ILogger<TheAudioDbService> logger)
    {
        _client = new ProviderClient(ProviderCatalog.TheAudioDb.Policy, httpClientFactory.CreateClient(), pipelines, logger,
            apiKeyService, ApiKeyNames.TheAudioDb);
    }

    public Task<ServiceResult<TheAudioDbArtistInfo>> GetArtistMetadataAsync(
        string musicBrainzId,
        string? languageCode = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(musicBrainzId))
            return Task.FromResult(ServiceResult<TheAudioDbArtistInfo>.FromSuccessNotFound());

        return _client.GetJsonAsync<TheAudioDbResponse, TheAudioDbArtistInfo>(
            apiKey => $"{BaseUrl}/{apiKey}/artist-mb.php?i={musicBrainzId}", response =>
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
            }, $"metadata for MBID {musicBrainzId}", cancellationToken);
    }

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

    private sealed class TheAudioDbResponse
    {
        [JsonPropertyName("artists")]
        public List<TheAudioDbArtist>? Artists { get; set; }
    }

    private sealed class TheAudioDbArtist
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
}
