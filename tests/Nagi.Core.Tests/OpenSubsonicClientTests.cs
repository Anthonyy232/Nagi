using System.Net;
using System.Security.Cryptography;
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

public class OpenSubsonicClientTests : IDisposable
{
    private const string BaseUrl = "https://music.example.com";
    private readonly TestHttpMessageHandler _handler = new();
    private readonly HttpClient _httpClient;

    public OpenSubsonicClientTests()
    {
        _httpClient = new HttpClient(_handler);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    [Theory]
    [InlineData("music.example.com", "https://music.example.com")]
    [InlineData("http://192.168.1.20:4533/", "http://192.168.1.20:4533")]
    [InlineData("HTTPS://Music.Example.com/navidrome/rest", "https://music.example.com/navidrome")]
    public void NormalizeAddress_MeansHttpsUnlessToldAndDropsThePastedApiPath(string address, string expected)
    {
        OpenSubsonicClient.NormalizeAddress(address).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://alex:secret@music.example.com")]
    [InlineData("https://music.example.com/?u=alex")]
    [InlineData("ftp://music.example.com")]
    [InlineData("not a server")]
    public void NormalizeAddress_RejectsAddressesThatCantBeAServer(string address)
    {
        var normalize = () => OpenSubsonicClient.NormalizeAddress(address);

        normalize.Should().Throw<MusicServerException>().Which.Error.Should().Be(MusicServerError.InvalidAddress);
    }

    [Fact]
    public async Task ConnectAsync_SignsInWithASaltedTokenRatherThanThePassword()
    {
        _handler.RespondSequence((HttpStatusCode.OK, Response(new { status = "ok", type = "navidrome", openSubsonic = true })));

        var (name, userName) = await OpenSubsonicClient.ConnectAsync(_httpClient, BaseUrl, "alex", "secret", false, "Nagi");

        name.Should().Be("Navidrome");
        userName.Should().Be("alex");
        var query = HttpUtility.ParseQueryString(_handler.Requests.Single().RequestUri!.Query);
        query["p"].Should().BeNull();
        query["apiKey"].Should().BeNull();
        query["u"].Should().Be("alex");
        query["t"].Should().Be(Md5Hex("secret" + query["s"]));
    }

    [Fact]
    public async Task ConnectAsync_WithAnApiKey_SendsOnlyTheKeyAndLearnsTheUserName()
    {
        _handler.RespondSequence(
            (HttpStatusCode.OK, Response(new { status = "ok", type = "lms", openSubsonic = true })),
            (HttpStatusCode.OK, Response(new { status = "ok", openSubsonic = true, tokenInfo = new { username = "alex" } })));

        var (_, userName) = await OpenSubsonicClient.ConnectAsync(_httpClient, BaseUrl, "", "key-123", true, "Nagi");

        userName.Should().Be("alex");
        _handler.Requests.Select(r => HttpUtility.ParseQueryString(r.RequestUri!.Query)).Should().OnlyContain(q =>
            q["apiKey"] == "key-123" && q["u"] == null && q["t"] == null && q["s"] == null && q["p"] == null);
    }

    [Theory]
    [InlineData(HttpRequestError.SecureConnectionError, MusicServerError.SecureConnectionFailed)]
    [InlineData(HttpRequestError.ConnectionError, MusicServerError.Unreachable)]
    public async Task ConnectAsync_FailedHttpsHandshake_IsToldApartFromAnUnreachableServer(HttpRequestError failure,
        MusicServerError expected)
    {
        _handler.SendAsyncFunc = (_, _) => throw new HttpRequestException(failure, "The connection failed.");

        var connect = () => OpenSubsonicClient.ConnectAsync(_httpClient, BaseUrl, "alex", "secret", false, "Nagi");

        (await connect.Should().ThrowAsync<MusicServerException>()).Which.Error.Should().Be(expected);
    }

    [Fact]
    public async Task ReportPlayedAsync_TimingOut_IsUnreachableUnlikeCancelling()
    {
        _handler.SendAsyncFunc = (_, _) => throw new TaskCanceledException();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var timedOut = () => Client().ReportPlayedAsync("s1", DateTime.UtcNow, CancellationToken.None);
        var cancelled = () => Client().ReportPlayedAsync("s1", DateTime.UtcNow, cancellation.Token);

        (await timedOut.Should().ThrowAsync<MusicServerException>()).Which.Error.Should().Be(MusicServerError.Unreachable);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ConnectAsync_RejectsASubsonicServerWithoutOpenSubsonic()
    {
        _handler.RespondSequence((HttpStatusCode.OK, Response(new { status = "ok", type = "subsonic", openSubsonic = false })));

        var connect = () => OpenSubsonicClient.ConnectAsync(_httpClient, BaseUrl, "alex", "secret", false, "Nagi");

        (await connect.Should().ThrowAsync<MusicServerException>()).Which.Error.Should().Be(MusicServerError.UnsupportedServer);
    }

    [Theory]
    [InlineData(40, MusicServerError.SignInFailed)]
    [InlineData(44, MusicServerError.SignInFailed)]
    [InlineData(41, MusicServerError.SignInMethodUnsupported)]
    [InlineData(42, MusicServerError.SignInMethodUnsupported)]
    public async Task ConnectAsync_TellsAWrongSignInFromOneTheServerDoesNotTake(int code, MusicServerError expected)
    {
        _handler.RespondSequence((HttpStatusCode.OK, Response(new
        {
            status = "failed",
            openSubsonic = true,
            error = new { code, message = "Rejected" }
        })));

        var connect = () => OpenSubsonicClient.ConnectAsync(_httpClient, BaseUrl, "alex", "secret", false, "Nagi");

        (await connect.Should().ThrowAsync<MusicServerException>()).Which.Error.Should().Be(expected);
    }

    [Fact]
    public async Task GetSongsAsync_PagesThroughEverySongAndMapsOpenSubsonicFields()
    {
        var firstPage = Enumerable.Range(0, 500).Select(i => (object)new { id = $"s{i}", title = $"Song {i}" }).ToList();
        firstPage[0] = new
        {
            id = "s0",
            title = "Glass Harbor",
            suffix = "m4a",
            album = "Low Tide",
            artist = "A & B",
            artists = new[] { new { id = "ar1", name = "A" }, new { id = "ar2", name = "B" } },
            albumArtists = new[] { new { id = "ar1", name = "A" } },
            genres = new[] { new { name = "Dream Pop" }, new { name = "Shoegaze" } },
            track = 3,
            discNumber = 1,
            year = 2021,
            duration = 245,
            displayComposer = "Ada Brook",
            contributors = new[]
            {
                new { role = "composer", artist = new { id = "ar3", name = "Ada Brook" } },
                new { role = "conductor", artist = new { id = "ar4", name = "Lena Hart" } }
            },
            groupings = new[] { "Harbor Suite" },
            replayGain = new { trackGain = -7.5, trackPeak = 0.98 },
            coverArt = "al-1",
            starred = "2026-05-01T10:00:00Z",
            playCount = 12,
            played = "2026-10-01T09:30:00Z",
            created = "2024-01-02T03:04:05Z"
        };
        _handler.RespondSequence(
            (HttpStatusCode.OK, Response(new { status = "ok", openSubsonic = true, searchResult3 = new { song = firstPage } })),
            (HttpStatusCode.OK, Response(new
            {
                status = "ok",
                openSubsonic = true,
                searchResult3 = new
                {
                    song = new object[]
                    {
                        new { id = "legacy", title = "Old Tags", artist = "Solo", artists = Array.Empty<object>() },
                        new { id = "clip", title = "Video", isVideo = true }
                    }
                }
            })));

        var songs = new List<ServerSongMetadata>();
        await foreach (var page in Client().GetSongsAsync(CancellationToken.None)) songs.AddRange(page);

        songs.Should().HaveCount(501, "videos are not songs");
        _handler.Requests.Select(r => HttpUtility.ParseQueryString(r.RequestUri!.Query)["songOffset"]).Should().Equal("0", "500");

        var song = songs[0];
        song.RemoteId.Should().Be("s0");
        song.FileExtension.Should().Be("m4a");
        song.Artists.Should().Equal("A", "B");
        song.AlbumArtists.Should().Equal("A");
        song.Genres.Should().Equal("Dream Pop", "Shoegaze");
        song.Composer.Should().Be("Ada Brook");
        song.Conductor.Should().Be("Lena Hart");
        song.Grouping.Should().Be("Harbor Suite");
        song.Duration.Should().Be(TimeSpan.FromSeconds(245));
        song.ReplayGainTrackGain.Should().Be(-7.5);
        song.ReplayGainTrackPeak.Should().Be(0.98);
        song.CoverArtId.Should().Be("al-1");
        song.IsFavorite.Should().BeTrue();
        song.PlayCount.Should().Be(12);
        song.LastPlayedUtc.Should().Be(new DateTime(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc));
        song.AddedUtc.Should().Be(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        songs[1].IsFavorite.Should().BeFalse();
        songs.Single(s => s.RemoteId == "legacy").Artists.Should().Equal("Solo");
    }

    [Fact]
    public async Task GetCoverArtAsync_MissingImage_ReturnsNull()
    {
        _handler.RespondSequence((HttpStatusCode.OK, Response(new
        {
            status = "failed",
            openSubsonic = true,
            error = new { code = 70, message = "Artwork not found" }
        })));

        var image = await Client().GetCoverArtAsync("al-404", CancellationToken.None);

        image.Should().BeNull();
    }

    [Fact]
    public async Task GetCoverArtAsync_RejectedSignIn_FailsInsteadOfReportingNoImage()
    {
        _handler.RespondSequence((HttpStatusCode.OK, Response(new
        {
            status = "failed",
            openSubsonic = true,
            error = new { code = 40, message = "Wrong username or password" }
        })));

        var download = () => Client().GetCoverArtAsync("al-1", CancellationToken.None);

        (await download.Should().ThrowAsync<MusicServerException>()).Which.Error.Should().Be(MusicServerError.SignInFailed);
    }

    [Fact]
    public async Task GetLyricsAsync_PrefersTimedLyricsAndShiftsThemByTheirOffset()
    {
        _handler.RespondSequence((HttpStatusCode.OK, Response(new
        {
            status = "ok",
            openSubsonic = true,
            lyricsList = new
            {
                structuredLyrics = new object[]
                {
                    new { synced = false, line = new[] { new { value = "Salt on the window" } } },
                    new
                    {
                        synced = true,
                        offset = 500,
                        line = new[] { new { start = 1500, value = "Salt on the window" }, new { start = 6000, value = "Ropes in the harbor" } }
                    }
                }
            }
        })));

        var lyrics = await Client().GetLyricsAsync("s1", CancellationToken.None);

        lyrics!.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Should().Equal("[00:01.000]Salt on the window", "[00:05.500]Ropes in the harbor");
    }

    [Fact]
    public async Task GetLyricsAsync_SongWithoutLyrics_ReturnsNull()
    {
        _handler.RespondSequence((HttpStatusCode.OK, Response(new { status = "ok", openSubsonic = true, lyricsList = new { } })));

        (await Client().GetLyricsAsync("s1", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public void GetStreamUri_StreamsTheOriginalFileWithTokenSignIn()
    {
        var query = HttpUtility.ParseQueryString(Client().GetStreamUri("s1").Query);
        query["id"].Should().Be("s1");
        query["format"].Should().Be("raw");
        query["p"].Should().BeNull();
        query["t"].Should().Be(Md5Hex("secret" + query["s"]));
    }

    private OpenSubsonicClient Client() => new(_httpClient, BaseUrl, "alex", "secret", false, "Nagi");

    private static string Response(object body) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["subsonic-response"] = body });

    private static string Md5Hex(string value) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(value)));
}
