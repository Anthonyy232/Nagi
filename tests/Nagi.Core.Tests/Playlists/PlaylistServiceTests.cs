using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Artwork;
using Nagi.Core.Library;
using Nagi.Core.Platform;
using Nagi.Core.Playlists;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.Playlists;

public class PlaylistServiceTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper = new();
    private readonly IFileSystemService _fileSystem = Substitute.For<IFileSystemService>();
    private readonly IPathConfiguration _pathConfig = Substitute.For<IPathConfiguration>();
    private readonly PlaylistService _playlistService;

    public PlaylistServiceTests()
    {
        _pathConfig.PlaylistImageCachePath.Returns(@"C:\cache\playlistimages");
        _playlistService = new PlaylistService(_dbHelper.ContextFactory, _fileSystem, Substitute.For<IImageProcessor>(),
            _pathConfig, Substitute.For<ILibraryNotifier>(), Substitute.For<ILogger<PlaylistService>>());
    }

    public void Dispose() => _dbHelper.Dispose();

    [Fact]
    public async Task RenamePlaylistAsync_EmitsPersistedModificationDate()
    {
        var playlist = await _playlistService.CreatePlaylistAsync("Original name");
        PlaylistUpdatedEventArgs? update = null;
        _playlistService.PlaylistUpdated += (_, args) => update = args;

        var success = await _playlistService.RenamePlaylistAsync(playlist!.Id, "New name");

        success.Should().BeTrue();
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var persisted = await context.Playlists.FindAsync(playlist.Id);
        update.Should().NotBeNull();
        update!.PlaylistId.Should().Be(playlist.Id);
        update.DateModified.Should().Be(persisted!.DateModified);
    }

    [Fact]
    public async Task CreateOrRename_ToANameAlreadyTaken_ThrowsNameTakenAndChangesNothing()
    {
        await _playlistService.CreatePlaylistAsync("Road Trip");
        var other = await _playlistService.CreatePlaylistAsync("Other");

        await FluentActions.Awaiting(() => _playlistService.CreatePlaylistAsync("road trip")).Should().ThrowAsync<PlaylistNameTakenException>();
        await FluentActions.Awaiting(() => _playlistService.RenamePlaylistAsync(other.Id, "ROAD TRIP")).Should().ThrowAsync<PlaylistNameTakenException>();

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        context.Playlists.Select(p => p.Name).Should().BeEquivalentTo("Road Trip", "Other");
    }

    [Fact]
    public async Task FindFreeNameAsync_NumbersPastNagisOwnTakenNames()
    {
        await _playlistService.CreatePlaylistAsync("mix");
        await _playlistService.CreatePlaylistAsync("Mix (2)");
        var (serverPlaylist, songs) = await _dbHelper.SeedPlaylistAsync(1);
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var stored = (await context.Playlists.FindAsync(serverPlaylist.Id))!;
            stored.Name = "Mix (3)";
            stored.ServerFolderId = songs[0].FolderId;
            await context.SaveChangesAsync();
        }

        (await _playlistService.FindFreeNameAsync(" Mix ")).Should().Be("Mix (3)");
        (await _playlistService.FindFreeNameAsync("Road Trip")).Should().Be("Road Trip");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReorderingSongs_TellsPlaylistListsAboutTheNewModificationDate(bool move)
    {
        var (playlist, songs) = await _dbHelper.SeedPlaylistAsync(1, 2);
        var changed = false;
        PlaylistUpdatedEventArgs? update = null;
        _playlistService.PlaylistsChanged += (_, _) => changed = true;
        _playlistService.PlaylistUpdated += (_, args) => update = args;

        var success = move
            ? await _playlistService.MoveSongAsync(playlist.Id, songs[0].Id, songs[1].Id, null)
            : await _playlistService.UpdatePlaylistOrderAsync(playlist.Id, [songs[1].Id, songs[0].Id]);

        success.Should().BeTrue();
        changed.Should().BeTrue();
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        update!.DateModified.Should().Be((await context.Playlists.FindAsync(playlist.Id))!.DateModified);
    }

    [Fact]
    public async Task APlaylistFromAServer_CantBeChangedNorTakeANameFromNagisOwn()
    {
        var (playlist, songs) = await _dbHelper.SeedPlaylistAsync(1, 2);
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            (await context.Playlists.FindAsync(playlist.Id))!.ServerFolderId = songs[0].FolderId;
            await context.SaveChangesAsync();
        }

        (await _playlistService.RenamePlaylistAsync(playlist.Id, "Mine")).Should().BeFalse();
        (await _playlistService.AddSongsToPlaylistAsync(playlist.Id, [Guid.NewGuid()])).Should().BeFalse();
        (await _playlistService.RemoveSongsFromPlaylistAsync(playlist.Id, [songs[0].Id])).Should().BeFalse();
        (await _playlistService.MoveSongAsync(playlist.Id, songs[0].Id, songs[1].Id, null)).Should().BeFalse();
        (await _playlistService.DeletePlaylistAsync(playlist.Id)).Should().BeFalse();
        await _playlistService.CreatePlaylistAsync(playlist.Name);

        await using var check = _dbHelper.ContextFactory.CreateDbContext();
        (await check.PlaylistSongs.Where(ps => ps.PlaylistId == playlist.Id).OrderBy(ps => ps.Order).Select(ps => ps.SongId).ToListAsync())
            .Should().Equal(songs[0].Id, songs[1].Id);
        (await check.Playlists.Select(p => p.Name).ToListAsync()).Should().Equal(playlist.Name, playlist.Name);
    }

    [Fact]
    public async Task AddSongsToPlaylistAsync_AppendsUniqueSongsAfterExistingFractionalOrders()
    {
        var (playlist, songs) = await _dbHelper.SeedPlaylistAsync(0.5, 1.5, 2.5);
        await _playlistService.RemoveSongsFromPlaylistAsync(playlist.Id, [songs[1].Id, songs[2].Id]);

        await _playlistService.AddSongsToPlaylistAsync(playlist.Id, [songs[0].Id, songs[2].Id, songs[2].Id, songs[1].Id]);

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var entries = await context.PlaylistSongs.OrderBy(x => x.Order).ToListAsync();
        entries.Select(x => x.SongId).Should().Equal(songs[0].Id, songs[2].Id, songs[1].Id);
        entries.Select(x => x.Order).Should().Equal(0.5, 1, 2);
    }

    [Fact]
    public async Task MoveSongAsync_WithNoRoomBetweenTheNeighbours_NumbersThePlaylistAgain()
    {
        var (playlist, songs) = await _dbHelper.SeedPlaylistAsync(1, 2, 2 + 1e-12, 4);

        (await _playlistService.MoveSongAsync(playlist.Id, songs[0].Id, songs[1].Id, songs[2].Id)).Should().BeTrue();
        (await _playlistService.MoveSongAsync(playlist.Id, songs[3].Id, songs[1].Id, songs[0].Id)).Should().BeTrue();

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var entries = await context.PlaylistSongs.OrderBy(x => x.Order).ToListAsync();
        entries.Select(x => x.SongId).Should().Equal(songs[1].Id, songs[3].Id, songs[0].Id, songs[2].Id);
        entries.Select(x => x.Order).Should().Equal(1, 1.5, 2, 3);
    }

    [Fact]
    public async Task MoveSongAsync_PersistsAFractionalOrderBetweenItsNeighboursAndTheModificationDate()
    {
        var (playlist, songs) = await _dbHelper.SeedPlaylistAsync(1, 2, 3);

        var result = await _playlistService.MoveSongAsync(playlist.Id, songs[2].Id, songs[0].Id, songs[1].Id);

        result.Should().BeTrue();
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var entries = await context.PlaylistSongs.OrderBy(x => x.Order).ToListAsync();
        entries.Select(x => x.SongId).Should().Equal(songs[0].Id, songs[2].Id, songs[1].Id);
        entries.Select(x => x.Order).Should().Equal(1, 1.5, 2);
        (await context.Playlists.FindAsync(playlist.Id))!.DateModified.Should().BeAfter(playlist.DateModified);
    }

    [Theory]
    [InlineData(1, 0.5)]
    [InlineData(0.25, -0.75)]
    public async Task MoveSongAsync_ToTheTop_OrdersTheSongBeforeTheFirstOne(double firstOrder, double expectedOrder)
    {
        var (playlist, songs) = await _dbHelper.SeedPlaylistAsync(firstOrder, firstOrder + 1, firstOrder + 2);

        (await _playlistService.MoveSongAsync(playlist.Id, songs[2].Id, null, songs[0].Id)).Should().BeTrue();

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var entries = await context.PlaylistSongs.OrderBy(x => x.Order).ToListAsync();
        entries.Select(x => x.SongId).Should().Equal(songs[2].Id, songs[0].Id, songs[1].Id);
        entries[0].Order.Should().Be(expectedOrder);
    }

    [Fact]
    public async Task UpdatePlaylistOrderAsync_NormalizesEvenNearIntegerValues()
    {
        var (playlist, songs) = await _dbHelper.SeedPlaylistAsync(1.0 + 1e-12, 1.5, 1.6);

        await _playlistService.UpdatePlaylistOrderAsync(playlist.Id, songs.Select(s => s.Id));

        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var entries = await context.PlaylistSongs.OrderBy(x => x.Order).ToListAsync();
        entries.Select(x => x.SongId).Should().Equal(songs.Select(s => s.Id));
        entries.Select(x => x.Order).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task UpdatePlaylistCoverAsync_WhenSelectedFileIsMissing_PreservesExistingCover()
    {
        var modified = DateTime.UtcNow.AddDays(-1);
        var playlist = new Playlist
        {
            Name = "Playlist",
            CoverImageUri = @"C:\cache\playlistimages\cover.jpg",
            DateModified = modified
        };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Playlists.Add(playlist);
            await context.SaveChangesAsync();
        }

        var updated = await _playlistService.UpdatePlaylistCoverAsync(playlist.Id, @"C:\Pictures\missing.jpg");

        updated.Should().BeFalse();
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var stored = await assertContext.Playlists.SingleAsync();
        stored.CoverImageUri.Should().Be(playlist.CoverImageUri);
        stored.DateModified.Should().Be(modified);
        _fileSystem.DidNotReceive().DeleteFile(Arg.Any<string>());
    }

    [Fact]
    public async Task DeletePlaylistAsync_DeletesCustomCoverAndStillRemovesPlaylistWhenCleanupFails()
    {
        var playlist = await _playlistService.CreatePlaylistAsync("Playlist");
        var changed = false;
        _playlistService.PlaylistsChanged += (_, _) => changed = true;
        var cachePath = _pathConfig.PlaylistImageCachePath;
        var expectedImagePath = Path.Combine(cachePath, $"{playlist!.Id}.custom.jpg");
        _fileSystem.DirectoryExists(cachePath).Returns(true);
        _fileSystem.FileExists(expectedImagePath).Returns(true);
        _fileSystem.When(fs => fs.DeleteFile(expectedImagePath)).Do(_ => throw new IOException("File is locked"));

        (await _playlistService.DeletePlaylistAsync(playlist.Id)).Should().BeTrue();

        _fileSystem.Received(1).DeleteFile(expectedImagePath);
        changed.Should().BeTrue();
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.Playlists.CountAsync()).Should().Be(0);
    }
}
