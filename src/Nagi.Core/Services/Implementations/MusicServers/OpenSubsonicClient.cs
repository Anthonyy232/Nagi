using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations.MusicServers;

/// <summary>
///     Talks to servers that implement the OpenSubsonic API, such as Navidrome, Gonic and Ampache. Only servers that
///     declare OpenSubsonic support are accepted. Requests sign in with an API key or a salted password token, never
///     the password itself, and ask for JSON.
/// </summary>
internal sealed class OpenSubsonicClient : IMusicServerClient
{
    // OpenSubsonic extends the last Subsonic API version instead of raising it.
    private const string ApiVersion = "1.16.1";
    private const int PageSize = 500;
    private const int FavoriteBatchSize = 100;
    private const string CoverArtSize = "600";

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _userName;
    private readonly string _secret;
    private readonly bool _usesApiKey;
    private readonly string _clientName;

    /// <param name="secret">The API key when <paramref name="usesApiKey" /> is set, otherwise the password.</param>
    public OpenSubsonicClient(HttpClient httpClient, string baseUrl, string userName, string secret, bool usesApiKey,
        string clientName)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl;
        _userName = userName;
        _secret = secret;
        _usesApiKey = usesApiKey;
        _clientName = clientName;
    }

    /// <inheritdoc cref="MusicServerHttp.NormalizeAddress" />
    public static string NormalizeAddress(string address)
    {
        var baseUrl = MusicServerHttp.NormalizeAddress(address);
        // People often paste the API address, which ends in /rest.
        return baseUrl.EndsWith("/rest", StringComparison.OrdinalIgnoreCase) ? baseUrl[..^"/rest".Length] : baseUrl;
    }

    /// <summary>
    ///     Signs in and checks the server speaks OpenSubsonic.
    /// </summary>
    /// <returns>The server's name, and the user name, which an API key sign-in learns from the server.</returns>
    public static async Task<(string ServerName, string UserName)> ConnectAsync(HttpClient httpClient, string baseUrl,
        string userName, string secret, bool usesApiKey, string clientName)
    {
        var client = new OpenSubsonicClient(httpClient, baseUrl, userName, secret, usesApiKey, clientName);
        var response = await client.SendAsync("ping", [], CancellationToken.None).ConfigureAwait(false);
        if (!response.OpenSubsonic)
            throw new MusicServerException(MusicServerError.UnsupportedServer, $"{baseUrl} implements the Subsonic API but not OpenSubsonic.");

        if (usesApiKey)
            userName = (await client.SendAsync("tokenInfo", [], CancellationToken.None).ConfigureAwait(false)).TokenInfo?.Username
                       ?? throw new MusicServerException(MusicServerError.UnsupportedServer, $"{baseUrl} didn't name the API key's user.");

        return (ToDisplayName(response.Type) ?? new Uri(baseUrl).Host, userName);
    }

    public async IAsyncEnumerable<IReadOnlyList<ServerSongMetadata>> GetSongsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // OpenSubsonic requires search3 to return every song for an empty query, so clients can sync the library.
        for (var offset = 0; ; offset += PageSize)
        {
            var response = await SendAsync("search3",
            [
                ("query", ""),
                ("songCount", PageSize.ToString(CultureInfo.InvariantCulture)),
                ("songOffset", offset.ToString(CultureInfo.InvariantCulture)),
                ("artistCount", "0"),
                ("albumCount", "0")
            ], cancellationToken).ConfigureAwait(false);

            var songs = response.SearchResult3?.Song ?? [];
            yield return songs.Where(song => song.IsVideo != true).Select(ToMetadata).ToList();
            if (songs.Count < PageSize) yield break;
        }
    }

    public async Task<byte[]?> GetCoverArtAsync(string coverArtId, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(
            BuildUri("getCoverArt", [("id", coverArtId), ("size", CoverArtSize), ("f", "json")]), cancellationToken)
            .ConfigureAwait(false);
        if (response.IsSuccessStatusCode &&
            response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        // Anything but image data is an API error, and a missing image is only one of them.
        try
        {
            await ReadResponseAsync(response, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (MusicServerException ex) when (ex.Error == MusicServerError.NotFound)
        {
            return null;
        }
    }

    public Uri GetStreamUri(string songId) => BuildUri("stream", [("id", songId), ("format", "raw")]);

    public Task CheckSongAsync(string songId, CancellationToken cancellationToken) =>
        SendAsync("getSong", [("id", songId)], cancellationToken);

    public async Task<string?> GetLyricsAsync(string songId, CancellationToken cancellationToken)
    {
        // Servers return a set per language and timing; timed lyrics follow along with the song.
        var sets = (await SendAsync("getLyricsBySongId", [("id", songId)], cancellationToken).ConfigureAwait(false))
            .LyricsList?.StructuredLyrics;
        var lyrics = sets?.FirstOrDefault(set => set.Synced) ?? sets?.FirstOrDefault();
        if (lyrics?.Line is null) return null;

        // A positive offset shows every line that much sooner.
        var offset = lyrics.Offset ?? 0;
        return LyricsFormatter.Format(lyrics.Line.Select(line => (
            line.Start is { } start ? TimeSpan.FromMilliseconds(Math.Max(0, start - offset)) : (TimeSpan?)null,
            line.Value)).ToList());
    }

    public Task ReportPlayedAsync(string songId, DateTime playedUtc, CancellationToken cancellationToken)
    {
        var time = new DateTimeOffset(DateTime.SpecifyKind(playedUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        return SendAsync("scrobble",
            [("id", songId), ("time", time.ToString(CultureInfo.InvariantCulture)), ("submission", "true")],
            cancellationToken);
    }

    public async Task SetFavoriteAsync(IReadOnlyCollection<string> songIds, bool isFavorite, CancellationToken cancellationToken)
    {
        foreach (var batch in songIds.Chunk(FavoriteBatchSize))
            await SendAsync(isFavorite ? "star" : "unstar", batch.Select(id => ("id", id)), cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<SubsonicResponse> SendAsync(string endpoint, IEnumerable<(string Name, string Value)> parameters,
        CancellationToken cancellationToken)
    {
        using var response = await GetAsync(BuildUri(endpoint, parameters.Append(("f", "json"))), cancellationToken)
            .ConfigureAwait(false);
        return await ReadResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SubsonicResponse> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // API errors arrive as HTTP 200 with a "failed" status, so other statuses come from a wrong address or a proxy.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new MusicServerException(MusicServerError.SignInFailed, $"{_baseUrl} answered {(int)response.StatusCode}.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new MusicServerException(MusicServerError.UnsupportedServer, $"{_baseUrl} has no Subsonic API.");
        if (!response.IsSuccessStatusCode)
            throw new MusicServerException(MusicServerError.ServerError, $"{_baseUrl} answered {(int)response.StatusCode}.");

        SubsonicEnvelope? envelope;
        try
        {
            envelope = await response.Content.ReadFromJsonAsync<SubsonicEnvelope>(cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new MusicServerException(MusicServerError.UnsupportedServer, $"{_baseUrl} didn't answer in OpenSubsonic JSON.", ex);
        }

        var result = envelope?.Response
                     ?? throw new MusicServerException(MusicServerError.UnsupportedServer, $"{_baseUrl} didn't answer in OpenSubsonic JSON.");
        if (result.Status == "ok") return result;

        var code = result.Error?.Code;
        var error = code switch
        {
            // Wrong user name or password, or an unknown API key.
            40 or 44 => MusicServerError.SignInFailed,
            // The account or server takes the other way of signing in, such as an API key instead of a password.
            41 or 42 => MusicServerError.SignInMethodUnsupported,
            70 => MusicServerError.NotFound,
            _ => MusicServerError.ServerError
        };
        throw new MusicServerException(error, $"{_baseUrl} reported error {code}: {result.Error?.Message}");
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        return await MusicServerHttp.SendAsync(_httpClient, request, _baseUrl, cancellationToken).ConfigureAwait(false);
    }

    private Uri BuildUri(string endpoint, IEnumerable<(string Name, string Value)> parameters)
    {
        var query = new StringBuilder();
        if (_usesApiKey)
        {
            // The server rejects an API key sent together with a user name.
            Append("apiKey", _secret);
        }
        else
        {
            var salt = RandomNumberGenerator.GetHexString(16, true);
            Append("u", _userName);
            Append("t", CreateToken(salt));
            Append("s", salt);
        }

        Append("v", ApiVersion);
        Append("c", _clientName);
        foreach (var (name, value) in parameters) Append(name, value);
        return new Uri($"{_baseUrl}/rest/{endpoint}.view?{query}");

        void Append(string name, string value)
        {
            if (query.Length > 0) query.Append('&');
            query.Append(name).Append('=').Append(Uri.EscapeDataString(value));
        }
    }

    // The protocol defines the token as MD5(password + salt), so the algorithm isn't ours to choose.
    private string CreateToken(string salt) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(_secret + salt)));

    private static string? ToDisplayName(string? type) =>
        string.IsNullOrWhiteSpace(type) ? null : char.ToUpperInvariant(type[0]) + type[1..];

    private static ServerSongMetadata ToMetadata(SubsonicSong song) => new()
    {
        RemoteId = song.Id,
        FileExtension = NullIfEmpty(song.Suffix),
        Title = song.Title ?? string.Empty,
        Album = song.Album,
        // OpenSubsonic servers list every artist; the single legacy field is a fallback for incomplete tags.
        Artists = Names(song.Artists, song.Artist),
        AlbumArtists = Names(song.AlbumArtists, null),
        Genres = Names(song.Genres, song.Genre),
        Duration = TimeSpan.FromSeconds(song.Duration ?? 0),
        TrackNumber = Positive(song.Track),
        DiscNumber = Positive(song.DiscNumber),
        Year = Positive(song.Year),
        Bitrate = Positive(song.BitRate),
        SampleRate = Positive(song.SamplingRate),
        Channels = Positive(song.ChannelCount),
        Bpm = Positive(song.Bpm),
        Comment = NullIfEmpty(song.Comment),
        Composer = NullIfEmpty(song.DisplayComposer),
        MusicBrainzTrackId = NullIfEmpty(song.MusicBrainzId),
        ReplayGainTrackGain = song.ReplayGain?.TrackGain,
        ReplayGainTrackPeak = song.ReplayGain?.TrackPeak,
        CoverArtId = NullIfEmpty(song.CoverArt),
        IsFavorite = song.Starred is not null,
        PlayCount = song.PlayCount ?? 0,
        LastPlayedUtc = song.Played?.UtcDateTime,
        AddedUtc = song.Created?.UtcDateTime
    };

    private static List<string> Names(List<SubsonicName>? names, string? fallback)
    {
        var list = names?.Select(n => n.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList() ?? [];
        if (list.Count == 0 && !string.IsNullOrWhiteSpace(fallback)) list.Add(fallback);
        return list;
    }

    private static int? Positive(int? value) => value > 0 ? value : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record SubsonicEnvelope([property: JsonPropertyName("subsonic-response")] SubsonicResponse? Response);

    private sealed record SubsonicResponse(
        string? Status,
        bool OpenSubsonic,
        string? Type,
        SubsonicError? Error,
        SubsonicTokenInfo? TokenInfo,
        SubsonicSearchResult? SearchResult3,
        SubsonicLyricsList? LyricsList);

    private sealed record SubsonicError(int Code, string? Message);

    private sealed record SubsonicTokenInfo(string? Username);

    private sealed record SubsonicSearchResult(List<SubsonicSong>? Song);

    private sealed record SubsonicName(string? Name);

    private sealed record SubsonicReplayGain(double? TrackGain, double? TrackPeak);

    private sealed record SubsonicLyricsList(List<SubsonicLyrics>? StructuredLyrics);

    private sealed record SubsonicLyrics(bool Synced, long? Offset, List<SubsonicLyricLine>? Line);

    private sealed record SubsonicLyricLine(long? Start, string? Value);

    private sealed class SubsonicSong
    {
        public string Id { get; init; } = string.Empty;
        public string? Suffix { get; init; }
        public string? Title { get; init; }
        public string? Album { get; init; }
        public string? Artist { get; init; }
        public string? Genre { get; init; }
        public List<SubsonicName>? Artists { get; init; }
        public List<SubsonicName>? AlbumArtists { get; init; }
        public List<SubsonicName>? Genres { get; init; }
        public int? Track { get; init; }
        public int? DiscNumber { get; init; }
        public int? Year { get; init; }
        public int? Duration { get; init; }
        public int? BitRate { get; init; }
        public int? SamplingRate { get; init; }
        public int? ChannelCount { get; init; }
        public int? Bpm { get; init; }
        public string? Comment { get; init; }
        public string? DisplayComposer { get; init; }
        public string? MusicBrainzId { get; init; }
        public SubsonicReplayGain? ReplayGain { get; init; }
        public string? CoverArt { get; init; }
        public bool? IsVideo { get; init; }
        public int? PlayCount { get; init; }
        public DateTimeOffset? Starred { get; init; }
        public DateTimeOffset? Played { get; init; }
        public DateTimeOffset? Created { get; init; }
    }
}
