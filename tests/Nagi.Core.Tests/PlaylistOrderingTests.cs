using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

public class PlaylistOrderingTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly LibraryService _libraryService;
    private readonly ILogger<LibraryService> _logger;

    public PlaylistOrderingTests()
    {
        _dbHelper = new DbContextFactoryTestHelper();
        _logger = Substitute.For<ILogger<LibraryService>>();

        _libraryService = new LibraryService(
            _dbHelper.ContextFactory,
            Substitute.For<IFileSystemService>(),
            Substitute.For<IMetadataService>(),
            Substitute.For<ILastFmMetadataService>(),
            Substitute.For<ISpotifyService>(),
            Substitute.For<IMusicBrainzService>(),
            Substitute.For<IFanartTvService>(),
            Substitute.For<ITheAudioDbService>(),
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<IPathConfiguration>(),
            Substitute.For<ISettingsService>(),
            Substitute.For<IReplayGainService>(),
            Substitute.For<IApiKeyService>(),
            Substitute.For<IImageProcessor>(),
            _logger);
    }

    public void Dispose()
    {
        _libraryService.Dispose();
        _dbHelper.Dispose();
    }

    [Fact]
    public async Task AddSongsToPlaylistAsync_AssignsSequentialDoubleOrders()
    {
        // Arrange
        var playlist = await _libraryService.CreatePlaylistAsync("Test");
        var folder = new Folder { Id = Guid.NewGuid(), Name = "F", Path = "C:\\" };
        var song1 = new Song { Id = Guid.NewGuid(), Title = "Song 1", FilePath = "1.mp3", FolderId = folder.Id, DirectoryPath = "C:\\" };
        var song2 = new Song { Id = Guid.NewGuid(), Title = "Song 2", FilePath = "2.mp3", FolderId = folder.Id, DirectoryPath = "C:\\" };
        
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Songs.AddRange(song1, song2);
            await context.SaveChangesAsync();
        }

        // Act
        await _libraryService.AddSongsToPlaylistAsync(playlist!.Id, new[] { song1.Id, song2.Id });

        // Assert
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var ps = await context.PlaylistSongs.OrderBy(x => x.Order).ToListAsync();
            ps.Should().HaveCount(2);
            ps[0].Order.Should().Be(1.0);
            ps[1].Order.Should().Be(2.0);
        }
    }

    [Fact]
    public async Task MovePlaylistSongAsync_BetweenTwoSongs_CalculatesMiddleOrder()
    {
        // Arrange: s1 (order=1), s2 (order=2), movedSong (order=3)
        // Move movedSong between s1 and s2 -> new order should be 1.5
        var playlist = new Playlist { Id = Guid.NewGuid(), Name = "Test" };
        var folder = new Folder { Id = Guid.NewGuid(), Name = "F", Path = "C:\\" };
        var s1 = new Song { Id = Guid.NewGuid(), Title = "S1", FilePath = "1.mp3", FolderId = folder.Id, DirectoryPath = "C:\\" };
        var s2 = new Song { Id = Guid.NewGuid(), Title = "S2", FilePath = "2.mp3", FolderId = folder.Id, DirectoryPath = "C:\\" };
        var movedSong = new Song { Id = Guid.NewGuid(), Title = "Moved", FilePath = "m.mp3", FolderId = folder.Id, DirectoryPath = "C:\\" };

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Playlists.Add(playlist);
            context.Folders.Add(folder);
            context.Songs.AddRange(s1, s2, movedSong);
            context.PlaylistSongs.AddRange(
                new PlaylistSong { PlaylistId = playlist.Id, SongId = s1.Id, Order = 1.0 },
                new PlaylistSong { PlaylistId = playlist.Id, SongId = s2.Id, Order = 2.0 },
                new PlaylistSong { PlaylistId = playlist.Id, SongId = movedSong.Id, Order = 3.0 }
            );
            await context.SaveChangesAsync();
        }

        // Act: Move movedSong between s1 and s2 (new order = (1.0 + 2.0) / 2 = 1.5)
        var result = await _libraryService.MovePlaylistSongAsync(playlist.Id, movedSong.Id, 1.5);

        // Assert
        result.Should().BeTrue();
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var ps = await context.PlaylistSongs.FirstAsync(x => x.SongId == movedSong.Id);
            ps.Order.Should().Be(1.5);
        }
    }

    [Fact]
    public async Task MovePlaylistSongAsync_ToTop_SetsHalfOfFirstOrder()
    {
        // Arrange: s1 (order=1), movedSong (order=2)
        // Move movedSong to top -> new order should be 0.5 (half of first)
        var playlist = new Playlist { Id = Guid.NewGuid(), Name = "Test" };
        var folder = new Folder { Id = Guid.NewGuid(), Name = "F", Path = "C:\\" };
        var s1 = new Song { Id = Guid.NewGuid(), Title = "S1", FilePath = "1.mp3", FolderId = folder.Id, DirectoryPath = "C:\\" };
        var movedSong = new Song { Id = Guid.NewGuid(), Title = "Moved", FilePath = "m.mp3", FolderId = folder.Id, DirectoryPath = "C:\\" };

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Playlists.Add(playlist);
            context.Folders.Add(folder);
            context.Songs.AddRange(s1, movedSong);
            context.PlaylistSongs.AddRange(
                new PlaylistSong { PlaylistId = playlist.Id, SongId = s1.Id, Order = 1.0 },
                new PlaylistSong { PlaylistId = playlist.Id, SongId = movedSong.Id, Order = 2.0 }
            );
            await context.SaveChangesAsync();
        }

        // Act: Move to top (new order = 1.0 / 2 = 0.5)
        await _libraryService.MovePlaylistSongAsync(playlist.Id, movedSong.Id, 0.5);

        // Assert
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var ps = await context.PlaylistSongs.FirstAsync(x => x.SongId == movedSong.Id);
            ps.Order.Should().Be(0.5);
        }
    }

    [Fact]
    public async Task MovePlaylistSongAsync_ToBottom_IncrementsPrevByOne()
    {
        // Arrange: movedSong (order=1), s1 (order=10)
        // Move movedSong to bottom -> new order should be 11 (last + 1)
        var playlist = new Playlist { Id = Guid.NewGuid(), Name = "Test" };
        var folder = new Folder { Id = Guid.NewGuid(), Name = "F", Path = "C:\\" };
        var s1 = new Song { Id = Guid.NewGuid(), Title = "S1", FilePath = "1.mp3", FolderId = folder.Id, DirectoryPath = "C:\\" };
        var movedSong = new Song { Id = Guid.NewGuid(), Title = "Moved", FilePath = "m.mp3", FolderId = folder.Id, DirectoryPath = "C:\\" };

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Playlists.Add(playlist);
            context.Folders.Add(folder);
            context.Songs.AddRange(s1, movedSong);
            context.PlaylistSongs.AddRange(
                new PlaylistSong { PlaylistId = playlist.Id, SongId = movedSong.Id, Order = 1.0 },
                new PlaylistSong { PlaylistId = playlist.Id, SongId = s1.Id, Order = 10.0 }
            );
            await context.SaveChangesAsync();
        }

        // Act: Move to bottom (new order = 10.0 + 1 = 11.0)
        await _libraryService.MovePlaylistSongAsync(playlist.Id, movedSong.Id, 11.0);

        // Assert
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var ps = await context.PlaylistSongs.FirstAsync(x => x.SongId == movedSong.Id);
            ps.Order.Should().Be(11.0);
        }
    }

    [Fact]
    public async Task UpdatePlaylistOrderAsync_Normalization_ResetsToIntegers()
    {
        // Arrange
        var playlist = new Playlist { Id = Guid.NewGuid(), Name = "Test" };
        var folder = new Folder { Id = Guid.NewGuid(), Name = "F", Path = "C:\\" };
        var s1 = new Song { Id = Guid.NewGuid(), Title = "S1", FilePath = "1.mp3", FolderId = folder.Id, DirectoryPath = "C:\\" };
        var s2 = new Song { Id = Guid.NewGuid(), Title = "S2", FilePath = "2.mp3", FolderId = folder.Id, DirectoryPath = "C:\\" };

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Playlists.Add(playlist);
            context.Folders.Add(folder);
            context.Songs.AddRange(s1, s2);
            context.PlaylistSongs.AddRange(
                new PlaylistSong { PlaylistId = playlist.Id, SongId = s1.Id, Order = 1.234 },
                new PlaylistSong { PlaylistId = playlist.Id, SongId = s2.Id, Order = 1.567 }
            );
            await context.SaveChangesAsync();
        }

        // Act: Full reset/normalization
        await _libraryService.UpdatePlaylistOrderAsync(playlist.Id, new[] { s1.Id, s2.Id });

        // Assert
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var ps = await context.PlaylistSongs.OrderBy(x => x.Order).ToListAsync();
            ps.Should().HaveCount(2);
            ps[0].Order.Should().Be(1.0);
            ps[1].Order.Should().Be(2.0);
        }
    }
}
