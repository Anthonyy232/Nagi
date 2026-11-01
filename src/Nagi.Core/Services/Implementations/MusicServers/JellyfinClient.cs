using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations.MusicServers;

/// <summary>
///     Talks to Jellyfin 10.11 and later. Requests carry the <c>Authorization: MediaBrowser</c> header, and stream URLs
///     the <c>ApiKey</c> parameter, because Jellyfin 12 turned the legacy ways of passing a token off.
/// </summary>
internal sealed class JellyfinClient : IMusicServerClient
{
    private const int PageSize = 500;
    private const string SongFields = "Genres,DateCreated,MediaSources,ProviderIds,Path";
    private static readonly Version MinimumVersion = new(10, 11);

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _userId;
    private readonly string _accessToken;
    private readonly string _authorizationParameters;

    /// <param name="accessToken">The user's access token, or empty before signing in.</param>
    public JellyfinClient(HttpClient httpClient, string baseUrl, string userId, string accessToken,
        JellyfinClientIdentity identity)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl;
        _userId = userId;
        _accessToken = accessToken;
        _authorizationParameters = AuthorizationParameters(identity, accessToken);
    }

    /// <summary>
    ///     Signs in and checks the account can see a music library.
    /// </summary>
    public static async Task<JellyfinSignIn> SignInAsync(HttpClient httpClient, string baseUrl, string userName,
        string password, JellyfinClientIdentity identity)
    {
        var anonymous = new JellyfinClient(httpClient, baseUrl, string.Empty, string.Empty, identity);
        var info = await anonymous.SendAsync<PublicSystemInfo>(HttpMethod.Get, "/System/Info/Public", null,
            CancellationToken.None).ConfigureAwait(false);
        if (!IsSupported(info.Version))
            throw new MusicServerException(MusicServerError.ServerTooOld, $"{baseUrl} runs Jellyfin {info.Version}.");

        var result = await anonymous.SendAsync<AuthenticationResult>(HttpMethod.Post, "/Users/AuthenticateByName",
            JsonContent.Create(new { Username = userName, Pw = password }), CancellationToken.None).ConfigureAwait(false);

        var client = new JellyfinClient(httpClient, baseUrl, result.User.Id, result.AccessToken, identity);
        if ((await client.GetMusicLibraryIdsAsync(CancellationToken.None).ConfigureAwait(false)).Count == 0)
            throw new MusicServerException(MusicServerError.NoMusicLibrary, $"{userName} can't see a music library on {baseUrl}.");

        var name = string.IsNullOrWhiteSpace(info.ServerName) ? new Uri(baseUrl).Host : info.ServerName;
        return new JellyfinSignIn(name, result.User.Id, result.AccessToken);
    }

    public async IAsyncEnumerable<IReadOnlyList<ServerSongMetadata>> GetSongsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var libraryId in await GetMusicLibraryIdsAsync(cancellationToken).ConfigureAwait(false))
        {
            for (var start = 0; ; start += PageSize)
            {
                var page = await SendAsync<ItemsResult>(HttpMethod.Get,
                    $"/Items?userId={_userId}&parentId={libraryId}&includeItemTypes=Audio&recursive=true" +
                    $"&fields={SongFields}&enableUserData=true&enableImageTypes=Primary&imageTypeLimit=1" +
                    $"&sortBy=SortName&startIndex={start.ToString(CultureInfo.InvariantCulture)}" +
                    $"&limit={PageSize.ToString(CultureInfo.InvariantCulture)}&enableTotalRecordCount=false",
                    null, cancellationToken).ConfigureAwait(false);

                yield return page.Items.Select(ToMetadata).ToList();
                if (page.Items.Count < PageSize) break;
            }
        }
    }

    public async Task<byte[]?> GetCoverArtAsync(string coverArtId, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(HttpMethod.Get,
            $"/Items/{Uri.EscapeDataString(coverArtId)}/Images/Primary?maxWidth=600&quality=90", null,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureSuccess(response);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public Uri GetStreamUri(string songId) =>
        new($"{_baseUrl}/Audio/{Uri.EscapeDataString(songId)}/stream?static=true&ApiKey={Uri.EscapeDataString(_accessToken)}");

    public async Task CheckSongAsync(string songId, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(HttpMethod.Get, $"/Items/{Uri.EscapeDataString(songId)}?userId={_userId}",
            null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    public async Task<string?> GetLyricsAsync(string songId, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(HttpMethod.Get, $"/Audio/{Uri.EscapeDataString(songId)}/Lyrics", null,
            cancellationToken).ConfigureAwait(false);
        // Jellyfin answers 404 for a song without lyrics.
        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        var lines = (await ReadAsync<LyricsResult>(response, cancellationToken).ConfigureAwait(false)).Lyrics;
        return lines is null
            ? null
            : LyricsFormatter.Format(lines.Select(line =>
                (line.Start is { } ticks ? TimeSpan.FromTicks(ticks) : (TimeSpan?)null, line.Text)).ToList());
    }

    public async Task ReportPlayedAsync(string songId, DateTime playedUtc, CancellationToken cancellationToken)
    {
        var datePlayed = DateTime.SpecifyKind(playedUtc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
        using var response = await SendCoreAsync(HttpMethod.Post,
            $"/UserPlayedItems/{Uri.EscapeDataString(songId)}?userId={_userId}&datePlayed={Uri.EscapeDataString(datePlayed)}",
            null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    public async Task SetFavoriteAsync(IReadOnlyCollection<string> songIds, bool isFavorite, CancellationToken cancellationToken)
    {
        var method = isFavorite ? HttpMethod.Post : HttpMethod.Delete;
        foreach (var songId in songIds)
        {
            using var response = await SendCoreAsync(method,
                $"/UserFavoriteItems/{Uri.EscapeDataString(songId)}?userId={_userId}", null, cancellationToken)
                .ConfigureAwait(false);
            // A song deleted from the server can't be a favorite there, and failing would hold back the others.
            if (response.StatusCode != HttpStatusCode.NotFound) EnsureSuccess(response);
        }
    }

    private async Task<List<string>> GetMusicLibraryIdsAsync(CancellationToken cancellationToken)
    {
        var views = await SendAsync<ItemsResult>(HttpMethod.Get, $"/UserViews?userId={_userId}", null, cancellationToken)
            .ConfigureAwait(false);
        return views.Items.Where(view => string.Equals(view.CollectionType, "music", StringComparison.OrdinalIgnoreCase))
            .Select(view => view.Id).ToList();
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string pathAndQuery, HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, pathAndQuery, content, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new MusicServerException(MusicServerError.UnsupportedServer, $"{_baseUrl} has no Jellyfin API.");
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        EnsureSuccess(response);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false)
                   ?? throw new MusicServerException(MusicServerError.UnsupportedServer, $"{_baseUrl} sent an empty answer.");
        }
        catch (JsonException ex)
        {
            throw new MusicServerException(MusicServerError.UnsupportedServer, $"{_baseUrl} didn't answer in Jellyfin JSON.", ex);
        }
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string pathAndQuery, HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, _baseUrl + pathAndQuery) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("MediaBrowser", _authorizationParameters);
        return await MusicServerHttp.SendAsync(_httpClient, request, _baseUrl, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var error = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => MusicServerError.SignInFailed,
            HttpStatusCode.NotFound => MusicServerError.NotFound,
            _ => MusicServerError.ServerError
        };
        throw new MusicServerException(error, $"{_baseUrl} answered {(int)response.StatusCode}.");
    }

    private static string AuthorizationParameters(JellyfinClientIdentity identity, string accessToken)
    {
        var parameters = $"Client=\"{Quote(identity.Name)}\", Device=\"{Quote(identity.DeviceName)}\", " +
                         $"DeviceId=\"{Quote(identity.DeviceId)}\", Version=\"{Quote(identity.Version)}\"";
        return accessToken.Length == 0 ? parameters : $"{parameters}, Token=\"{Quote(accessToken)}\"";

        // Jellyfin reads the values URL-encoded, which also keeps quotes and commas from ending them early.
        static string Quote(string value) => Uri.EscapeDataString(value);
    }

    private static bool IsSupported(string? version)
    {
        // Development builds append a suffix, such as 12.1.0-dev.
        var number = version?.Split('-')[0];
        return Version.TryParse(number, out var parsed) && parsed >= MinimumVersion;
    }

    private static ServerSongMetadata ToMetadata(JellyfinItem item)
    {
        var source = item.MediaSources?.FirstOrDefault();
        var audio = source?.MediaStreams?.FirstOrDefault(stream => stream.Type == "Audio");
        return new ServerSongMetadata
        {
            RemoteId = item.Id,
            // The file's own extension; the container name is vaguer, such as "ogg" for Opus or "asf" for WMA.
            FileExtension = Path.GetExtension(item.Path)?.TrimStart('.') is { Length: > 0 } extension ? extension : null,
            Title = item.Name ?? string.Empty,
            Album = item.Album,
            Artists = item.Artists ?? [],
            AlbumArtists = item.AlbumArtists?.Select(artist => artist.Name).OfType<string>().ToList() ?? [],
            Genres = item.Genres ?? [],
            Duration = TimeSpan.FromTicks(item.RunTimeTicks ?? 0),
            TrackNumber = item.IndexNumber,
            DiscNumber = item.ParentIndexNumber,
            Year = item.ProductionYear,
            Bitrate = source?.Bitrate / 1000,
            SampleRate = audio?.SampleRate,
            Channels = audio?.Channels,
            // Jellyfin normalizes toward -18 LUFS, the reference ReplayGain 2.0 uses, so its gain carries over unchanged.
            ReplayGainTrackGain = item.NormalizationGain,
            MusicBrainzTrackId = ProviderId(item, "MusicBrainzRecording") ?? ProviderId(item, "MusicBrainzTrack"),
            MusicBrainzReleaseId = ProviderId(item, "MusicBrainzAlbum"),
            MusicBrainzReleaseGroupId = ProviderId(item, "MusicBrainzReleaseGroup"),
            CoverArtId = item.AlbumPrimaryImageTag is not null ? item.AlbumId
                : item.ImageTags?.ContainsKey("Primary") == true ? item.Id
                : null,
            IsFavorite = item.UserData?.IsFavorite ?? false,
            PlayCount = item.UserData?.PlayCount ?? 0,
            LastPlayedUtc = item.UserData?.LastPlayedDate?.UtcDateTime,
            AddedUtc = item.DateCreated?.UtcDateTime
        };
    }

    private static string? ProviderId(JellyfinItem item, string provider) =>
        item.ProviderIds?.GetValueOrDefault(provider) is { Length: > 0 } id ? id : null;

    private sealed record PublicSystemInfo(string? ServerName, string? Version);

    private sealed record AuthenticationResult(string AccessToken, JellyfinUser User);

    private sealed record JellyfinUser(string Id);

    private sealed record ItemsResult(List<JellyfinItem> Items);

    private sealed record NamedItem(string? Name);

    private sealed record MediaSource(int? Bitrate, List<MediaStream>? MediaStreams);

    private sealed record MediaStream(string? Type, int? SampleRate, int? Channels);

    private sealed record LyricsResult(List<JellyfinLyricLine>? Lyrics);

    private sealed record JellyfinLyricLine(string? Text, long? Start);

    private sealed record UserItemData(bool IsFavorite, int PlayCount, DateTimeOffset? LastPlayedDate);

    private sealed class JellyfinItem
    {
        public string Id { get; init; } = string.Empty;
        public string? Name { get; init; }
        public string? Path { get; init; }
        public string? CollectionType { get; init; }
        public string? Album { get; init; }
        public string? AlbumId { get; init; }
        public string? AlbumPrimaryImageTag { get; init; }
        public List<NamedItem>? AlbumArtists { get; init; }
        public List<string>? Artists { get; init; }
        public List<string>? Genres { get; init; }
        public long? RunTimeTicks { get; init; }
        public int? IndexNumber { get; init; }
        public int? ParentIndexNumber { get; init; }
        public int? ProductionYear { get; init; }
        public double? NormalizationGain { get; init; }
        public DateTimeOffset? DateCreated { get; init; }
        public Dictionary<string, string>? ImageTags { get; init; }
        public Dictionary<string, string>? ProviderIds { get; init; }
        public List<MediaSource>? MediaSources { get; init; }
        public UserItemData? UserData { get; init; }
    }
}

internal sealed record JellyfinSignIn(string ServerName, string UserId, string AccessToken);

/// <summary>
///     How Nagi introduces itself to Jellyfin, which keeps one sign-in per <paramref name="DeviceId" />, so the ID must
///     stay the same for a server for as long as the server is in the library.
/// </summary>
internal sealed record JellyfinClientIdentity(string Name, string Version, string DeviceName, string DeviceId);
