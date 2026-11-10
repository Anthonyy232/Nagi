using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Library;
using Nagi.Core.Listening;
using Nagi.Core.OnlineProviders;
using Nagi.Core.Playlists;
using Nagi.Core.Settings;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.Library;

public class LibraryResetTests : LibraryTestBase
{
    [Fact]
    public async Task RunWhileQuiet_TakesTheWriteLockOnlyOnceTheMetadataFetchHasStopped()
    {
        // The fetch takes the write lock to store a cover, so taking the write lock first could deadlock with it.
        _settingsService.Set(CoreSettings.FetchOnlineMetadata, true);
        _settingsService.EnableOnly(CoreSettings.MetadataProviders, ServiceProviderIds.LastFm);
        var artist = new Artist { Name = "Pending artist" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Artists.Add(artist);
            await context.SaveChangesAsync();
        }
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The lookup blocks its caller instead of returning a pending task: a cancelled fetch stops waiting for a
        // pending lookup, so it could end, and let the quiet step run, at any moment.
        var lookup = new ManualResetEventSlim();
        _lastFmService.GetArtistInfoAsync(artist.Name, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            started.TrySetResult();
            lookup.Wait(TimeSpan.FromSeconds(5));
            return ServiceResult<ArtistInfo>.FromSuccessNotFound();
        });
        var fetch = _library.Artwork.StartMetadataBackgroundFetchAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var ranQuiet = false;

        var quiet = _library.Reset.RunWhileQuietAsync("test", () =>
        {
            ranQuiet = true;
            return Task.CompletedTask;
        });

        var writeLock = _library.WriteLock.WaitAsync(CancellationToken.None);
        writeLock.IsCompletedSuccessfully.Should().BeTrue("the quiet step waits for the fetch before the write lock");
        _library.WriteLock.Release();
        ranQuiet.Should().BeFalse();
        lookup.Set();
        await Task.WhenAll(fetch, quiet).WaitAsync(TimeSpan.FromSeconds(5));
        ranQuiet.Should().BeTrue();
    }

    [Fact]
    public async Task ClearAllLibraryDataAsync_WhenCalled_DeletesAllDataAndRecreatesDirectories()
    {
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var song = new Song
            {
                FilePath = @"C:\Music\song.mp3",
                Folder = new Folder { Name = "Test", Path = @"C:\Music" },
                Album = new Album { Title = "Album" },
                Genres = [new Genre { Name = "Genre" }],
                SongArtists = [new SongArtist { Artist = new Artist { Name = "Test Artist" } }]
            };
            context.ListenHistory.Add(new ListenHistory { Song = song });
            context.PlaylistSongs.Add(new PlaylistSong { Song = song, Playlist = new Playlist { Name = "Playlist" } });
            context.SmartPlaylists.Add(new SmartPlaylist
            {
                Name = "Favorites",
                Rules = [new SmartPlaylistRule { Field = SmartPlaylistField.Title, Operator = SmartPlaylistOperator.Contains, Value = "favorite" }]
            });
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(Arg.Any<string>()).Returns(true);

        await _library.Reset.ClearAllLibraryDataAsync();

        await using (var assertContext = _dbHelper.ContextFactory.CreateDbContext())
        {
            (await assertContext.Folders.CountAsync()).Should().Be(0);
            (await assertContext.Artists.CountAsync()).Should().Be(0);
            (await assertContext.Songs.CountAsync()).Should().Be(0);
            (await assertContext.Albums.CountAsync()).Should().Be(0);
            (await assertContext.Genres.CountAsync()).Should().Be(0);
            (await assertContext.Playlists.CountAsync()).Should().Be(0);
            (await assertContext.PlaylistSongs.CountAsync()).Should().Be(0);
            (await assertContext.ListenHistory.CountAsync()).Should().Be(0);
            (await assertContext.SmartPlaylists.CountAsync()).Should().Be(0);
            (await assertContext.SmartPlaylistRules.CountAsync()).Should().Be(0);
        }

        _fileSystem.Received(1).DeleteDirectory(_pathConfig.AlbumArtCachePath, true);
        _fileSystem.Received(1).DeleteDirectory(_pathConfig.ArtistImageCachePath, true);
        _fileSystem.Received(1).DeleteDirectory(_pathConfig.LrcCachePath, true);
        _fileSystem.Received(1).DeleteDirectory(_pathConfig.PlaylistImageCachePath, true);
        _fileSystem.Received(1).CreateDirectory(_pathConfig.AlbumArtCachePath);
        _fileSystem.Received(1).CreateDirectory(_pathConfig.ArtistImageCachePath);
        _fileSystem.Received(1).CreateDirectory(_pathConfig.LrcCachePath);
        _fileSystem.Received(1).CreateDirectory(_pathConfig.PlaylistImageCachePath);
    }
}
