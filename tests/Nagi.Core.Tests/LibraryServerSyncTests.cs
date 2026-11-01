using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public partial class LibraryServiceTests
{
    [Fact]
    public async Task SyncServerFolder_AddsTheServersSongsUnderItsRoot()
    {
        var folder = await AddServerAsync();
        var added = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var played = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var client = ServerWith(ServerSong("s1", "Breakwater", isFavorite: true, playCount: 7, lastPlayed: played, added: added));

        var changed = await _libraryService.SyncServerFolderAsync(folder.Id, client);

        changed.Should().BeTrue();
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var song = await context.Songs.Include(s => s.Album).Include(s => s.SongArtists).ThenInclude(sa => sa.Artist).SingleAsync();
        song.RemoteId.Should().Be("s1");
        song.FolderId.Should().Be(folder.Id);
        song.DirectoryPath.Should().Be(folder.Path, "folder browsing lists a server's songs at its root");
        song.Album!.Title.Should().Be("Saltwater Hymns");
        song.SongArtists.Single().Artist.Name.Should().Be("Harbor Lights");
        song.IsLoved.Should().BeTrue();
        song.PlayCount.Should().Be(7);
        song.LastPlayedDate.Should().Be(played);
        song.DateAddedToLibrary.Should().Be(added);
        song.FileCreatedDate.Should().Be(added, "date created sorts and smart playlists need a date");
    }

    [Theory]
    [InlineData("s1", "FLAC", @"\s1.flac")]
    [InlineData("s1.flac", "mp3", @"\s1%2Eflac.mp3")]
    [InlineData("s1.flac", null, @"\s1%2Eflac")]
    public async Task SyncServerFolder_EndsASongsKeyInItsFilesExtensionAndNoOther(string id, string? extension, string expected)
    {
        var folder = await AddServerAsync();

        await _libraryService.SyncServerFolderAsync(folder.Id, ServerWith(ServerSong(id, "Breakwater", fileExtension: extension)));

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.Songs.SingleAsync()).FilePath.Should().Be(folder.Path + expected);
    }

    [Fact]
    public async Task SyncServerFolder_UpdatesChangedSongsAndRemovesTheOnesNoLongerListed()
    {
        var folder = await AddServerAsync();
        await _libraryService.SyncServerFolderAsync(folder.Id, ServerWith(ServerSong("s1", "Old title"), ServerSong("s2", "Gone")));

        await _libraryService.SyncServerFolderAsync(folder.Id, ServerWith(ServerSong("s1", "New title")));

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var song = await context.Songs.SingleAsync();
        song.RemoteId.Should().Be("s1");
        song.Title.Should().Be("New title");
    }

    [Fact]
    public async Task SyncServerFolder_SongsTheServerDescribesTheSameWay_ChangeNothing()
    {
        var folder = await AddServerAsync();
        await _libraryService.SyncServerFolderAsync(folder.Id, ServerWith(ServerSong("s1", "Breakwater")));
        var events = 0;
        _libraryService.LibraryContentChanged += (_, _) => events++;

        var changed = await _libraryService.SyncServerFolderAsync(folder.Id, ServerWith(ServerSong("s1", "Breakwater")));

        changed.Should().BeFalse();
        events.Should().Be(0);
    }

    [Fact]
    public async Task SyncServerFolder_SongWithoutATitleAlbumOrArtist_GetsTheDefaultsTagsGet()
    {
        var folder = await AddServerAsync();
        var song = ServerSong("s1", " ");
        song.Album = null;
        song.Artists = [];

        await _libraryService.SyncServerFolderAsync(folder.Id, ServerWith(song));

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var stored = await context.Songs.Include(s => s.Album).Include(s => s.SongArtists).ThenInclude(sa => sa.Artist)
            .SingleAsync();
        stored.Title.Should().Be(Song.UnknownTitle);
        stored.Album!.Title.Should().Be(Album.UnknownAlbumName);
        stored.SongArtists.Single().Artist.Name.Should().Be(Artist.UnknownArtistName, "the song belongs on that artist's page");
    }

    [Fact]
    public async Task SyncServerFolder_AfterTheSplitCharactersChange_SplitsTheServersNamesLikeTags()
    {
        var folder = await AddServerAsync();
        var song = ServerSong("s1", "Breakwater");
        song.Artists = ["Mareen Holt; Kiko Mori", "Kiko Mori"];
        song.Genres = ["Folk/Ambient"];
        var client = ServerWith(song);
        await _libraryService.SyncServerFolderAsync(folder.Id, client);

        _settingsService.GetArtistSplitCharactersAsync().Returns(";");
        _settingsService.GetGenreSplitCharactersAsync().Returns("/");
        await _libraryService.SyncServerFolderAsync(folder.Id, client);

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var stored = await context.Songs.Include(s => s.SongArtists).ThenInclude(sa => sa.Artist).Include(s => s.Genres)
            .SingleAsync();
        stored.SongArtists.OrderBy(sa => sa.Order).Select(sa => sa.Artist.Name).Should().Equal("Mareen Holt", "Kiko Mori");
        stored.Genres.Select(g => g.Name).Should().BeEquivalentTo("Folk", "Ambient");
    }

    [Fact]
    public async Task SyncServerFolder_KeepsAFavoriteTheServerHasNotHeardAbout()
    {
        var folder = await AddServerAsync();
        await _libraryService.SyncServerFolderAsync(folder.Id, ServerWith(ServerSong("s1", "Breakwater")));
        Guid songId;
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
            songId = await context.Songs.Select(s => s.Id).SingleAsync();

        await _libraryService.SetSongsLovedAsync([songId], true);
        await _libraryService.SyncServerFolderAsync(folder.Id, ServerWith(ServerSong("s1", "Breakwater", playCount: 3)));

        await using var check = _dbHelper.ContextFactory.CreateDbContext();
        var song = await check.Songs.SingleAsync();
        song.PlayCount.Should().Be(3, "the server's details were imported");
        song.IsLoved.Should().BeTrue();
        song.IsLovedSyncPending.Should().BeTrue();
    }

    [Fact]
    public async Task SyncServerFolder_AfterAFavoriteChangedHere_TakesTheServersValueOnceItIsSent()
    {
        var folder = await AddServerAsync();
        await _libraryService.SyncServerFolderAsync(folder.Id, ServerWith(ServerSong("s1", "Breakwater", isFavorite: true)));
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
            await _libraryService.SetSongsLovedAsync([await context.Songs.Select(s => s.Id).SingleAsync()], false);
        // The server was told, then the favorite came back from another app, so the server lists the song as before.
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
            await context.Songs.ExecuteUpdateAsync(setters => setters.SetProperty(s => s.IsLovedSyncPending, false));

        await _libraryService.SyncServerFolderAsync(folder.Id, ServerWith(ServerSong("s1", "Breakwater", isFavorite: true)));

        await using var check = _dbHelper.ContextFactory.CreateDbContext();
        (await check.Songs.SingleAsync()).IsLoved.Should().BeTrue();
    }

    [Fact]
    public async Task SetSongsLoved_MarksOnlyServerSongsForTheServer()
    {
        var folder = await AddServerAsync();
        await _libraryService.SyncServerFolderAsync(folder.Id, ServerWith(ServerSong("s1", "Remote")));
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var local = new Folder { Name = "Music", Path = @"C:\Music" };
            context.Songs.Add(new Song { Title = "Local", FilePath = @"C:\Music\a.flac", DirectoryPath = local.Path, Folder = local });
            await context.SaveChangesAsync();
        }

        List<Guid> ids;
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
            ids = await context.Songs.Select(s => s.Id).ToListAsync();
        await _libraryService.SetSongsLovedAsync(ids, true);

        await using var check = _dbHelper.ContextFactory.CreateDbContext();
        var songs = await check.Songs.ToListAsync();
        songs.Should().OnlyContain(s => s.IsLoved);
        songs.Single(s => s.Title == "Remote").IsLovedSyncPending.Should().BeTrue();
        songs.Single(s => s.Title == "Local").IsLovedSyncPending.Should().BeFalse();
    }

    [Fact]
    public async Task SyncServerFolder_ServerThatListsNothing_KeepsItsSongs()
    {
        var folder = await AddServerAsync();
        await _libraryService.SyncServerFolderAsync(folder.Id, ServerWith(ServerSong("s1", "Breakwater")));

        var changed = await _libraryService.SyncServerFolderAsync(folder.Id, ServerWith());

        changed.Should().BeFalse();
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.Songs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SyncServerFolder_ServerThatListsFarFewerSongs_KeepsThem()
    {
        var folder = await AddServerAsync();
        var songs = Enumerable.Range(0, 60).Select(i => ServerSong($"s{i}", $"Song {i}")).ToArray();
        await _libraryService.SyncServerFolderAsync(folder.Id, ServerWith(songs));

        await _libraryService.SyncServerFolderAsync(folder.Id, ServerWith(songs[0]));

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.Songs.CountAsync()).Should().Be(60, "a library offline on the server mustn't take playlists and history with it");
    }

    [Fact]
    public async Task SyncServerFolder_DownloadsEachCoverOnce_AndAgainOnceItIsGoneFromTheCache()
    {
        var folder = await AddServerAsync();
        var songs = new[] { ServerSong("s1", "One", coverArtId: "al-1"), ServerSong("s2", "Two", coverArtId: "al-1") };
        var client = ServerWith(songs);
        client.GetCoverArtAsync("al-1", Arg.Any<CancellationToken>()).Returns(new byte[] { 1, 2, 3 });
        _imageProcessor.SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>())
            .Returns((@"C:\cache\albumart\cover.jpg", "light", "dark"));
        _fileSystem.FileExists(@"C:\cache\albumart\cover.jpg").Returns(true);

        await _libraryService.SyncServerFolderAsync(folder.Id, client);
        await _libraryService.SyncServerFolderAsync(folder.Id, client);
        await client.Received(1).GetCoverArtAsync("al-1", Arg.Any<CancellationToken>());

        _fileSystem.FileExists(@"C:\cache\albumart\cover.jpg").Returns(false);
        await _libraryService.SyncServerFolderAsync(folder.Id, client);

        await client.Received(2).GetCoverArtAsync("al-1", Arg.Any<CancellationToken>());
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.Songs.ToListAsync()).Should().OnlyContain(s =>
            s.AlbumArtUriFromTrack == @"C:\cache\albumart\cover.jpg" && s.LightSwatchId == "light" && s.DarkSwatchId == "dark");
        (await context.Albums.SingleAsync()).CoverArtUri.Should().Be(@"C:\cache\albumart\cover.jpg");
    }

    [Fact]
    public async Task SyncServerFolder_ARetaggedSongGivesItsCoverToItsNewAlbum()
    {
        var folder = await AddServerAsync();
        var client = ServerWith(ServerSong("s1", "One", coverArtId: "al-1"));
        client.GetCoverArtAsync("al-1", Arg.Any<CancellationToken>()).Returns(new byte[] { 1 });
        _imageProcessor.SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>()).Returns((@"C:\cache\albumart\cover.jpg", "light", "dark"));
        _fileSystem.FileExists(@"C:\cache\albumart\cover.jpg").Returns(true);
        await _libraryService.SyncServerFolderAsync(folder.Id, client);
        var retagged = ServerWith(ServerSong("s1", "One", album: "Homeport", coverArtId: "al-1"));

        await _libraryService.SyncServerFolderAsync(folder.Id, retagged);

        await retagged.DidNotReceiveWithAnyArgs().GetCoverArtAsync(default!, default);
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var album = await context.Albums.SingleAsync();
        album.Title.Should().Be("Homeport", "the album the retag left empty is gone");
        album.CoverArtUri.Should().Be(@"C:\cache\albumart\cover.jpg");
    }

    [Fact]
    public async Task SyncServerFolder_CoverTheServerChanged_DownloadsAgainAndDeletesTheOldOne()
    {
        var folder = await AddServerAsync();
        var client = ServerWith(ServerSong("s1", "One", coverArtId: "mf-1"), ServerSong("s2", "Two", coverArtId: "mf-2"));
        var changed = ServerWith(ServerSong("s1", "One", coverArtId: "mf-1"), ServerSong("s2", "Two", coverArtId: "mf-2-new"));
        foreach (var server in new[] { client, changed })
            server.GetCoverArtAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Encoding.UTF8.GetBytes(call.Arg<string>()));
        _imageProcessor.SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>())
            .Returns(call => ($@"C:\cache\albumart\{Encoding.UTF8.GetString(call.Arg<byte[]>())}.jpg", "light", "dark"));
        _fileSystem.FileExists(Arg.Any<string>()).Returns(true);
        await _libraryService.SyncServerFolderAsync(folder.Id, client);

        await _libraryService.SyncServerFolderAsync(folder.Id, changed);

        await changed.DidNotReceive().GetCoverArtAsync("mf-1", Arg.Any<CancellationToken>());
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.Songs.SingleAsync(s => s.RemoteId == "s2")).AlbumArtUriFromTrack.Should().Be(@"C:\cache\albumart\mf-2-new.jpg");
        _fileSystem.Received(1).DeleteFile(@"C:\cache\albumart\mf-2.jpg");
    }

    [Fact]
    public async Task SyncServerFolder_WhileTheServerSyncs_WaitsForThatSyncThenSyncs()
    {
        var folder = await AddServerAsync();
        var listing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalled = Substitute.For<IMusicServerClient>();
        stalled.GetSongsAsync(Arg.Any<CancellationToken>()).Returns(call => Stalled(listing, call.Arg<CancellationToken>()));
        using var cancellation = new CancellationTokenSource();
        var first = _libraryService.SyncServerFolderAsync(folder.Id, stalled, cancellationToken: cancellation.Token);
        await listing.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = _libraryService.SyncServerFolderAsync(folder.Id, ServerWith(ServerSong("s1", "Breakwater")));
        second.IsCompleted.Should().BeFalse();
        await cancellation.CancelAsync();

        await FluentActions.Awaiting(() => first.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<OperationCanceledException>();
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
    }

    [Fact]
    public async Task RemoveFolder_OfAServer_CancelsItsSyncAndRemovesIt()
    {
        var folder = await AddServerAsync();
        var listing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<IMusicServerClient>();
        client.GetSongsAsync(Arg.Any<CancellationToken>()).Returns(call => Stalled(listing, call.Arg<CancellationToken>()));
        var sync = _libraryService.SyncServerFolderAsync(folder.Id, client);
        await listing.Task.WaitAsync(TimeSpan.FromSeconds(5));

        (await _libraryService.RemoveFolderAsync(folder.Id)).Should().BeTrue();

        await FluentActions.Awaiting(() => sync.WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().ThrowAsync<OperationCanceledException>();
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.MusicServers.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ClearAllLibraryData_AnnouncesEachServerAsRemoved()
    {
        var folder = await AddServerAsync();
        var removed = new List<Guid?>();
        _libraryService.LibraryContentChanged += (_, e) =>
        {
            if (e.ChangeType == LibraryChangeType.FolderRemoved) removed.Add(e.FolderId);
        };

        await _libraryService.ClearAllLibraryDataAsync();

        removed.Should().Equal(folder.Id);
    }

    [Fact]
    public async Task ServerRoots_AreLeftAloneByFolderScans()
    {
        var folder = await AddServerAsync();
        await _libraryService.SyncServerFolderAsync(folder.Id, ServerWith(ServerSong("s1", "Breakwater")));
        _fileSystem.DirectoryExists(Arg.Any<string>()).Returns(false);

        var rescanned = await _libraryService.RescanFolderForMusicAsync(folder.Id);
        var refreshed = await _libraryService.RefreshAllFoldersAsync();

        rescanned.Should().BeFalse();
        refreshed.Should().BeFalse();
        _fileSystem.DidNotReceive().DirectoryExists(folder.Path);
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.Songs.CountAsync()).Should().Be(1);
        (await context.MusicServers.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DeduplicateLibrary_LeavesServerKeysAlone()
    {
        var folder = await AddServerAsync();
        await _libraryService.SyncServerFolderAsync(folder.Id, ServerWith(ServerSong("s1", "Breakwater")));
        // Like the real normalization, which resolves a key that isn't a path against the working directory.
        _fileSystem.NormalizePath(Arg.Any<string>()).Returns(call => @"C:\Nagi\" + call.Arg<string>());

        await _libraryService.DeduplicateLibraryAsync();

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.Folders.SingleAsync()).Path.Should().Be(folder.Path);
        (await context.Songs.SingleAsync()).FilePath.Should().Be($@"{folder.Path}\s1");
    }

    private Task<Folder> AddServerAsync() => _libraryService.AddMusicServerAsync(
        new MusicServer
        {
            FolderId = Guid.NewGuid(),
            Kind = MusicServerKind.OpenSubsonic,
            BaseUrl = "https://music.example.com",
            UserName = "alex"
        },
        "Navidrome");

    private static IMusicServerClient ServerWith(params ServerSongMetadata[] songs)
    {
        var client = Substitute.For<IMusicServerClient>();
        client.GetSongsAsync(Arg.Any<CancellationToken>()).Returns(_ => Pages(songs));
        return client;
    }

    private static async IAsyncEnumerable<IReadOnlyList<ServerSongMetadata>> Pages(IReadOnlyList<ServerSongMetadata> songs)
    {
        await Task.Yield();
        yield return songs;
    }

    // A server that started answering and then went quiet, until the sync is cancelled.
    private static async IAsyncEnumerable<IReadOnlyList<ServerSongMetadata>> Stalled(TaskCompletionSource started,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        started.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }

    private static ServerSongMetadata ServerSong(string id, string title, string album = "Saltwater Hymns",
        bool isFavorite = false, int playCount = 0, DateTime? lastPlayed = null, DateTime? added = null,
        string? coverArtId = null, string? fileExtension = null) => new()
        {
            RemoteId = id,
            FileExtension = fileExtension,
            Title = title,
            Album = album,
            Artists = ["Harbor Lights"],
            Duration = TimeSpan.FromMinutes(3),
            IsFavorite = isFavorite,
            PlayCount = playCount,
            LastPlayedUtc = lastPlayed,
            AddedUtc = added,
            CoverArtId = coverArtId
        };
}
