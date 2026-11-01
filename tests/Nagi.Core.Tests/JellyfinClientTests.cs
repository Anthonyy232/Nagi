using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using FluentAssertions;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations.MusicServers;
using Nagi.Core.Tests.Utils;
using Xunit;

namespace Nagi.Core.Tests;

public class JellyfinClientTests : IDisposable
{
    private const string BaseUrl = "https://jellyfin.example.com";
    private static readonly JellyfinClientIdentity Identity = new("Nagi", "2.6.0", "Desk PC", "device-1");
    private readonly TestHttpMessageHandler _handler = new();
    private readonly HttpClient _httpClient;

    public JellyfinClientTests()
    {
        _httpClient = new HttpClient(_handler);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    [Theory]
    [InlineData("10.10.7", false)]
    [InlineData("10.11.0", true)]
    [InlineData("12.1.0-dev", true)]
    public async Task SignInAsync_SupportsJellyfin10_11AndLater(string version, bool supported)
    {
        RouteServer(version, hasMusicLibrary: true);

        var signIn = () => JellyfinClient.SignInAsync(_httpClient, BaseUrl, "alex", "secret", Identity);

        if (supported)
            await signIn.Should().NotThrowAsync();
        else
            (await signIn.Should().ThrowAsync<MusicServerException>()).Which.Error.Should().Be(MusicServerError.ServerTooOld);
    }

    [Fact]
    public async Task SignInAsync_SignsEveryRequestWithTheCurrentAuthorizationHeader()
    {
        RouteServer("12.0.0", hasMusicLibrary: true);

        var signIn = await JellyfinClient.SignInAsync(_httpClient, BaseUrl, "alex", "secret", Identity);

        signIn.Should().Be(new JellyfinSignIn("Living Room", "user-1", "token-1"));
        _handler.Requests.Should().OnlyContain(r => !r.Headers.Contains("X-Emby-Authorization") && !r.Headers.Contains("X-Emby-Token"));
        var headers = _handler.Requests.Select(r => r.Headers.GetValues("Authorization").Single()).ToList();
        headers.Should().OnlyContain(h => h.StartsWith("MediaBrowser ") && h.Contains("DeviceId=\"device-1\""));
        headers.Last().Should().Contain("Token=\"token-1\"", "requests after signing in carry the access token");
    }

    [Fact]
    public async Task SignInAsync_AccountWithoutAMusicLibrary_Fails()
    {
        RouteServer("12.0.0", hasMusicLibrary: false);

        var signIn = () => JellyfinClient.SignInAsync(_httpClient, BaseUrl, "alex", "secret", Identity);

        (await signIn.Should().ThrowAsync<MusicServerException>()).Which.Error.Should().Be(MusicServerError.NoMusicLibrary);
    }

    [Fact]
    public void GetStreamUri_PassesTheTokenAsApiKeyNotTheLegacyParameter()
    {
        var uri = Client().GetStreamUri("song-1").ToString();

        uri.Should().Be($"{BaseUrl}/Audio/song-1/stream?static=true&ApiKey=token-1");
    }

    [Fact]
    public async Task GetSongsAsync_ReadsEveryMusicLibraryAndMapsAudioItems()
    {
        _handler.SendAsyncFunc = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/UserViews" => Json(new
            {
                Items = new object[]
                {
                    new { Id = "lib-music", CollectionType = "music" },
                    new { Id = "lib-movies", CollectionType = "movies" },
                    new { Id = "lib-live", CollectionType = "music" }
                }
            }),
            "/Items" when request.RequestUri!.Query.Contains("parentId=lib-live") => Json(new { Items = Array.Empty<object>() }),
            "/Items" => Json(new
            {
                Items = new object[]
                {
                    new
                    {
                        Id = "song-1",
                        Name = "Copper Skies",
                        Path = "/music/Selene Grey/Copper Skies/02 Copper Skies.opus",
                        Album = "Copper Skies",
                        AlbumId = "album-1",
                        AlbumPrimaryImageTag = "tag",
                        AlbumArtists = new[] { new { Name = "Selene Grey", Id = "ar-1" } },
                        Artists = new[] { "Selene Grey", "Guest" },
                        Genres = new[] { "Folk" },
                        RunTimeTicks = TimeSpan.FromMinutes(3).Ticks,
                        IndexNumber = 2,
                        ParentIndexNumber = 1,
                        ProductionYear = 2021,
                        NormalizationGain = -6.2,
                        DateCreated = "2025-02-03T04:05:06.0000000Z",
                        ProviderIds = new Dictionary<string, string> { ["MusicBrainzRecording"] = "rec-1", ["MusicBrainzAlbum"] = "rel-1" },
                        MediaSources = new[]
                        {
                            new { Bitrate = 920000, MediaStreams = new[] { new { Type = "Audio", SampleRate = 48000, Channels = 2 } } }
                        },
                        UserData = new { IsFavorite = true, PlayCount = 4, LastPlayedDate = "2026-10-20T08:00:00Z" }
                    }
                }
            }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var songs = new List<ServerSongMetadata>();
        await foreach (var page in Client().GetSongsAsync(CancellationToken.None)) songs.AddRange(page);

        _handler.Requests.Where(r => r.RequestUri!.AbsolutePath == "/Items")
            .Select(r => HttpUtility.ParseQueryString(r.RequestUri!.Query)["parentId"]).Should().Equal("lib-music", "lib-live");
        var song = songs.Should().ContainSingle().Subject;
        song.RemoteId.Should().Be("song-1");
        song.FileExtension.Should().Be("opus");
        song.Artists.Should().Equal("Selene Grey", "Guest");
        song.AlbumArtists.Should().Equal("Selene Grey");
        song.Duration.Should().Be(TimeSpan.FromMinutes(3));
        song.TrackNumber.Should().Be(2);
        song.Bitrate.Should().Be(920);
        song.SampleRate.Should().Be(48000);
        song.ReplayGainTrackGain.Should().Be(-6.2);
        song.MusicBrainzTrackId.Should().Be("rec-1");
        song.MusicBrainzReleaseId.Should().Be("rel-1");
        song.CoverArtId.Should().Be("album-1", "songs share their album's cover");
        song.IsFavorite.Should().BeTrue();
        song.PlayCount.Should().Be(4);
        song.AddedUtc.Should().Be(new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc));
    }

