using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

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
            _pathConfig, Substitute.For<ILogger<PlaylistService>>());
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
    public async Task MovePlaylistSongAsync_PersistsFractionalOrderAndModificationDate()
    {
        var (playlist, songs) = await _dbHelper.SeedPlaylistAsync(1, 2, 3);

        var result = await _playlistService.MovePlaylistSongAsync(playlist.Id, songs[2].Id, 1.5);

        result.Should().BeTrue();
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        var entries = await context.PlaylistSongs.OrderBy(x => x.Order).ToListAsync();
        entries.Select(x => x.SongId).Should().Equal(songs[0].Id, songs[2].Id, songs[1].Id);
        entries.Select(x => x.Order).Should().Equal(1, 1.5, 2);
        (await context.Playlists.FindAsync(playlist.Id))!.DateModified.Should().BeAfter(playlist.DateModified);
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
            Name = "Playlist", CoverImageUri = @"C:\cache\playlistimages\cover.jpg", DateModified = modified
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
    public async Task DeletePlaylistAsync_WhenCoverCleanupFails_StillRemovesPlaylistAndNotifies()
    {
        var playlist = await _playlistService.CreatePlaylistAsync("Playlist");
        var changed = false;
        _playlistService.PlaylistsChanged += (_, _) => changed = true;
        _fileSystem.DirectoryExists(_pathConfig.PlaylistImageCachePath).Returns(true);
        _fileSystem.FileExists(Arg.Any<string>()).Returns(true);
        _fileSystem.When(fs => fs.DeleteFile(Arg.Any<string>())).Do(_ => throw new IOException("File is locked"));

        (await _playlistService.DeletePlaylistAsync(playlist!.Id)).Should().BeTrue();

        changed.Should().BeTrue();
        await using var context = _dbHelper.ContextFactory.CreateDbContext();
        (await context.Playlists.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DeletePlaylistAsync_WithCustomCover_DeletesImageFromDisk()
    {
        var playlistName = "Playlist with Art";
        var playlist = await _playlistService.CreatePlaylistAsync(playlistName);
        playlist.Should().NotBeNull();

        var cachePath = _pathConfig.PlaylistImageCachePath;
        var expectedImagePath = Path.Combine(cachePath, $"{playlist!.Id}.custom.jpg");
        _fileSystem.DirectoryExists(cachePath).Returns(true);
        _fileSystem.FileExists(expectedImagePath).Returns(true);

        var result = await _playlistService.DeletePlaylistAsync(playlist.Id);

        result.Should().BeTrue();
        _fileSystem.Received(1).DeleteFile(expectedImagePath);

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Playlists.CountAsync()).Should().Be(0);
    }
}
