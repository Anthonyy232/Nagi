using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Nagi.Core.Library;
using Nagi.Core.Lyrics;
using Nagi.Core.Playlists;
using Nagi.Core.Services.Abstractions;
using static Nagi.Core.MusicServers.MusicServerHttp;

namespace Nagi.Core.MusicServers;

/// <summary>
///     Talks to Jellyfin 10.11 and later. Requests carry the <c>Authorization: MediaBrowser</c> header, and stream URLs
///     the <c>ApiKey</c> parameter, because Jellyfin 12 turned the legacy ways of passing a token off.
/// </summary>
internal sealed class JellyfinClient : IMusicServerClient
{
    private const string SongFields = "Genres,DateCreated,MediaSources,ProviderIds,Path,People";
    private static readonly Version MinimumVersion = new(10, 11);

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _userId;
    private readonly string _accessToken;
    private readonly string _authorizationParameters;

    public JellyfinClient(HttpClient httpClient, string baseUrl, string userId, string accessToken,
        JellyfinClientIdentity identity)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl;
        _userId = userId;
        _accessToken = accessToken;
        _authorizationParameters = $"{AuthorizationParameters(identity)}, Token=\"{Quote(accessToken)}\"";
    }

    /// <summary>
    ///     Signs in and checks the account can see a music library.
    /// </summary>
    internal static async Task<ServerSignIn> SignInAsync(HttpClient httpClient, MusicServer server, string password,
        JellyfinClientIdentity identity)
    {
        var baseUrl = server.BaseUrl;
        // Before signing in, requests introduce Nagi without a token.
        using var publicInfoRequest = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/System/Info/Public");
        var info = await SendAnonymousAsync<PublicSystemInfo>(httpClient, publicInfoRequest, baseUrl, identity).ConfigureAwait(false);
        if (!IsSupported(info.Version))
            throw new MusicServerException(MusicServerError.ServerTooOld, $"{baseUrl} runs Jellyfin {info.Version}.");

        using var signInRequest = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/Users/AuthenticateByName")
        {
            Content = JsonContent.Create(new { Username = server.UserName, Pw = password })
        };
        var result = await SendAnonymousAsync<AuthenticationResult>(httpClient, signInRequest, baseUrl, identity).ConfigureAwait(false);

        var client = new JellyfinClient(httpClient, baseUrl, result.User.Id, result.AccessToken, identity);
        if ((await client.GetMusicLibraryIdsAsync(CancellationToken.None).ConfigureAwait(false)).Count == 0)
            throw new MusicServerException(MusicServerError.NoMusicLibrary, $"{server.UserName} can't see a music library on {baseUrl}.");

        // The access token stands in for the password, which isn't kept.
        return new ServerSignIn(ServerName(info.ServerName, baseUrl), server.UserName, result.User.Id, result.AccessToken);
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

    public async Task<IReadOnlyList<ServerPlaylist>> GetPlaylistsAsync(CancellationToken cancellationToken)
    {
        var listed = await SendAsync<ItemsResult>(HttpMethod.Get,
            $"/Items?userId={_userId}&includeItemTypes=Playlist&mediaTypes=Audio&recursive=true&fields=DateCreated&enableUserData=false",
            null, cancellationToken).ConfigureAwait(false);
        var playlists = new List<ServerPlaylist>();
        foreach (var playlist in listed.Items)
        {
            using var response = await SendCoreAsync(HttpMethod.Get,
                $"/Playlists/{Uri.EscapeDataString(playlist.Id)}/Items?userId={_userId}&enableImages=false&enableUserData=false",
                null, cancellationToken).ConfigureAwait(false);
            // Deleted since it was listed.
            if (response.StatusCode == HttpStatusCode.NotFound) continue;
            var songs = await ReadAsync<ItemsResult>(response, _baseUrl, cancellationToken).ConfigureAwait(false);
            playlists.Add(new ServerPlaylist(playlist.Id, playlist.Name ?? string.Empty, playlist.DateCreated?.UtcDateTime,
                songs.Items.Select(song => song.Id).ToList()));
        }

        return playlists;
    }

    public async Task<byte[]?> GetCoverArtAsync(string coverArtId, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(HttpMethod.Get,
            $"/Items/{Uri.EscapeDataString(coverArtId)}/Images/Primary?maxWidth={CoverSize.ToString(CultureInfo.InvariantCulture)}&quality=90",
            null, cancellationToken).ConfigureAwait(false);
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

        var lines = (await ReadAsync<LyricsResult>(response, _baseUrl, cancellationToken).ConfigureAwait(false)).Lyrics;
        return lines is null
            ? null
            : LyricsFormatter.Format(lines.Select(line =>
                (line.Start is { } ticks ? TimeSpan.FromTicks(ticks) : (TimeSpan?)null, line.Text)).ToList());
    }

    public async Task ReportPlayedAsync(string songId, DateTime playedUtc, CancellationToken cancellationToken)
    {
        var datePlayed = playedUtc.ToString("O", CultureInfo.InvariantCulture);
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

    // Also takes Nagi off the server's list of devices.
    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(HttpMethod.Post, "/Sessions/Logout", null, cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response);
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
        return await ReadApiAsync<T>(response, _baseUrl, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> SendAnonymousAsync<T>(HttpClient httpClient, HttpRequestMessage request, string baseUrl,
        JellyfinClientIdentity identity)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("MediaBrowser", AuthorizationParameters(identity));
        using var response = await MusicServerHttp.SendAsync(httpClient, request, baseUrl, CancellationToken.None).ConfigureAwait(false);
        return await ReadApiAsync<T>(response, baseUrl, CancellationToken.None).ConfigureAwait(false);
    }

    // An API every Jellyfin server has answers 404 only when the address isn't a Jellyfin server.
    private static Task<T> ReadApiAsync<T>(HttpResponseMessage response, string baseUrl, CancellationToken cancellationToken) =>
        response.StatusCode == HttpStatusCode.NotFound
            ? throw new MusicServerException(MusicServerError.UnsupportedServer, $"{baseUrl} has no Jellyfin API.")
            : ReadAsync<T>(response, baseUrl, cancellationToken);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, string baseUrl, CancellationToken cancellationToken)
    {
        EnsureSuccess(response, baseUrl);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false)
                   ?? throw new MusicServerException(MusicServerError.UnsupportedServer, $"{baseUrl} sent an empty answer.");
        }
        catch (JsonException ex)
        {
            throw new MusicServerException(MusicServerError.UnsupportedServer, $"{baseUrl} didn't answer in Jellyfin JSON.", ex);
        }
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string pathAndQuery, HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, _baseUrl + pathAndQuery) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("MediaBrowser", _authorizationParameters);
        return await MusicServerHttp.SendAsync(_httpClient, request, _baseUrl, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureSuccess(HttpResponseMessage response) => EnsureSuccess(response, _baseUrl);

    private static void EnsureSuccess(HttpResponseMessage response, string baseUrl)
    {
        if (response.IsSuccessStatusCode) return;
        var error = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => MusicServerError.SignInFailed,
            HttpStatusCode.NotFound => MusicServerError.NotFound,
            _ => MusicServerError.ServerError
        };
        throw new MusicServerException(error, $"{baseUrl} answered {(int)response.StatusCode}.");
    }

    private static string AuthorizationParameters(JellyfinClientIdentity identity) =>
        $"Client=\"{Quote(identity.Name)}\", Device=\"{Quote(identity.DeviceName)}\", " +
        $"DeviceId=\"{Quote(identity.DeviceId)}\", Version=\"{Quote(identity.Version)}\"";

    // Jellyfin reads the values URL-encoded, which also keeps quotes and commas from ending them early.
    private static string Quote(string value) => Uri.EscapeDataString(value);

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
            FileExtension = NullIfEmpty(Path.GetExtension(item.Path)?.TrimStart('.')),
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
            Composer = NullIfEmpty(string.Join(", ",
                item.People?.Where(person => person.Type == "Composer").Select(person => person.Name) ?? [])),
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
        NullIfEmpty(item.ProviderIds?.GetValueOrDefault(provider));

    private sealed record PublicSystemInfo(string? ServerName, string? Version);

    private sealed record AuthenticationResult(string AccessToken, JellyfinUser User);

    private sealed record JellyfinUser(string Id);

    private sealed record ItemsResult(List<JellyfinItem> Items);

    private sealed record NamedItem(string? Name);

    private sealed record Person(string? Name, string? Type);

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
        public List<Person>? People { get; init; }
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

internal sealed class JellyfinConnector(IAppInfoService appInfo) : MusicServerConnector
{
    public override bool SupportsApiKey => false;

    public override Task<ServerSignIn> SignInAsync(HttpClient httpClient, MusicServer server, string secret) =>
        JellyfinClient.SignInAsync(httpClient, server, secret, Identity(server));

    // A Jellyfin server is always added with the ID of the user it signed in as.
    public override IMusicServerClient CreateClient(HttpClient httpClient, MusicServer server, string secret) =>
        new JellyfinClient(httpClient, server.BaseUrl, server.UserId!, secret, Identity(server));

    private JellyfinClientIdentity Identity(MusicServer server) =>
        new(appInfo.GetAppName(), appInfo.GetAppVersion(), Environment.MachineName, server.FolderId.ToString("N"));
}

/// <summary>
///     How Nagi introduces itself to Jellyfin, which keeps one sign-in per <paramref name="DeviceId" />, so the ID must
///     stay the same for a server for as long as the server is in the library.
/// </summary>
internal sealed record JellyfinClientIdentity(string Name, string Version, string DeviceName, string DeviceId);