    [Fact]
    public async Task SetFavoriteAsync_SkipsSongsDeletedFromTheServer()
    {
        _handler.SendAsyncFunc = (request, _) => Task.FromResult(new HttpResponseMessage(
            request.RequestUri!.AbsolutePath.EndsWith("gone") ? HttpStatusCode.NotFound : HttpStatusCode.OK));

        await Client().SetFavoriteAsync(["gone", "song-1"], false, CancellationToken.None);

        _handler.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, MusicServerError.SignInFailed)]
    [InlineData(HttpStatusCode.NotFound, MusicServerError.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, MusicServerError.ServerError)]
    public async Task ReportPlayedAsync_SaysWhyTheServerRefused(HttpStatusCode status, MusicServerError expected)
    {
        _handler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(status));

        var report = () => Client().ReportPlayedAsync("song-1", new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc),
            CancellationToken.None);

        (await report.Should().ThrowAsync<MusicServerException>()).Which.Error.Should().Be(expected);
    }

    [Fact]
    public async Task GetLyricsAsync_TimedLyrics_BecomeLrcFromTheirTicks()
    {
        _handler.SendAsyncFunc = (_, _) => Task.FromResult(Json(new
        {
            Lyrics = new[] { new { Text = "Salt on the window", Start = 10_000_000L }, new { Text = "Ropes in the harbor", Start = 55_000_000L } }
        }));

        var lyrics = await Client().GetLyricsAsync("song-1", CancellationToken.None);

        lyrics!.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Should().Equal("[00:01.000]Salt on the window", "[00:05.500]Ropes in the harbor");
    }

    [Fact]
    public async Task GetLyricsAsync_UntimedLyrics_StayPlainText()
    {
        _handler.SendAsyncFunc = (_, _) => Task.FromResult(Json(new
        {
            Lyrics = new[] { new { Text = "Gulls over the pier" }, new { Text = "Calling the morning in" }, new { Text = "" } }
        }));

        var lyrics = await Client().GetLyricsAsync("song-1", CancellationToken.None);

        lyrics.Should().Be("Gulls over the pier\nCalling the morning in");
    }

    [Fact]
    public async Task GetLyricsAsync_SongWithoutLyrics_ReturnsNull()
    {
        _handler.SendAsyncFunc = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        (await Client().GetLyricsAsync("song-1", CancellationToken.None)).Should().BeNull();
    }

    private JellyfinClient Client() => new(_httpClient, BaseUrl, "user-1", "token-1", Identity);

    private void RouteServer(string version, bool hasMusicLibrary)
    {
        _handler.SendAsyncFunc = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/System/Info/Public" => Json(new { ServerName = "Living Room", Version = version, Id = "server-1" }),
            "/Users/AuthenticateByName" => Json(new { AccessToken = "token-1", User = new { Id = "user-1", Name = "alex" } }),
            "/UserViews" => Json(new { Items = new[] { new { Id = "lib", CollectionType = hasMusicLibrary ? "music" : "movies" } } }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
    }

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
