using System.Text;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Library;
using Nagi.Core.MusicServers;
using Nagi.Core.Playlists;
using Nagi.Core.Settings;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.MusicServers;

public class ServerLibrarySyncTests : LibraryTestBase
{
    [Fact]
    public async Task SyncServerFolder_AddsTheServersSongsUnderItsRoot()
    {
        var folder = await AddServerAsync();
        var added = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var played = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var client = ServerWith(ServerSong("s1", "Breakwater", isFavorite: true, playCount: 7, lastPlayed: played, added: added));

        await _library.Servers.SyncAsync(folder.Id, client);

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

        await _library.Servers.SyncAsync(folder.Id, ServerWith(ServerSong(id, "Breakwater", fileExtension: extension)));

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.Songs.SingleAsync()).FilePath.Should().Be(folder.Path + expected);
    }

    [Fact]
    public async Task SyncServerFolder_UpdatesChangedSongsAndRemovesTheOnesNoLongerListed()
    {
        var folder = await AddServerAsync();
        await _library.Servers.SyncAsync(folder.Id, ServerWith(ServerSong("s1", "Old title"), ServerSong("s2", "Gone")));

        await _library.Servers.SyncAsync(folder.Id, ServerWith(ServerSong("s1", "New title")));

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var song = await context.Songs.SingleAsync();
        song.RemoteId.Should().Be("s1");
        song.Title.Should().Be("New title");
    }

    [Fact]
    public async Task SyncServerFolder_SongsTheServerDescribesTheSameWay_ChangeNothing()
    {
        var folder = await AddServerAsync();
        await _library.Servers.SyncAsync(folder.Id, ServerWith(ServerSong("s1", "Breakwater")));
        var events = 0;
        _library.Notifier.Changed += (_, _) => events++;
        _library.Playlists.PlaylistsChanged += (_, _) => events++;

        await _library.Servers.SyncAsync(folder.Id, ServerWith(ServerSong("s1", "Breakwater")));

        events.Should().Be(0);
    }

    [Fact]
    public async Task SyncServerFolder_SongWithoutATitleAlbumOrArtist_GetsTheDefaultsTagsGet()
    {
        var folder = await AddServerAsync();
        var song = ServerSong("s1", " ");
        song.Album = null;
        song.Artists = [];

        await _library.Servers.SyncAsync(folder.Id, ServerWith(song));

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var stored = await context.Songs.Include(s => s.Album).Include(s => s.SongArtists).ThenInclude(sa => sa.Artist)
            .SingleAsync();
        stored.Title.Should().Be(Song.UnknownTitle);
        stored.Album!.Title.Should().Be(Album.UnknownAlbumName);
        stored.SongArtists.Single().Artist.Name.Should().Be(Artist.UnknownArtistName, "the song belongs on that artist's page");
    }

    [Fact]
    public async Task SyncServerFolder_SongWithoutAlbumArtists_AddsNoUnknownArtist()
    {
        var folder = await AddServerAsync();
        var song = ServerSong("s1", "One");
        song.AlbumArtists = [];

        await _library.Servers.SyncAsync(folder.Id, ServerWith(song));

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.Artists.Select(a => a.Name).ToListAsync()).Should().Equal("Harbor Lights");
        (await context.Albums.SingleAsync()).ArtistName.Should().Be("Harbor Lights");
    }

    [Fact]
    public async Task SyncServerFolder_AfterTheSplitCharactersChange_SplitsTheServersNamesLikeTags()
    {
        var folder = await AddServerAsync();
        var song = ServerSong("s1", "Breakwater");
        song.Artists = ["Mareen Holt; Kiko Mori", "Kiko Mori"];
        song.Genres = ["Folk/Ambient"];
        var client = ServerWith(song);
        await _library.Servers.SyncAsync(folder.Id, client);

        _settingsService.Set(CoreSettings.ArtistSplitCharacters, ";");
        _settingsService.Set(CoreSettings.GenreSplitCharacters, "/");
        await _library.Servers.SyncAsync(folder.Id, client);

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
        await _library.Servers.SyncAsync(folder.Id, ServerWith(ServerSong("s1", "Breakwater")));
        Guid songId;
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
            songId = await context.Songs.Select(s => s.Id).SingleAsync();

        await _library.Listening.SetSongsLovedAsync([songId], true);
        await _library.Servers.SyncAsync(folder.Id, ServerWith(ServerSong("s1", "Breakwater", playCount: 3)));

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
        await _library.Servers.SyncAsync(folder.Id, ServerWith(ServerSong("s1", "Breakwater", isFavorite: true)));
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
            await _library.Listening.SetSongsLovedAsync([await context.Songs.Select(s => s.Id).SingleAsync()], false);
        // The server was told, then the favorite came back from another app, so the server lists the song as before.
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
            await context.Songs.ExecuteUpdateAsync(setters => setters.SetProperty(s => s.IsLovedSyncPending, false));

        await _library.Servers.SyncAsync(folder.Id, ServerWith(ServerSong("s1", "Breakwater", isFavorite: true)));

        await using var check = _dbHelper.ContextFactory.CreateDbContext();
        (await check.Songs.SingleAsync()).IsLoved.Should().BeTrue();
    }

    [Fact]
    public async Task SetSongsLoved_MarksOnlyServerSongsForTheServer()
    {
        var folder = await AddServerAsync();
        await _library.Servers.SyncAsync(folder.Id, ServerWith(ServerSong("s1", "Remote")));
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var local = new Folder { Name = "Music", Path = @"C:\Music" };
            context.Songs.Add(new Song { Title = "Local", FilePath = @"C:\Music\a.flac", DirectoryPath = local.Path, Folder = local });
            await context.SaveChangesAsync();
        }

        List<Guid> ids;
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
            ids = await context.Songs.Select(s => s.Id).ToListAsync();
        await _library.Listening.SetSongsLovedAsync(ids, true);

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
        await _library.Servers.SyncAsync(folder.Id, ServerWith(ServerSong("s1", "Breakwater")));

        var changed = await SyncChangesLibraryAsync(folder.Id, ServerWith());

        changed.Should().BeFalse();
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.Songs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SyncServerFolder_ServerThatListsFarFewerSongs_KeepsThem()
    {
        var folder = await AddServerAsync();
        var songs = Enumerable.Range(0, 60).Select(i => ServerSong($"s{i}", $"Song {i}")).ToArray();
        await _library.Servers.SyncAsync(folder.Id, ServerWith(songs));

        await _library.Servers.SyncAsync(folder.Id, ServerWith(songs[0]));

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

        await _library.Servers.SyncAsync(folder.Id, client);
        await _library.Servers.SyncAsync(folder.Id, client);
        await client.Received(1).GetCoverArtAsync("al-1", Arg.Any<CancellationToken>());

        _fileSystem.FileExists(@"C:\cache\albumart\cover.jpg").Returns(false);
        await _library.Servers.SyncAsync(folder.Id, client);

        await client.Received(2).GetCoverArtAsync("al-1", Arg.Any<CancellationToken>());
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.Songs.ToListAsync()).Should().OnlyContain(s =>
            s.AlbumArtUriFromTrack == @"C:\cache\albumart\cover.jpg" && s.LightSwatchId == "light" && s.DarkSwatchId == "dark");
        (await context.Albums.SingleAsync()).CoverArtUri.Should().Be(@"C:\cache\albumart\cover.jpg");
    }

    [Fact]
    public async Task SyncServerFolder_StoresADownloadedCoverUnderTheWriteLock()
    {
        var folder = await AddServerAsync();
        var client = ServerWith(ServerSong("s1", "One", coverArtId: "al-1"));
        client.GetCoverArtAsync("al-1", Arg.Any<CancellationToken>()).Returns(new byte[] { 1, 2, 3 });
        bool? lockHeldDuringSave = null;
        _imageProcessor.SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>()).Returns(_ =>
        {
            var probe = _library.WriteLock.WaitAsync(CancellationToken.None);
            lockHeldDuringSave = !probe.IsCompleted;
            probe.ContinueWith(_ => _library.WriteLock.Release(), TaskScheduler.Default);
            return (@"C:\cache\albumart\cover.jpg", "light", "dark");
        });

        await _library.Servers.SyncAsync(folder.Id, client);

        lockHeldDuringSave.Should().BeTrue();
    }

    [Fact]
    public async Task SyncServerFolder_AnnouncesTheSongsAndThenTheirCovers()
    {
        var folder = await AddServerAsync();
        var client = ServerWith(ServerSong("s1", "One", coverArtId: "al-1"));
        client.GetCoverArtAsync("al-1", Arg.Any<CancellationToken>()).Returns(new byte[] { 1, 2, 3 });
        _imageProcessor.SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>())
            .Returns((@"C:\cache\albumart\cover.jpg", "light", "dark"));
        var changes = new List<LibraryChange>();
        _library.Notifier.Changed += (_, e) => changes.Add(e);

        await _library.Servers.SyncAsync(folder.Id, client);

        // Song lists reload for covers, which they do on SongsChanged.
        changes.Should().Equal(new LibraryChange(LibraryChangeKind.FolderRescanned, folder.Id),
            new LibraryChange(LibraryChangeKind.SongsChanged));
    }

    [Fact]
    public async Task SyncServerFolder_ARetaggedSongGivesItsCoverToItsNewAlbum()
    {
        var folder = await AddServerAsync();
        var client = ServerWith(ServerSong("s1", "One", coverArtId: "al-1"));
        client.GetCoverArtAsync("al-1", Arg.Any<CancellationToken>()).Returns(new byte[] { 1 });
        _imageProcessor.SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>()).Returns((@"C:\cache\albumart\cover.jpg", "light", "dark"));
        _fileSystem.FileExists(@"C:\cache\albumart\cover.jpg").Returns(true);
        await _library.Servers.SyncAsync(folder.Id, client);
        var retagged = ServerWith(ServerSong("s1", "One", album: "Homeport", coverArtId: "al-1"));

        await _library.Servers.SyncAsync(folder.Id, retagged);

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
        await _library.Servers.SyncAsync(folder.Id, client);

        await _library.Servers.SyncAsync(folder.Id, changed);

        await changed.DidNotReceive().GetCoverArtAsync("mf-1", Arg.Any<CancellationToken>());
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.Songs.SingleAsync(s => s.RemoteId == "s2")).AlbumArtUriFromTrack.Should().Be(@"C:\cache\albumart\mf-2-new.jpg");
        _fileSystem.Received(1).DeleteFile(@"C:\cache\albumart\mf-2.jpg");
    }

    [Fact]
    public async Task SyncServerFolder_SongListedOnTwoPages_IsAddedOnce()
    {
        var folder = await AddServerAsync();
        var client = ServerWith();
        client.GetSongsAsync(Arg.Any<CancellationToken>()).Returns(_ => Pages(
            [ServerSong("s1", "One"), ServerSong("s2", "Two")],
            [ServerSong("s2", "Two"), ServerSong("s3", "Three")]));

        await _library.Servers.SyncAsync(folder.Id, client);

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.Songs.Select(s => s.RemoteId).ToListAsync()).Should().BeEquivalentTo("s1", "s2", "s3");
    }

    [Fact]
    public async Task SyncServerFolder_WhenTheServerGoesAwayDuringCovers_StopsAskingForThemAndKeepsTheSongs()
    {
        var folder = await AddServerAsync();
        var songs = Enumerable.Range(0, 20).Select(i => ServerSong($"s{i}", $"Song {i}", coverArtId: $"al-{i}")).ToArray();
        var client = ServerWith(songs);
        var asked = 0;
        client.GetCoverArtAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (Interlocked.Increment(ref asked) == 1) throw new MusicServerException(MusicServerError.Unreachable, "Offline");
            await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
            return (byte[]?)null;
        });

        await _library.Servers.SyncAsync(folder.Id, client).WaitAsync(TimeSpan.FromSeconds(5));

        // Covers download a few at a time, so only the ones already started when the first failed were asked for.
        asked.Should().BeLessThan(songs.Length);
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.Songs.CountAsync()).Should().Be(songs.Length);
    }

    [Fact]
    public async Task SyncServerFolder_AddsThePlaylistsWithTheSongsTheLibraryHas()
    {
        var folder = await AddServerAsync();
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Playlists.Add(new Playlist { Name = "Road Trip" });
            await context.SaveChangesAsync();
        }

        var client = ServerWith(ServerSong("s1", "Breakwater"), ServerSong("s2", "Gulls"));
        client.GetPlaylistsAsync(default).ReturnsForAnyArgs([new ServerPlaylist("p1", "Road Trip", Created, ["s2", "unknown", "s1", "s2"])]);

        await _library.Servers.SyncAsync(folder.Id, client);

        await using var check = _dbHelper.ContextFactory.CreateDbContext();
        var playlist = await check.Playlists.SingleAsync(p => p.ServerFolderId == folder.Id);
        playlist.Name.Should().Be("Road Trip", "a server's playlist can share a name with one made in Nagi");
        playlist.DateCreated.Should().Be(Created, "sorting by date created goes by the server's date");
        (await check.PlaylistSongs.Where(ps => ps.PlaylistId == playlist.Id).OrderBy(ps => ps.Order).Select(ps => ps.Song.Title)
            .ToListAsync()).Should().Equal("Gulls", "Breakwater");
    }

    [Fact]
    public async Task SyncServerFolder_FollowsThePlaylistsTheServerChangesAndRemoves()
    {
        var folder = await AddServerAsync();
        var client = ServerWith(ServerSong("s1", "Breakwater"), ServerSong("s2", "Gulls"));
        client.GetPlaylistsAsync(default).ReturnsForAnyArgs(
            [new ServerPlaylist("p1", "Road Trip", null, ["s1", "s2"]), new ServerPlaylist("p2", "Deleted", null, ["s1"])],
            [new ServerPlaylist("p1", "Night Drive", null, ["s2", "s1"])]);
        await _library.Servers.SyncAsync(folder.Id, client);

        (await SyncChangesLibraryAsync(folder.Id, client)).Should().BeTrue();
        (await SyncChangesLibraryAsync(folder.Id, client)).Should().BeFalse();

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var playlist = await context.Playlists.SingleAsync();
        playlist.Name.Should().Be("Night Drive");
        (await context.PlaylistSongs.OrderBy(ps => ps.Order).Select(ps => ps.Song.Title).ToListAsync())
            .Should().Equal("Gulls", "Breakwater");
    }

    [Fact]
    public async Task RemoveServer_RemovesItsPlaylists()
    {
        var folder = await AddServerAsync();
        var client = ServerWith(ServerSong("s1", "Breakwater"));
        client.GetPlaylistsAsync(default).ReturnsForAnyArgs([new ServerPlaylist("p1", "Road Trip", null, ["s1"])]);
        await _library.Servers.SyncAsync(folder.Id, client);

        await _library.Servers.RemoveAsync(folder.Id);

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.Playlists.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ClearAllLibraryData_CancelsServerSyncsAndForgetsEachServersSignIn()
    {
        var folder = await AddServerAsync();

        await _library.Reset.ClearAllLibraryDataAsync();

        _library.MusicServers.Received(1).CancelSyncs();
        _library.MusicServers.Received(1).ForgetSignIns(Arg.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(new[] { folder.Id })));
    }

    [Fact]
    public async Task ServerRoots_AreLeftAloneByFolderScans()
    {
        var folder = await AddServerAsync();
        await _library.Servers.SyncAsync(folder.Id, ServerWith(ServerSong("s1", "Breakwater")));
        _fileSystem.DirectoryExists(Arg.Any<string>()).Returns(false);

        var rescanned = await _library.Folders.RescanFolderForMusicAsync(folder.Id);
        var refreshed = await _library.Folders.RefreshAllFoldersAsync();
        var removed = await _library.Folders.RemoveFolderAsync(folder.Id);

        rescanned.Should().BeFalse();
        refreshed.Should().BeFalse();
        removed.Should().BeFalse("a server is removed with its sign-in");
        _fileSystem.DidNotReceive().DirectoryExists(folder.Path);
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.Songs.CountAsync()).Should().Be(1);
        (await context.MusicServers.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DeduplicateLibrary_LeavesServerKeysAlone()
    {
        var folder = await AddServerAsync();
        await _library.Servers.SyncAsync(folder.Id, ServerWith(ServerSong("s1", "Breakwater")));
        // Like the real normalization, which resolves a key that isn't a path against the working directory.
        _fileSystem.NormalizePath(Arg.Any<string>()).Returns(call => @"C:\Nagi\" + call.Arg<string>());

        await _library.Folders.DeduplicateLibraryAsync();

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.Folders.SingleAsync()).Path.Should().Be(folder.Path);
        (await context.Songs.SingleAsync()).FilePath.Should().Be($@"{folder.Path}\s1");
    }

    private static readonly DateTime Created = new(2026, 5, 4, 3, 2, 1, DateTimeKind.Utc);

    private Task<Folder> AddServerAsync() => _library.Servers.AddAsync(
        new MusicServer
        {
            FolderId = Guid.NewGuid(),
            Kind = MusicServerKind.OpenSubsonic,
            BaseUrl = "https://music.example.com",
            UserName = "alex"
        },
        "Navidrome");

    // A sync tells what it changed only through the library's and the playlists' change events.
    private async Task<bool> SyncChangesLibraryAsync(Guid folderId, IMusicServerClient client)
    {
        var changed = false;
        EventHandler<LibraryChange> onLibraryChanged = (_, _) => changed = true;
        EventHandler onPlaylistsChanged = (_, _) => changed = true;
        _library.Notifier.Changed += onLibraryChanged;
        _library.Playlists.PlaylistsChanged += onPlaylistsChanged;
        try
        {
            await _library.Servers.SyncAsync(folderId, client);
        }
        finally
        {
            _library.Notifier.Changed -= onLibraryChanged;
            _library.Playlists.PlaylistsChanged -= onPlaylistsChanged;
        }

        return changed;
    }

    private static IMusicServerClient ServerWith(params ServerSongMetadata[] songs)
    {
        var client = Substitute.For<IMusicServerClient>();
        client.GetSongsAsync(Arg.Any<CancellationToken>()).Returns(_ => Pages(songs));
        client.GetPlaylistsAsync(Arg.Any<CancellationToken>()).Returns([]);
        return client;
    }

    private static async IAsyncEnumerable<IReadOnlyList<ServerSongMetadata>> Pages(params IReadOnlyList<ServerSongMetadata>[] pages)
    {
        await Task.Yield();
        foreach (var page in pages) yield return page;
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
