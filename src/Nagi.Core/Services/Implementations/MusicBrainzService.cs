using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Nagi.Core.Http.Pipelines;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Service for resolving artist identities via the MusicBrainz database.
///     Rate limiting (1 req/s as MusicBrainz requires), retry, and circuit-breaker policy
///     are handled by <see cref="IProviderPipelineProvider"/>.
/// </summary>
public class MusicBrainzService : IMusicBrainzService
{
    private const string BaseUrl = "https://musicbrainz.org/ws/2";
    private const string UserAgent = "Nagi/1.0 (+https://github.com/Anthonyy232/Nagi)";

    // Fuzzy search matches below this score are unrelated releases far more often than not.
    private const int MinimumMatchScore = 80;

    private readonly HttpClient _httpClient;
    private readonly IProviderPipelineProvider _pipelines;
    private readonly ILogger<MusicBrainzService> _logger;

    public MusicBrainzService(
        IHttpClientFactory httpClientFactory,
        IProviderPipelineProvider pipelines,
        ILogger<MusicBrainzService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _pipelines = pipelines;
        _logger = logger;
    }

    public async Task<ServiceResult<string>> SearchArtistAsync(string artistName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artistName))
            return ServiceResult<string>.FromSuccessNotFound();

        if (_pipelines.IsCircuitOpen(ServiceProviderIds.MusicBrainz))
        {
            _logger.LogDebug("MusicBrainz circuit is open; skipping lookup for {ArtistName}.", artistName);
            return ServiceResult<string>.FromTemporaryError();
        }

        // Escape Lucene's quoted-phrase syntax before URL encoding.
        var encodedName = Uri.EscapeDataString($"\"{EscapeLucene(artistName)}\"");
        var url = $"{BaseUrl}/artist?query=artist:{encodedName}&limit=1&fmt=json";

        return await _pipelines.ExecuteWithFallbackAsync(
            ServiceProviderIds.MusicBrainz,
            async ct =>
            {
                _logger.LogDebug("Searching MusicBrainz for artist: {ArtistName}", artistName);
                return await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("MusicBrainz search failed with status {StatusCode} for artist: {ArtistName}",
                        response.StatusCode, artistName);
                    return ServiceResult<string>.FromTemporaryError();
                }

                var result = await response.Content.ReadFromJsonAsync<MusicBrainzSearchResult>(cancellationToken: ct)
                    .ConfigureAwait(false);

                var artist = result?.Artists?.FirstOrDefault();
                if (artist is null)
                {
                    _logger.LogDebug("No MusicBrainz match found for artist: {ArtistName}", artistName);
                    return ServiceResult<string>.FromSuccessNotFound();
                }

                if (artist.Score < MinimumMatchScore)
                {
                    _logger.LogDebug("MusicBrainz match score too low ({Score}) for artist: {ArtistName}",
                        artist.Score, artistName);
                    return ServiceResult<string>.FromSuccessNotFound();
                }

                _logger.LogInformation("Found MusicBrainz ID {MBID} for artist: {ArtistName}", artist.Id, artistName);
                return ServiceResult<string>.FromSuccess(artist.Id);
            },
            fallback: ServiceResult<string>.FromTemporaryError(),
            _logger,
            $"search for {artistName}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ServiceResult<string>> SearchReleaseGroupAsync(string albumTitle, string artistName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return ServiceResult<string>.FromSuccessNotFound();

        if (_pipelines.IsCircuitOpen(ServiceProviderIds.MusicBrainz))
        {
            _logger.LogDebug("MusicBrainz circuit is open; skipping release-group lookup for {AlbumTitle}.",
                albumTitle);
            return ServiceResult<string>.FromTemporaryError();
        }

        var query = $"releasegroup:\"{EscapeLucene(albumTitle)}\"";
        if (!string.IsNullOrWhiteSpace(artistName))
            query += $" AND artist:\"{EscapeLucene(artistName)}\"";

        var url = $"{BaseUrl}/release-group?query={Uri.EscapeDataString(query)}&limit=10&fmt=json";

        return await _pipelines.ExecuteWithFallbackAsync(
            ServiceProviderIds.MusicBrainz,
            async ct =>
            {
                _logger.LogDebug("Searching MusicBrainz for release group: {AlbumTitle} by {ArtistName}",
                    albumTitle, artistName);
                return await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "MusicBrainz release-group search failed with status {StatusCode} for album: {AlbumTitle}",
                        response.StatusCode, albumTitle);
                    return ServiceResult<string>.FromTemporaryError();
                }

                var result = await response.Content.ReadFromJsonAsync<MusicBrainzReleaseGroupSearchResult>(
                    cancellationToken: ct).ConfigureAwait(false);

                var releaseGroup = SelectReleaseGroup(result?.ReleaseGroups, albumTitle);
                if (releaseGroup is null)
                {
                    _logger.LogDebug("No MusicBrainz release group found for album: {AlbumTitle}", albumTitle);
                    return ServiceResult<string>.FromSuccessNotFound();
                }

                _logger.LogInformation("Found MusicBrainz release group {MBID} for album: {AlbumTitle}",
                    releaseGroup.Id, albumTitle);
                return ServiceResult<string>.FromSuccess(releaseGroup.Id);
            },
            fallback: ServiceResult<string>.FromTemporaryError(),
            _logger,
            $"release-group search for {albumTitle}",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Picks the release group that most likely is the album the user has locally.
    ///     Scores alone are not enough: fan-made remixes and demos of a popular album can rank
    ///     above the original release.
    /// </summary>
    private static MusicBrainzReleaseGroup? SelectReleaseGroup(
        IEnumerable<MusicBrainzReleaseGroup>? candidates, string albumTitle)
    {
        return candidates?
            .Where(c => c.Score >= MinimumMatchScore && !string.IsNullOrEmpty(c.Id))
            .Where(c => c.SecondaryTypes is null ||
                        !c.SecondaryTypes.Any(t => t is "Remix" or "Demo"))
            .OrderBy(c => string.Equals(c.Title?.Trim(), albumTitle.Trim(), StringComparison.OrdinalIgnoreCase)
                ? 0
                : 1)
            .ThenBy(c => string.Equals(c.PrimaryType, "Album", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(c => c.FirstReleaseDate ?? "9999")
            .FirstOrDefault();
    }

    private static string EscapeLucene(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private sealed class MusicBrainzSearchResult
    {
        [JsonPropertyName("artists")]
        public List<MusicBrainzArtist>? Artists { get; set; }
    }

    private sealed class MusicBrainzArtist
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("score")]
        public int Score { get; set; }
    }

    private sealed class MusicBrainzReleaseGroupSearchResult
    {
        [JsonPropertyName("release-groups")]
        public List<MusicBrainzReleaseGroup>? ReleaseGroups { get; set; }
    }

    private sealed class MusicBrainzReleaseGroup
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("score")]
        public int Score { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("primary-type")]
        public string? PrimaryType { get; set; }

        [JsonPropertyName("secondary-types")]
        public List<string>? SecondaryTypes { get; set; }

        [JsonPropertyName("first-release-date")]
        public string? FirstReleaseDate { get; set; }
    }
}
