using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Finds artist biographies on Wikipedia through Wikidata, which records each artist's MusicBrainz ID
///     (property P434). Wikimedia asks API clients to identify themselves with a User-Agent.
/// </summary>
public class WikipediaService : IWikipediaService, IDisposable
{
    private const string WikidataApiUrl = "https://www.wikidata.org/w/api.php";
    private const string UserAgent = "Nagi/1.0 (https://github.com/Anthonyy232/Nagi)";
    private const string FallbackLanguage = "en";

    private readonly HttpClient _httpClient;
    private readonly IProviderPipelineProvider _pipelines;
    private readonly ILogger<WikipediaService> _logger;

    public WikipediaService(
        IHttpClientFactory httpClientFactory,
        IProviderPipelineProvider pipelines,
        ILogger<WikipediaService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _pipelines = pipelines;
        _logger = logger;
    }

    public void Dispose() => _httpClient.Dispose();

    public async Task<ServiceResult<string>> GetArtistBiographyAsync(
        string musicBrainzId, string? languageCode = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(musicBrainzId))
            return ServiceResult<string>.FromSuccessNotFound();

        var language = ToWikipediaLanguage(languageCode);

        var itemId = await GetAsync<WikidataSearchResponse, string>(
            $"{WikidataApiUrl}?action=query&list=search&srsearch=haswbstatement:P434={Uri.EscapeDataString(musicBrainzId)}&srlimit=1&format=json",
            response => response.Query?.Search?.FirstOrDefault()?.Title,
            $"Wikidata item for MBID {musicBrainzId}", cancellationToken).ConfigureAwait(false);
        if (itemId.Data is null) return itemId;

        var siteFilter = language == FallbackLanguage ? "enwiki" : $"{language}wiki|enwiki";
        var article = await GetAsync<WikidataEntitiesResponse, Article>(
            $"{WikidataApiUrl}?action=wbgetentities&ids={itemId.Data}&props=sitelinks&sitefilter={Uri.EscapeDataString(siteFilter)}&format=json",
            response => SelectArticle(response, itemId.Data, language),
            $"Wikipedia article for {itemId.Data}", cancellationToken).ConfigureAwait(false);
        if (article.Data is null)
            return article.IsConclusive ? ServiceResult<string>.FromSuccessNotFound() : ServiceResult<string>.FromTemporaryError();

        var (articleLanguage, title) = article.Data;
        return await GetAsync<WikipediaExtractResponse, string>(
            $"https://{articleLanguage}.wikipedia.org/w/api.php?action=query&prop=extracts&exintro=1&explaintext=1&redirects=1&format=json&formatversion=2&titles={Uri.EscapeDataString(title)}",
            response => response.Query?.Pages?.FirstOrDefault()?.Extract is { } extract && !string.IsNullOrWhiteSpace(extract)
                ? $"{extract.Trim()}\n\n{Resources.Strings.Biography_WikipediaAttribution}"
                : null,
            $"Wikipedia extract for {title}", cancellationToken).ConfigureAwait(false);
    }

    // Wikipedia names a few editions differently from the ISO 639-1 codes .NET reports.
    private static string ToWikipediaLanguage(string? languageCode) =>
        languageCode?.ToLowerInvariant() switch
        {
            null or "" or "iv" => FallbackLanguage,
            "nb" => "no",
            "fil" => "tl",
            var code => code
        };

    private static Article? SelectArticle(WikidataEntitiesResponse response, string itemId, string language)
    {
        if (response.Entities?.GetValueOrDefault(itemId)?.Sitelinks is not { } sitelinks) return null;
        if (sitelinks.GetValueOrDefault($"{language}wiki")?.Title is { } localTitle) return new Article(language, localTitle);
        return sitelinks.GetValueOrDefault("enwiki")?.Title is { } englishTitle ? new Article(FallbackLanguage, englishTitle) : null;
    }

    private async Task<ServiceResult<TResult>> GetAsync<TResponse, TResult>(
        string url, Func<TResponse, TResult?> select, string operation, CancellationToken cancellationToken)
        where TResult : class
    {
        if (_pipelines.IsCircuitOpen(ServiceProviderIds.Wikipedia))
            return ServiceResult<TResult>.FromTemporaryError();

        return await _pipelines.ExecuteWithFallbackAsync(
            ServiceProviderIds.Wikipedia,
            async ct =>
            {
                _logger.LogDebug("Fetching {Operation}", operation);
                return await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Wikimedia request failed with status {StatusCode} for {Operation}",
                        response.StatusCode, operation);
                    return ServiceResult<TResult>.FromTemporaryError();
                }

                var body = await response.Content.ReadFromJsonAsync<TResponse>(ct).ConfigureAwait(false);
                return body is not null && select(body) is { } result
                    ? ServiceResult<TResult>.FromSuccess(result)
                    : ServiceResult<TResult>.FromSuccessNotFound();
            },
            fallback: ServiceResult<TResult>.FromTemporaryError(),
            _logger,
            operation,
            cancellationToken).ConfigureAwait(false);
    }

    private sealed record Article(string Language, string Title);

    private sealed class WikidataSearchResponse
    {
        [JsonPropertyName("query")]
        public SearchQuery? Query { get; set; }

        public sealed class SearchQuery
        {
            [JsonPropertyName("search")]
            public List<SearchHit>? Search { get; set; }
        }

        public sealed class SearchHit
        {
            [JsonPropertyName("title")]
            public string? Title { get; set; }
        }
    }

    private sealed class WikidataEntitiesResponse
    {
        [JsonPropertyName("entities")]
        public Dictionary<string, Entity>? Entities { get; set; }

        public sealed class Entity
        {
            [JsonPropertyName("sitelinks")]
            public Dictionary<string, Sitelink>? Sitelinks { get; set; }
        }

        public sealed class Sitelink
        {
            [JsonPropertyName("title")]
            public string? Title { get; set; }
        }
    }

    private sealed class WikipediaExtractResponse
    {
        [JsonPropertyName("query")]
        public ExtractQuery? Query { get; set; }

        public sealed class ExtractQuery
        {
            [JsonPropertyName("pages")]
            public List<Page>? Pages { get; set; }
        }

        public sealed class Page
        {
            [JsonPropertyName("extract")]
            public string? Extract { get; set; }
        }
    }
}
