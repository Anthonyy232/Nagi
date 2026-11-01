using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations.MusicServers;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class MusicServerServiceTests : IDisposable
{
    private const string BaseUrl = "https://music.example.com";
    private const string OkResponse = """{"subsonic-response":{"status":"ok","type":"navidrome","openSubsonic":true}}""";

    // Favorites are pushed from an event handler while a test reads the database, so contexts run at the same time.
    private readonly DbContextFactoryTestHelper _dbHelper = new(useFileDatabase: true);
    private readonly TestHttpMessageHandler _handler = new();
    private readonly ILibraryWriter _writer = Substitute.For<ILibraryWriter>();
    private readonly ILibraryScanner _scanner = Substitute.For<ILibraryScanner>();
    private readonly ICredentialLockerService _credentials = Substitute.For<ICredentialLockerService>();
    private readonly FakeTimeProvider _time = new();
    private readonly MusicServerService _service;

    public MusicServerServiceTests()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_handler, false));
        var appInfo = Substitute.For<IAppInfoService>();
        appInfo.GetAppName().Returns("Nagi");
        appInfo.GetAppVersion().Returns("2.6.0");
        _credentials.SaveCredential(default!, default!, default!).ReturnsForAnyArgs(true);
        _credentials.RetrieveCredential(Arg.Any<string>()).Returns(("alex", "secret"));
        _writer.AddMusicServerAsync(Arg.Any<MusicServer>(), Arg.Any<string>())
            .Returns(call => new Folder { Id = call.Arg<MusicServer>().FolderId, Name = call.Arg<string>() });

        _service = new MusicServerService(_dbHelper.ContextFactory, _writer, _scanner, _credentials, httpClientFactory,
            appInfo, Substitute.For<ILogger<MusicServerService>>(), _time);
    }

    public void Dispose()
    {
        _service.Dispose();
        _handler.Dispose();
        _dbHelper.Dispose();
    }

    [Fact]
    public async Task AddAsync_Jellyfin_KeepsTheAccessTokenInsteadOfThePassword()
    {
        _handler.SendAsyncFunc = JellyfinServer("token-1");

        var folder = await _service.AddAsync(MusicServerKind.Jellyfin, "https://jellyfin.example.com", "alex", "secret");

        _credentials.Received(1).SaveCredential($"Nagi/MusicServer/{folder.Id:N}", "alex", "token-1");
        await _writer.Received(1).AddMusicServerAsync(Arg.Is<MusicServer>(s => s.UserId == "user-1"), "Living Room");
    }

    [Fact]
    public async Task AddAsync_ServerAlreadyAdded_FailsWithoutSigningIn()
    {
        await SeedServerAsync();

        var add = () => _service.AddAsync(MusicServerKind.OpenSubsonic, "Music.Example.com", "ALEX", "secret");

        (await add.Should().ThrowAsync<MusicServerException>()).Which.Error.Should().Be(MusicServerError.AlreadyAdded);
        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task AddAsync_SignInThatCantBeSaved_DoesNotAddTheServer()
    {
        _handler.RespondSequence((HttpStatusCode.OK, OkResponse));
        _credentials.SaveCredential(default!, default!, default!).ReturnsForAnyArgs(false);

        var add = () => _service.AddAsync(MusicServerKind.OpenSubsonic, BaseUrl, "alex", "secret");

        (await add.Should().ThrowAsync<MusicServerException>()).Which.Error.Should().Be(MusicServerError.SignInNotSaved);
        await _writer.DidNotReceiveWithAnyArgs().AddMusicServerAsync(default!, default!);
    }

    [Fact]
    public async Task AddAsync_AnotherServerOfTheSameKind_IsNamedAfterItsHost()
    {
        await SeedServerAsync();
        _handler.RespondSequence((HttpStatusCode.OK, OkResponse));

        await _service.AddAsync(MusicServerKind.OpenSubsonic, "https://other.example.com:4533", "alex", "secret");

        await _writer.Received(1).AddMusicServerAsync(Arg.Any<MusicServer>(), "Navidrome (other.example.com:4533)");
    }

    [Fact]
    public async Task SignInAsync_SavesTheNewSignInAndStreamsWithIt()
    {
        var (server, song) = await SeedServerAsync(kind: MusicServerKind.Jellyfin);
        (await _service.GetStreamUriAsync(song))!.Query.Should().Contain("ApiKey=secret");
        _handler.SendAsyncFunc = JellyfinServer("token-2");

        await _service.SignInAsync(server.FolderId, "new-password");

        _credentials.Received(1).SaveCredential($"Nagi/MusicServer/{server.FolderId:N}", "alex", "token-2");
        _credentials.RetrieveCredential(Arg.Any<string>()).Returns(("alex", "token-2"));
        (await _service.GetStreamUriAsync(song))!.Query.Should().Contain("ApiKey=token-2");
    }

    [Fact]
    public async Task SignInAsync_ApiKeyOfAnotherAccount_IsRefusedAndTheServerStaysWithItsAccount()
    {
        var (server, _) = await SeedServerAsync(usesApiKey: true);
        _handler.RespondSequence((HttpStatusCode.OK, OkResponse),
            (HttpStatusCode.OK, """{"subsonic-response":{"status":"ok","openSubsonic":true,"tokenInfo":{"username":"admin"}}}"""));

        var signIn = () => _service.SignInAsync(server.FolderId, "admin-key");

        (await signIn.Should().ThrowAsync<MusicServerException>()).Which.Error.Should().Be(MusicServerError.DifferentAccount);
        _credentials.DidNotReceiveWithAnyArgs().SaveCredential(default!, default!, default!);
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.MusicServers.SingleAsync()).UserName.Should().Be("alex");
    }

    [Fact]
    public async Task RemovingTheServersFolder_ForgetsItsSignIn()
    {
        var (server, _) = await SeedServerAsync();

        _scanner.LibraryContentChanged += Raise.EventWith(new LibraryContentChangedEventArgs(LibraryChangeType.FolderRemoved, server.FolderId));

        _credentials.Received(1).RemoveCredential($"Nagi/MusicServer/{server.FolderId:N}");
    }

    [Fact]
    public async Task RemoveAsync_Jellyfin_SignsOutWithTheSavedSignIn()
    {
        var (server, _) = await SeedServerAsync(kind: MusicServerKind.Jellyfin);
        _writer.RemoveFolderAsync(server.FolderId).Returns(true);
        var signedOut = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SendAsyncFunc = (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/Sessions/Logout") signedOut.TrySetResult(request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        };

        (await _service.RemoveAsync(server.FolderId)).Should().BeTrue();

        (await signedOut.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Contain("Token=\"secret\"");
    }

    [Fact]
    public async Task RemoveAsync_ServerThatCouldntBeRemoved_StaysSignedIn()
    {
        var (server, _) = await SeedServerAsync(kind: MusicServerKind.Jellyfin);
        _writer.RemoveFolderAsync(server.FolderId).Returns(false);

        (await _service.RemoveAsync(server.FolderId)).Should().BeFalse();

        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SyncAsync_SendsFavoritesAndPlaysBeforeReadingTheServer()
    {
        var (server, song) = await SeedServerAsync(lovedPending: true);
        await SeedListensAsync(song, (new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), true));
        _handler.RespondSequence((HttpStatusCode.OK, OkResponse), (HttpStatusCode.OK, OkResponse));
        (bool Pending, bool Submitted)? whenSyncing = null;
        _scanner.SyncServerFolderAsync(server.FolderId, Arg.Any<IMusicServerClient>(), Arg.Any<IProgress<ScanProgress>?>(),
            Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await using var context = _dbHelper.ContextFactory.CreateDbContext();
            whenSyncing = ((await context.Songs.SingleAsync()).IsLovedSyncPending,
                (await context.ListenHistory.SingleAsync()).IsSubmittedToServer);
            return true;
        });

        await _service.SyncAsync(server.FolderId);

        whenSyncing.Should().Be((false, true), "the sync replaces the favorite and play count with the server's");
    }

    [Fact]
    public async Task StartPeriodicSync_SyncsAtOnceThenEveryHalfHour()
    {
        var (server, _) = await SeedServerAsync();
        var synced = new SemaphoreSlim(0);
        _scanner.SyncServerFolderAsync(server.FolderId, Arg.Any<IMusicServerClient>(), Arg.Any<IProgress<ScanProgress>?>(),
            Arg.Any<CancellationToken>()).Returns(_ =>
        {
            synced.Release();
            return Task.FromResult(false);
        });

        _service.StartPeriodicSync();
        (await synced.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        _time.Advance(TimeSpan.FromMinutes(29));
        (await synced.WaitAsync(TimeSpan.FromMilliseconds(100))).Should().BeFalse();
        _time.Advance(TimeSpan.FromMinutes(1));
        (await synced.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    [Fact]
    public async Task GetLyricsAsync_ServerThatCantGiveLyrics_HasNone()
    {
        var (_, song) = await SeedServerAsync();
        _handler.RespondSequence((HttpStatusCode.NotFound, string.Empty));

        (await _service.GetLyricsAsync(song)).Should().BeNull();
    }

    [Fact]
    public async Task FavoriteChangedInNagi_IsSentToTheServer()
    {
        await SeedServerAsync(lovedPending: true);
        _handler.RespondSequence((HttpStatusCode.OK, OkResponse));

        _scanner.LibraryContentChanged += Raise.EventWith(new LibraryContentChangedEventArgs(LibraryChangeType.SongsUpdated));

        (await WaitUntilAsync(async () => !await IsLovedPendingAsync())).Should().BeTrue();
        _handler.Requests.Single().RequestUri!.AbsolutePath.Should().Be("/rest/star.view");
    }

    [Fact]
    public async Task FavoriteChangedAgainWhileItWasSent_StaysPending()
    {
        var (server, _) = await SeedServerAsync(lovedPending: true);
        _handler.SendAsyncFunc = async (_, _) =>
        {
            // The user takes the favorite back while the server is being told about it.
            await using (var context = _dbHelper.ContextFactory.CreateDbContext())
                await context.Songs.ExecuteUpdateAsync(setters => setters.SetProperty(s => s.IsLoved, false));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OkResponse, Encoding.UTF8, "application/json") };
        };

        await _service.SyncAsync(server.FolderId);

        (await IsLovedPendingAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task ReportPlaysAsync_SendsEligiblePlaysInOrderAndStopsAtAFailure()
    {
        var (server, song) = await SeedServerAsync();
        var first = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        await SeedListensAsync(song, (first.AddHours(-1), false), (first.AddHours(1), true), (first, true), (first.AddHours(2), true));
        _handler.RespondSequence((HttpStatusCode.OK, OkResponse), (HttpStatusCode.InternalServerError, ""));

        await _service.ReportPlaysAsync(server.FolderId);

        HttpUtility.ParseQueryString(_handler.Requests[0].RequestUri!.Query)["time"]
            .Should().Be(new DateTimeOffset(first).ToUnixTimeMilliseconds().ToString());
        _handler.Requests.Should().HaveCount(2, "a failed play holds back the later ones, so plays reach the server in order");
        (await SubmittedFlagsAsync()).Should().Equal(false, true, false, false);
    }

    [Fact]
    public async Task ReportPlaysAsync_PlayOfASongGoneFromTheServer_IsDroppedSoTheNextOnesAreSent()
    {
        var (server, song) = await SeedServerAsync();
        var first = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        await SeedListensAsync(song, (first, true), (first.AddHours(1), true));
        _handler.RespondSequence(
            (HttpStatusCode.OK, """{"subsonic-response":{"status":"failed","error":{"code":70,"message":"Song not found"}}}"""),
            (HttpStatusCode.OK, OkResponse));

        await _service.ReportPlaysAsync(server.FolderId);

        _handler.Requests.Should().HaveCount(2);
        (await SubmittedFlagsAsync()).Should().Equal(true, true);
    }

    [Fact]
    public async Task ReportPlaysAsync_CancelledHalfway_KeepsTheReportedPlayMarked()
    {
        var (server, song) = await SeedServerAsync();
        var first = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        await SeedListensAsync(song, (first, true), (first.AddHours(1), true));
        using var cancellation = new CancellationTokenSource();
        _handler.SendAsyncFunc = (_, _) =>
        {
            if (_handler.Requests.Count == 2) cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(OkResponse, Encoding.UTF8, "application/json") });
        };

        var report = () => _service.ReportPlaysAsync(server.FolderId, cancellation.Token);

        await report.Should().ThrowAsync<OperationCanceledException>();
        (await SubmittedFlagsAsync()).Should().Equal(true, false);
    }

    private async Task<(MusicServer Server, Song Song)> SeedServerAsync(bool lovedPending = false,
        MusicServerKind kind = MusicServerKind.OpenSubsonic, bool usesApiKey = false)
    {
        var folder = new Folder { Name = "Navidrome", Path = "nagi-server:seed" };
        var server = new MusicServer
        {
            Kind = kind,
            BaseUrl = BaseUrl,
            UserName = "alex",
            UserId = kind == MusicServerKind.Jellyfin ? "user-1" : null,
            UsesApiKey = usesApiKey,
            Folder = folder
        };
        var song = new Song
        {
            Title = "Breakwater",
            FilePath = @"nagi-server:seed\s1",
            DirectoryPath = folder.Path,
            Folder = folder,
            RemoteId = "s1",
            IsLoved = lovedPending,
            IsLovedSyncPending = lovedPending
        };
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        context.MusicServers.Add(server);
        context.Songs.Add(song);
        await context.SaveChangesAsync();
        return (server, song);
    }

    private async Task SeedListensAsync(Song song, params (DateTime At, bool Eligible)[] listens)
    {
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        context.ListenHistory.AddRange(listens.Select(listen => new ListenHistory
        {
            SongId = song.Id,
            ListenTimestampUtc = listen.At,
            IsEligibleForScrobbling = listen.Eligible
        }));
        await context.SaveChangesAsync();
    }

    private async Task<List<bool>> SubmittedFlagsAsync()
    {
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        return await context.ListenHistory.OrderBy(lh => lh.ListenTimestampUtc).Select(lh => lh.IsSubmittedToServer).ToListAsync();
    }

    private async Task<bool> IsLovedPendingAsync()
    {
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        return (await context.Songs.SingleAsync()).IsLovedSyncPending;
    }

    // The push runs from an event handler, so there is no task to await.
    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (await condition()) return true;
            await Task.Delay(10);
        }

        return false;
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> JellyfinServer(string accessToken) =>
        (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/System/Info/Public" => Json(new { ServerName = "Living Room", Version = "12.0.0" }),
            "/Users/AuthenticateByName" => Json(new { AccessToken = accessToken, User = new { Id = "user-1" } }),
            "/UserViews" => Json(new { Items = new[] { new { Id = "lib", CollectionType = "music" } } }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
