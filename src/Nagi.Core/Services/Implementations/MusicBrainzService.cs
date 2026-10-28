using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Helpers;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Rate limiting (1 req/s as MusicBrainz requires), retry, and circuit-breaker policy
///     are handled by <see cref="IProviderPipelineProvider"/>.
/// </summary>
public class MusicBrainzService : IMusicBrainzService
{
    private const string BaseUrl = "https://musicbrainz.org/ws/2";

    private readonly HttpClient _httpClient;
    private readonly IProviderPipelineProvider _pipelines;
    private readonly ILogger<MusicBrainzService> _logger;

    public MusicBrainzService(
        IHttpClientFactory httpClientFactory,
        IProviderPipelineProvider pipelines,
        ILogger<MusicBrainzService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _pipelines = pipelines;
        _logger = logger;
    }

    public Task<ServiceResult<string>> SearchArtistAsync(string artistName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artistName))
            return Task.FromResult(ServiceResult<string>.FromSuccessNotFound());

        return SearchAsync($"artist?query=artist:{Phrase(artistName)}", result => result.Artists,
            minimumScore: 80, $"artist: {artistName}", cancellationToken);
    }

    public Task<ServiceResult<string>> SearchReleaseGroupAsync(string albumTitle, string artistName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(albumTitle) || string.IsNullOrWhiteSpace(artistName))
            return Task.FromResult(ServiceResult<string>.FromSuccessNotFound());

        // Both phrases must score well; a looser match would attach another album's cover.
        return SearchAsync(
            $"release-group?query=releasegroup:{Phrase(albumTitle)}%20AND%20artist:{Phrase(artistName)}",
            result => result.ReleaseGroups, minimumScore: 90, $"album: {albumTitle} by {artistName}", cancellationToken);
    }

    // Escape Lucene's quoted-phrase syntax before URL encoding.
    private static string Phrase(string value) =>
        Uri.EscapeDataString($"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");

    private Task<ServiceResult<string>> SearchAsync(string pathAndQuery,
        Func<MusicBrainzSearchResult, List<MusicBrainzMatch>?> selectMatches, int minimumScore, string subject,
        CancellationToken cancellationToken) =>
        _pipelines.GetJsonAsync<MusicBrainzSearchResult, string>(_httpClient, ServiceProviderIds.MusicBrainz,
            $"{BaseUrl}/{pathAndQuery}&limit=1&fmt=json",
            result =>
            {
                var match = selectMatches(result)?.FirstOrDefault();
                if (match is null)
                {
                    _logger.LogDebug("No MusicBrainz match found for {Subject}", subject);
                    return ServiceResult<string>.FromSuccessNotFound();
                }

                if (match.Score < minimumScore)
                {
                    _logger.LogDebug("MusicBrainz match score too low ({Score}) for {Subject}", match.Score, subject);
                    return ServiceResult<string>.FromSuccessNotFound();
                }

                _logger.LogInformation("Found MusicBrainz ID {MBID} for {Subject}", match.Id, subject);
                return ServiceResult<string>.FromSuccess(match.Id);
            },
            _logger, $"MusicBrainz search for {subject}", cancellationToken);

    private sealed class MusicBrainzSearchResult
    {
        [JsonPropertyName("artists")]
        public List<MusicBrainzMatch>? Artists { get; set; }

        [JsonPropertyName("release-groups")]
        public List<MusicBrainzMatch>? ReleaseGroups { get; set; }
    }

    private sealed class MusicBrainzMatch
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("score")]
        public int Score { get; set; }
    }
}
