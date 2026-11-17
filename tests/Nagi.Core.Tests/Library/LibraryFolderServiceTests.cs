using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Library;
using Nagi.Core.Listening;
using Nagi.Core.Lyrics;
using Nagi.Core.Metadata;
using Nagi.Core.Playback;
using Nagi.Core.Playlists;
using Nagi.Core.Settings;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.Library;

public class LibraryFolderServiceTests : LibraryTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_DuringScanOrReplayGain_AllowsWorkerToReleaseItsGate(bool duringReplayGain)
    {
        var folder = new Folder { Path = @"C:\Music", Name = "Music" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }
        const string songPath = @"C:\Music\song.mp3";
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (songPath, DateTime.UtcNow) });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new TaskCompletionSource<SongFileMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        var songMetadata = new SongFileMetadata { FilePath = songPath, Title = "Song", Artists = ["Artist"] };
        _settingsService.Set(CoreSettings.VolumeNormalization, duringReplayGain);
        _replayGainService.ScanLibraryAsync(Arg.Any<IProgress<ScanProgress>?>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
        });
        _metadataService.ExtractMetadataAsync(songPath, folder.Path).Returns(_ =>
        {
            if (duringReplayGain) return Task.FromResult(songMetadata);
            started.TrySetResult();
            return metadata.Task;
        });

        var scan = _library.Folders.RescanFolderForMusicAsync(folder.Id);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _library.Dispose();
        metadata.SetResult(songMetadata);

        (await scan.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Songs.CountAsync()).Should().Be(duringReplayGain ? 1 : 0);
    }

    [Fact]
    public async Task RefreshAllFoldersAsync_WhenReplacingAnActiveRefresh_RunsReplayGainOnceAfterAllFolders()
    {
        var firstFolder = new Folder { Path = @"C:\Music\A", Name = "A" };
        var secondFolder = new Folder { Path = @"C:\Music\B", Name = "B" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.AddRange(firstFolder, secondFolder);
            await context.SaveChangesAsync();
        }

        var firstPath = firstFolder.Path + @"\first.mp3";
        var secondPath = secondFolder.Path + @"\second.mp3";
        _fileSystem.DirectoryExists(Arg.Any<string>()).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(firstFolder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (firstPath, DateTime.UtcNow) });
        _fileSystem.EnumerateFilesWithLastWriteTime(secondFolder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (secondPath, DateTime.UtcNow) });

        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstMetadata = new TaskCompletionSource<SongFileMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementMetadata = new TaskCompletionSource<SongFileMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        var extractionCount = 0;
        _metadataService.ExtractMetadataAsync(firstPath, firstFolder.Path).Returns(_ =>
        {
            if (Interlocked.Increment(ref extractionCount) == 1)
            {
                firstStarted.SetResult();
                return firstMetadata.Task;
            }
            replacementStarted.SetResult();
            return replacementMetadata.Task;
        });
        _metadataService.ExtractMetadataAsync(secondPath, secondFolder.Path)
            .Returns(new SongFileMetadata { FilePath = secondPath, Title = "Second" });
        _settingsService.Set(CoreSettings.VolumeNormalization, true);
        var songCountsAtAnalysis = new List<int>();
        _replayGainService.ScanLibraryAsync(Arg.Any<IProgress<ScanProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await using var context = _dbHelper.ContextFactory.CreateDbContext();
                songCountsAtAnalysis.Add(await context.Songs.CountAsync());
            });

        var firstRefresh = _library.Folders.RefreshAllFoldersAsync();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var replacementRefresh = _library.Folders.RefreshAllFoldersAsync();
        firstMetadata.SetResult(new SongFileMetadata { FilePath = firstPath, Title = "First" });
        (await firstRefresh.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
        await replacementStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        replacementMetadata.SetResult(new SongFileMetadata { FilePath = firstPath, Title = "First" });

        (await replacementRefresh.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        songCountsAtAnalysis.Should().Equal(2);
    }

    [Fact]
    public async Task RemoveFolderAsync_CleansEmptyBranchesAndPreservesOccupiedAncestors()
    {
        var removed = new Folder { Name = "Removed", Path = @"D:\Removed" };
        var root = new Folder { Name = "Music", Path = @"C:\Music" };
        var artist = new Folder { Name = "École", Path = @"C:\Music\École", ParentFolder = root };
        var album = new Folder { Name = "Album", Path = @"C:\Music\École\Album", ParentFolder = artist };
        var empty = new Folder { Name = "Empty", Path = @"C:\Music\Empty", ParentFolder = root };
        var leaf = new Folder { Name = "Leaf", Path = @"C:\Music\Empty\Leaf", ParentFolder = empty };
        var similarPrefix = new Folder { Name = "Écolerie", Path = @"C:\Music\Écolerie", ParentFolder = root };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.AddRange(removed, album, leaf, similarPrefix);
            context.Songs.Add(new Song
            {
                Folder = root,
                FilePath = @"C:\Music\école\Album\song.mp3",
                DirectoryPath = @"C:\Music\école\Album\"
            });
            context.Albums.Add(new Album { Title = "Empty album" });
            context.Genres.Add(new Genre { Name = "Empty genre" });
            await context.SaveChangesAsync();
        }

        (await _library.Folders.RemoveFolderAsync(removed.Id)).Should().BeTrue();

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Folders.Select(f => f.Id).ToListAsync()).Should().BeEquivalentTo(new[] { root.Id, artist.Id, album.Id });
        (await assertContext.Albums.CountAsync()).Should().Be(0);
        (await assertContext.Genres.CountAsync()).Should().Be(0);
        (await assertContext.Songs.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveFolderAsync_CleansLocalArtistImageAfterRemovingOrphan(bool imageLocked)
    {
        var folder = new Folder { Path = @"C:\Music" };
        var artist = new Artist { Name = "Orphan" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            await context.SaveChangesAsync();
        }
        var imagePath = Path.Combine(_pathConfig.ArtistImageCachePath, $"{artist.Id}.local.jpg");
        _fileSystem.DirectoryExists(_pathConfig.ArtistImageCachePath).Returns(true);
        _fileSystem.GetFiles(_pathConfig.ArtistImageCachePath, $"{artist.Id}.*").Returns([imagePath]);
        if (imageLocked)
            _fileSystem.When(fs => fs.DeleteFile(imagePath)).Do(_ => throw new IOException("File is locked"));

        (await _library.Folders.RemoveFolderAsync(folder.Id)).Should().BeTrue();

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Artists.CountAsync()).Should().Be(0);
        _fileSystem.Received(1).DeleteFile(imagePath);
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_CreatesIntermediateFoldersAndRepairsExistingParent()
    {
        var root = new Folder { Name = "Music", Path = @"C:\Music" };
        var album = new Folder { Name = "Album", Path = @"C:\Music\Artist\Album", ParentFolder = root };
        var filePath = @"C:\Music\Artist\Album\song.mp3";
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(album);
            await context.SaveChangesAsync();
        }
        _fileSystem.DirectoryExists(root.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(root.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (filePath, DateTime.UtcNow) });
        _metadataService.ExtractMetadataAsync(filePath, root.Path).Returns(new SongFileMetadata
        {
            FilePath = filePath,
            Title = "Song",
            Artists = ["Artist"],
            Album = "Album"
        });

        await _library.Folders.RescanFolderForMusicAsync(root.Id);

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var artistFolder = await assertContext.Folders.SingleAsync(f => f.Path == @"C:\Music\Artist");
        artistFolder.ParentFolderId.Should().Be(root.Id);
        (await assertContext.Folders.FindAsync(album.Id))!.ParentFolderId.Should().Be(artistFolder.Id);
        (await assertContext.Folders.CountAsync()).Should().Be(3);
    }

    [Theory]
    [InlineData(@"C:\Music\Vol. 1", "Vol. 1")]
    [InlineData(@"C:\", @"C:\")]
    public async Task AddFolderAsync_WithValidNewPath_PreservesFolderName(string folderPath, string expectedName)
    {
        var lastWriteTime = DateTime.UtcNow;
        _fileSystem.GetLastWriteTimeUtc(folderPath).Returns(lastWriteTime);

        var result = await _library.Folders.AddFolderAsync(folderPath);

        result.Should().NotBeNull();
        result!.Path.Should().Be(folderPath);
        result.Name.Should().Be(expectedName);
        result.LastModifiedDate.Should().Be(lastWriteTime);

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Folders.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AddFolderAsync_WithExistingPath_ReturnsExistingFolderWithoutAdding()
    {
        const string folderPath = "C:\\Music\\Pop";
        var existingFolder = new Folder { Path = folderPath, Name = "Pop" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(existingFolder);
            await context.SaveChangesAsync();
        }

        var result = await _library.Folders.AddFolderAsync(folderPath);

        result.Should().NotBeNull();
        result!.Id.Should().Be(existingFolder.Id);

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Folders.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("C:\\Music\\Pop\\80s")]
    [InlineData("C:\\Music")]
    public async Task AddFolderAsync_OverlappingAnExistingRoot_ThrowsAndAddsNothing(string folderPath)
    {
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(new Folder { Path = "C:\\Music\\Pop", Name = "Pop" });
            await context.SaveChangesAsync();
        }

        var add = () => _library.Folders.AddFolderAsync(folderPath);

        await add.Should().ThrowAsync<FolderOverlapsException>();
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Folders.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RemoveFolderAsync_WithExistingFolder_RemovesFolderAndAssociatedData()
    {
        var folder = new Folder { Path = "C:\\Music\\Jazz", Name = "Jazz" };
        var artist = new Artist { Name = "Jazz Artist" };
        var album = new Album { Title = "Jazz Album" };
        album.AlbumArtists.Add(new AlbumArtist { Artist = artist, Order = 0 });
        var song = new Song
        {
            FilePath = "C:\\Music\\Jazz\\track1.mp3",
            Title = "Jazz Track",
            Folder = folder,
            Album = album,
            AlbumArtUriFromTrack = "C:\\cache\\albumart\\art1.jpg",
            LrcFilePath = "C:\\cache\\lrc\\lyrics1.lrc"
        };
        song.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
        album.SyncDenormalizedFields();
        song.SyncDenormalizedFields();
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }

        _fileSystem.FileExists("C:\\cache\\albumart\\art1.jpg").Returns(true);
        _fileSystem.FileExists("C:\\cache\\lrc\\lyrics1.lrc").Returns(true);
        _pathConfig.LrcCachePath.Returns("C:\\cache\\lrc");

        var result = await _library.Folders.RemoveFolderAsync(folder.Id);

        result.Should().BeTrue();

        await using (var assertContext = _dbHelper.ContextFactory.CreateDbContext())
        {
            (await assertContext.Folders.CountAsync()).Should().Be(0);
            (await assertContext.Songs.CountAsync()).Should().Be(0);
            (await assertContext.Albums.CountAsync()).Should().Be(0);
            (await assertContext.Artists.CountAsync()).Should().Be(0);
        }

        _fileSystem.Received(1).DeleteFile("C:\\cache\\albumart\\art1.jpg");
        _fileSystem.Received(1).DeleteFile("C:\\cache\\lrc\\lyrics1.lrc");
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_WithMixedChanges_CorrectlyUpdatesLibrary()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\Scan", Name = "Scan" };
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Artist" };
        var existingSongUnchanged = new Song
        {
            FilePath = "C:\\Music\\Scan\\unchanged.mp3",
            FileModifiedDate = new DateTime(2023, 1, 1),
            FolderId = folder.Id
        };
        existingSongUnchanged.SongArtists.Add(new SongArtist { ArtistId = artist.Id, Order = 0 });
        var existingSongToUpdate = new Song
        {
            FilePath = "C:\\Music\\Scan\\updated.mp3",
            FileModifiedDate = new DateTime(2023, 1, 1),
            FolderId = folder.Id
        };
        existingSongToUpdate.SongArtists.Add(new SongArtist { ArtistId = artist.Id, Order = 0 });
        var existingSongToDelete = new Song
        {
            FilePath = "C:\\Music\\Scan\\deleted.mp3",
            FileModifiedDate = new DateTime(2023, 1, 1),
            FolderId = folder.Id
        };
        existingSongToDelete.SongArtists.Add(new SongArtist { ArtistId = artist.Id, Order = 0 });
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.Add(artist);
            context.Songs.AddRange(existingSongUnchanged, existingSongToUpdate, existingSongToDelete);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories).Returns(new[]
        {
            ("C:\\Music\\Scan\\unchanged.mp3", new DateTime(2023, 1, 1)),
            ("C:\\Music\\Scan\\updated.mp3",   new DateTime(2023, 2, 2)),
            ("C:\\Music\\Scan\\new.mp3",       new DateTime(2023, 3, 3)),
        });

        _metadataService.ExtractMetadataAsync("C:\\Music\\Scan\\updated.mp3", Arg.Any<string?>())
            .Returns(new SongFileMetadata
            { FilePath = "C:\\Music\\Scan\\updated.mp3", Title = "Updated Song", Artists = new List<string> { "Artist" } });
        _metadataService.ExtractMetadataAsync("C:\\Music\\Scan\\new.mp3", Arg.Any<string?>())
            .Returns(new SongFileMetadata
            { FilePath = "C:\\Music\\Scan\\new.mp3", Title = "New Song", Artists = new List<string> { "Artist" } });

        var result = await _library.Folders.RescanFolderForMusicAsync(folder.Id);

        result.Should().BeTrue();

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var songs = await assertContext.Songs.ToListAsync();
        songs.Should().HaveCount(3);
        songs.Should().Contain(s => s.FilePath == "C:\\Music\\Scan\\unchanged.mp3");
        songs.Should().Contain(s => s.FilePath == "C:\\Music\\Scan\\updated.mp3" && s.Title == "Updated Song");
        songs.Should().Contain(s => s.FilePath == "C:\\Music\\Scan\\new.mp3" && s.Title == "New Song");
        songs.Should().NotContain(s => s.FilePath == "C:\\Music\\Scan\\deleted.mp3");
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_WhenFolderPathNoLongerExists_RemovesFolderFromLibrary()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\DeletedFolder", Name = "Deleted" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(false);

        var result = await _library.Folders.RescanFolderForMusicAsync(folder.Id);

        result.Should().BeTrue();
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Folders.CountAsync()).Should().Be(0);
    }

    // A drive that isn't connected must not cost the library its songs.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticRefresh_WhenFolderPathIsMissing_KeepsTheFolderAndItsSongs(bool background)
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "Z:\\Offline", Name = "Offline" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Songs.Add(new Song { FilePath = "Z:\\Offline\\song.mp3", FolderId = folder.Id });
            await context.SaveChangesAsync();
        }
        _fileSystem.DirectoryExists(folder.Path).Returns(false);

        var changed = background
            ? await _library.Folders.RefreshFolderInBackgroundAsync(folder.Id)
            : await _library.Folders.RefreshAllFoldersAsync();

        changed.Should().BeFalse();
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Folders.CountAsync()).Should().Be(1);
        (await assertContext.Songs.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_WhenCancelled_StopsProcessingAndReturnsFalse()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\Scan", Name = "Scan" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(new[] { ("C:\\Music\\Scan\\file1.mp3", DateTime.UtcNow) });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _library.Folders.RescanFolderForMusicAsync(folder.Id, cancellationToken: cts.Token);

        result.Should().BeFalse();
        await _metadataService.DidNotReceive().ExtractMetadataAsync(Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_GivesUnchangedSongsTheLyricsAndCoverFilesAddedBesideThem()
    {
        var lastWriteTime = DateTime.UtcNow;
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\Scan", Name = "Scan" };
        var song = new Song
        {
            Id = Guid.NewGuid(),
            FilePath = "C:\\Music\\Scan\\song.mp3",
            FolderId = folder.Id,
            FileModifiedDate = lastWriteTime
        };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (song.FilePath, lastWriteTime) });

        var lyricsPath = "C:\\Music\\Scan\\song.txt";
        _fileSystem.GetFiles("C:\\Music\\Scan", "*.lrc").Returns(Array.Empty<string>());
        _fileSystem.GetFiles("C:\\Music\\Scan", "*.txt").Returns(new[] { lyricsPath });
        var coverPath = "C:\\Music\\Scan\\cover.jpg";
        _fileSystem.GetFiles("C:\\Music\\Scan", "*.*").Returns(new[] { coverPath });
        _fileSystem.ReadAllBytesAsync(coverPath).Returns(new byte[] { 1, 2, 3 });
        _imageProcessor.SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>())
            .Returns(("C:\\cache\\albumart\\cover.jpg", "light", "dark"));
        _fileSystem.FileExists("C:\\cache\\albumart\\cover.jpg").Returns(true);

        var result = await _library.Folders.RescanFolderForMusicAsync(folder.Id);

        result.Should().BeTrue();
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var updatedSong = await assertContext.Songs.FindAsync(song.Id);
        updatedSong.Should().NotBeNull();
        updatedSong!.LrcFilePath.Should().Be(lyricsPath);
        updatedSong.AlbumArtUriFromTrack.Should().Be("C:\\cache\\albumart\\cover.jpg");
        await _metadataService.DidNotReceive().ExtractMetadataAsync(Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_RereadsAnUnchangedSongWhoseLyricsFileWasDeleted()
    {
        var lastWriteTime = DateTime.UtcNow;
        var folder = new Folder { Id = Guid.NewGuid(), Path = @"C:\Music\Scan", Name = "Scan" };
        const string songPath = @"C:\Music\Scan\song.mp3";
        var song = new Song
        {
            Id = Guid.NewGuid(),
            FilePath = songPath,
            FolderId = folder.Id,
            FileModifiedDate = lastWriteTime,
            LrcFilePath = @"C:\Music\Scan\song.ttml"
        };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (songPath, lastWriteTime) });
        const string embeddedLyricsPath = @"C:\cache\lrc\embedded.lrc";
        _metadataService.ExtractMetadataAsync(songPath, folder.Path).Returns(new SongFileMetadata
        {
            FilePath = songPath,
            FileModifiedDate = lastWriteTime,
            Title = "Song",
            Artists = ["Artist"],
            AlbumArtists = ["Artist"],
            LrcFilePath = embeddedLyricsPath
        });

        await _library.Folders.RescanFolderForMusicAsync(folder.Id);

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Songs.SingleAsync()).LrcFilePath.Should().Be(embeddedLyricsPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RescanFolderForMusicAsync_SidecarAddedBesideAnUnchangedSongReplacesCachedLyricsButNotTheUsersEdit(
        bool isOverride)
    {
        const string cacheRoot = @"C:\cache\lrc";
        _pathConfig.LrcCachePath.Returns(cacheRoot);
        var lastWriteTime = DateTime.UtcNow;
        var folder = new Folder { Id = Guid.NewGuid(), Path = @"C:\Music\Scan", Name = "Scan" };
        const string songPath = @"C:\Music\Scan\song.mp3";
        var lrcPath = isOverride
            ? LrcCache.OverridePath(cacheRoot, songPath)
            : LrcCache.CachedPath(cacheRoot, songPath, "Artist", "Album", "Song");
        var song = new Song
        {
            Id = Guid.NewGuid(),
            FilePath = songPath,
            FolderId = folder.Id,
            FileModifiedDate = lastWriteTime,
            LrcFilePath = lrcPath
        };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (songPath, lastWriteTime) });
        const string sidecarPath = @"C:\Music\Scan\song.ttml";
        _fileSystem.GetFiles(folder.Path, "*.ttml").Returns([sidecarPath]);

        await _library.Folders.RescanFolderForMusicAsync(folder.Id);

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Songs.FindAsync(song.Id))!.LrcFilePath.Should().Be(isOverride ? lrcPath : sidecarPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeduplicateLibraryAsync_PreservesUserDataAndRollsBackFailedMerges(bool failDuringSongMerge)
    {
        const string alias = @"Z:\Music";
        const string canonical = @"\\server\share\Music";
        var aliasRoot = new Folder { Path = alias, Name = "Music", LastModifiedDate = DateTime.UtcNow.AddDays(-2) };
        var root = new Folder { Path = canonical, Name = "Music", LastModifiedDate = DateTime.UtcNow };
        var aliasChild = new Folder { Path = alias + @"\Album", Name = "Album", ParentFolder = aliasRoot, LastModifiedDate = aliasRoot.LastModifiedDate };
        var child = new Folder { Path = canonical + @"\Album", Name = "Album", ParentFolder = root, LastModifiedDate = root.LastModifiedDate };
        var first = new Song
        {
            FilePath = aliasChild.Path + @"\song.mp3",
            DirectoryPath = aliasChild.Path,
            Folder = aliasChild,
            PlayCount = 5,
            SkipCount = 2,
            TotalListenTimeTicks = 100,
            DateAddedToLibrary = DateTime.UtcNow.AddDays(-10)
        };
        var second = new Song
        {
            FilePath = child.Path + @"\song.mp3",
            DirectoryPath = child.Path,
            Folder = child,
            PlayCount = 3,
            SkipCount = 1,
            TotalListenTimeTicks = 50,
            IsLoved = true,
            Rating = 4,
            LastPlayedDate = DateTime.UtcNow
        };
        var shared = new Playlist { Name = "Shared" };
        var duplicateOnly = new Playlist { Name = "Duplicate only" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.AddRange(first, second);
            context.ListenHistory.AddRange(new ListenHistory { Song = first },
                new ListenHistory { Song = second, ContextType = PlaybackContextType.Folder, ContextId = child.Id });
            context.PlaylistSongs.AddRange(
                new PlaylistSong { Playlist = shared, Song = first, Order = 5 },
                new PlaylistSong { Playlist = shared, Song = second, Order = 2 },
                new PlaylistSong { Playlist = duplicateOnly, Song = second, Order = 7 });
            await context.SaveChangesAsync();
        }
        _fileSystem.NormalizePath(Arg.Any<string>()).Returns(call =>
        {
            var path = call.Arg<string>();
            if (failDuringSongMerge && path.EndsWith(".mp3")) throw new IOException("Normalization failed");
            return path.Replace(alias, canonical, StringComparison.OrdinalIgnoreCase);
        });
        var notifications = 0;
        _library.Notifier.Changed += (_, _) => notifications++;

        if (failDuringSongMerge)
        {
            Func<Task> merge = () => _library.Folders.DeduplicateLibraryAsync();
            await merge.Should().ThrowAsync<IOException>();
            await using var context = _dbHelper.ContextFactory.CreateDbContext();
            (await context.Folders.CountAsync()).Should().Be(4);
            (await context.Songs.CountAsync()).Should().Be(2);
            (await context.PlaylistSongs.CountAsync()).Should().Be(3);
            (await context.Songs.FindAsync(second.Id))!.FolderId.Should().Be(child.Id);
            notifications.Should().Be(0);
            failDuringSongMerge = false;
        }

        (await _library.Folders.DeduplicateLibraryAsync()).Should().Be(3);
        (await _library.Folders.DeduplicateLibraryAsync()).Should().Be(0);
        notifications.Should().Be(1);
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var survivor = await assertContext.Songs.SingleAsync();
        survivor.Id.Should().Be(first.Id);
        survivor.FilePath.Should().Be(second.FilePath);
        survivor.DirectoryPath.Should().Be(child.Path);
        survivor.FolderId.Should().Be(aliasChild.Id);
        survivor.PlayCount.Should().Be(8);
        survivor.SkipCount.Should().Be(3);
        survivor.TotalListenTimeTicks.Should().Be(150);
        survivor.IsLoved.Should().BeTrue();
        survivor.Rating.Should().Be(4);
        survivor.DateAddedToLibrary.Should().Be(first.DateAddedToLibrary);
        survivor.LastPlayedDate.Should().Be(second.LastPlayedDate);
        (await assertContext.Folders.CountAsync()).Should().Be(2);
        (await assertContext.Folders.FindAsync(aliasChild.Id))!.ParentFolderId.Should().Be(aliasRoot.Id);
        (await assertContext.ListenHistory.Select(history => history.SongId).ToListAsync()).Should().Equal(first.Id, first.Id);
        (await assertContext.ListenHistory.SingleAsync(history => history.ContextType == PlaybackContextType.Folder))
            .ContextId.Should().Be(aliasChild.Id);
        var entries = await assertContext.PlaylistSongs.ToListAsync();
        entries.Should().HaveCount(2).And.OnlyContain(entry => entry.SongId == first.Id);
        entries.Single(entry => entry.PlaylistId == shared.Id).Order.Should().Be(2);
        entries.Single(entry => entry.PlaylistId == duplicateOnly.Id).Order.Should().Be(7);
    }

    [Theory]
    [InlineData(@"C:\Users\Music\song.lrc")]
    [InlineData(@"C:\cache\lrc-backup\song.lrc")]
    [InlineData(@"C:\cache\lrc\..\song.lrc")]
    public async Task RemoveFolderAsync_WhenSongHasLrcFileOutsideCache_DoesNotDeleteLrcFile(string userLrcPath)
    {
        _fileSystem.FileExists(userLrcPath).Returns(true);

        var folder = new Folder { Name = "F", Path = "C:\\Music" };
        var song = new Song { Title = "S", Folder = folder, FilePath = "C:\\Music\\s.mp3", LrcFilePath = userLrcPath };
        await using (var ctx = _dbHelper.ContextFactory.CreateDbContext())
        {
            ctx.Folders.Add(folder);
            ctx.Songs.Add(song);
            await ctx.SaveChangesAsync();
        }

        await _library.Folders.RemoveFolderAsync(folder.Id);

        _fileSystem.DidNotReceive().DeleteFile(userLrcPath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemovingLibrarySongs_PreservesSharedAndExternalCoverArt(bool removeFolder)
    {
        var folder = new Folder { Path = @"C:\Music", Name = "Music" };
        var otherFolder = new Folder { Path = @"C:\Other", Name = "Other" };
        var sharedSongArt = @"C:\cache\albumart\shared-song.jpg";
        var sharedAlbumArt = @"C:\cache\albumart\shared-album.jpg";
        var orphanArt = @"C:\cache\albumart\orphan.jpg";
        var externalArt = @"C:\Music\cover.jpg";
        var remainingAlbum = new Album { Title = "Remaining", CoverArtUri = sharedAlbumArt };
        var removedAlbum = new Album { Title = "Removed", CoverArtUri = orphanArt };

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            foreach (var art in new[] { sharedSongArt, sharedAlbumArt, orphanArt, externalArt })
                context.Songs.Add(new Song
                {
                    Folder = folder,
                    FilePath = Path.Combine(folder.Path, Path.GetFileName(art) + ".mp3"),
                    DirectoryPath = folder.Path,
                    AlbumArtUriFromTrack = art,
                    Album = removedAlbum
                });
            context.Songs.Add(new Song
            {
                Folder = otherFolder,
                FilePath = @"C:\Other\remaining.mp3",
                DirectoryPath = otherFolder.Path,
                Album = remainingAlbum,
                AlbumArtUriFromTrack = sharedSongArt
            });
            await context.SaveChangesAsync();
        }
        _fileSystem.FileExists(Arg.Any<string>()).Returns(true);
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(Array.Empty<(string, DateTime)>());

        var changed = removeFolder
            ? await _library.Folders.RemoveFolderAsync(folder.Id)
            : await _library.Folders.RescanFolderForMusicAsync(folder.Id);

        changed.Should().BeTrue();
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Songs.CountAsync()).Should().Be(1);
        _fileSystem.Received(1).DeleteFile(orphanArt);
        _fileSystem.DidNotReceive().DeleteFile(sharedSongArt);
        _fileSystem.DidNotReceive().DeleteFile(sharedAlbumArt);
        _fileSystem.DidNotReceive().DeleteFile(externalArt);
    }

    [Fact]
    public async Task Rescan_GivesTracksWithoutArtTheirAlbumCoverExceptInUnknownAlbum()
    {
        var timestamp = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        const string albumCover = @"C:\cache\albumart\hash.aaaaaa.bbbbbb.fetched.jpg";
        var folder = new Folder { Path = @"C:\Music", Name = "Music" };
        var album = new Album { Title = "Album", CoverArtUri = albumCover };
        var unknownAlbum = new Album { Title = Album.UnknownAlbumName, CoverArtUri = albumCover };
        Song Track(string name, Album trackAlbum, string? art = null) => new()
        {
            Folder = folder,
            FilePath = $@"C:\Music\{name}.mp3",
            DirectoryPath = folder.Path,
            FileModifiedDate = timestamp,
            Album = trackAlbum,
            AlbumArtUriFromTrack = art,
            LightSwatchId = art is null ? null : "aaaaaa"
        };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.AddRange(Track("with-art", album, albumCover), Track("without-art", album), Track("unknown", unknownAlbum));
            await context.SaveChangesAsync();
        }
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.FileExists(albumCover).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories).Returns(new[]
        {
            (@"C:\Music\with-art.mp3", timestamp), (@"C:\Music\without-art.mp3", timestamp), (@"C:\Music\unknown.mp3", timestamp)
        });

        (await _library.Folders.RescanFolderForMusicAsync(folder.Id)).Should().BeTrue();

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var songs = await assertContext.Songs.ToDictionaryAsync(s => Path.GetFileNameWithoutExtension(s.FilePath));
        songs["without-art"].AlbumArtUriFromTrack.Should().Be(albumCover);
        songs["without-art"].LightSwatchId.Should().Be("aaaaaa");
        songs["unknown"].AlbumArtUriFromTrack.Should().BeNull();
        await _imageProcessor.DidNotReceive().SaveCoverArtAndExtractColorsAsync(Arg.Any<byte[]>());
    }

    [Fact]
    public async Task Rescan_KeepsAlbumCoversInStepWithTheirTracks()
    {
        var timestamp = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        const string oldCover = @"C:\cache\albumart\old.jpg";
        const string newCover = @"C:\cache\albumart\new.jpg";
        var folder = new Folder { Path = @"C:\Music", Name = "Music" };
        var artist = new Artist { Name = "Artist" };
        var albums = new Dictionary<string, Album>();
        Song Track(string albumTitle, int trackNumber, string? albumCover = oldCover, string? art = oldCover)
        {
            if (!albums.TryGetValue(albumTitle, out var album))
            {
                album = new Album { Title = albumTitle, CoverArtUri = albumCover };
                album.AlbumArtists.Add(new AlbumArtist { Artist = artist, Order = 0 });
                albums.Add(albumTitle, album);
            }
            var song = new Song
            {
                Folder = folder,
                FilePath = $@"C:\Music\{albumTitle}{trackNumber}.mp3",
                DirectoryPath = folder.Path,
                FileModifiedDate = timestamp,
                Album = album,
                TrackNumber = trackNumber,
                AlbumArtUriFromTrack = art
            };
            song.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
            return song;
        }
        var retagged = new[] { Track("Retagged", 1), Track("Retagged", 2) };
        var bonus = new[] { Track("Bonus", 1), Track("Bonus", 2) };
        // Tracks without art of their own show the album's online cover.
        var online = new[] { Track("Online", 1), Track("Online", 2) };
        var uncovered = Track("Uncovered", 1, albumCover: null, art: newCover);
        var changed = retagged.Append(bonus[1]).Concat(online).ToArray();
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.AddRange(retagged.Concat(bonus).Concat(online).Append(uncovered));
            await context.SaveChangesAsync();
        }
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.FileExists(Arg.Any<string>()).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(retagged.Concat(bonus).Concat(online).Append(uncovered)
                .Select(s => (s.FilePath, changed.Contains(s) ? timestamp.AddDays(1) : timestamp)).ToArray());
        foreach (var song in changed)
            _metadataService.ExtractMetadataAsync(song.FilePath, folder.Path, true).Returns(new SongFileMetadata
            {
                FilePath = song.FilePath,
                FileModifiedDate = timestamp.AddDays(1),
                Title = song.FilePath,
                Album = song.Album!.Title,
                TrackNumber = song.TrackNumber,
                Artists = ["Artist"],
                AlbumArtists = ["Artist"],
                CoverArtUri = online.Contains(song) ? null : newCover
            });

        (await _library.Folders.RescanFolderForMusicAsync(folder.Id)).Should().BeTrue();

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var covers = await assertContext.Albums.ToDictionaryAsync(a => a.Title, a => a.CoverArtUri);
        covers["Retagged"].Should().Be(newCover);
        covers["Bonus"].Should().Be(oldCover, because: "another track still uses the album's cover");
        covers["Online"].Should().Be(oldCover, because: "re-reading tracks without art doesn't make the cover stale");
        covers["Uncovered"].Should().Be(newCover);
        (await assertContext.Songs.Where(s => s.Album!.Title == "Online").Select(s => s.AlbumArtUriFromTrack).ToListAsync())
            .Should().AllBe(oldCover);
    }

    [Fact]
    public async Task Rescan_RebuildsCoversWhoseCachedImageIsMissing()
    {
        var timestamp = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        const string embeddedCover = @"C:\cache\albumart\embedded.jpg";
        const string onlineCover = @"C:\cache\albumart\online.jpg";
        const string splitCover = @"C:\cache\albumart\split.jpg";
        var folder = new Folder { Path = @"C:\Music", Name = "Music" };
        var otherFolder = new Folder { Path = @"C:\Other", Name = "Other" };
        var artist = new Artist { Name = "Artist" };
        Song Track(string title, string cover)
        {
            var album = new Album { Title = title, CoverArtUri = cover, CoverArtLastCheckedUtc = timestamp };
            album.AlbumArtists.Add(new AlbumArtist { Artist = artist, Order = 0 });
            var song = new Song
            {
                Folder = folder,
                FilePath = $@"C:\Music\{title}.mp3",
                DirectoryPath = folder.Path,
                FileModifiedDate = timestamp,
                Album = album,
                AlbumArtUriFromTrack = cover
            };
            song.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
            return song;
        }
        SongFileMetadata Metadata(string title, string? cover) => new()
        {
            FilePath = $@"C:\Music\{title}.mp3",
            FileModifiedDate = timestamp,
            Title = title,
            Album = title,
            Artists = ["Artist"],
            AlbumArtists = ["Artist"],
            CoverArtUri = cover
        };
        var split = Track("Split", splitCover);
        // The split album's cover is embedded in a track of a folder this scan doesn't cover.
        var otherFolderTrack = new Song
        {
            Folder = otherFolder,
            FilePath = @"C:\Other\SplitOther.mp3",
            DirectoryPath = otherFolder.Path,
            FileModifiedDate = timestamp,
            Album = split.Album,
            AlbumArtUriFromTrack = splitCover
        };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.AddRange(Track("Embedded", embeddedCover), Track("Online", onlineCover), split, otherFolderTrack);
            await context.SaveChangesAsync();
        }
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories).Returns(new[]
        {
            (@"C:\Music\Embedded.mp3", timestamp), (@"C:\Music\Online.mp3", timestamp), (@"C:\Music\Split.mp3", timestamp)
        });
        _metadataService.ExtractMetadataAsync(@"C:\Music\Split.mp3", folder.Path, true).Returns(Metadata("Split", null));
        // Both covers are missing. Re-reading the embedded art recreates its cover; the online one only comes back online.
        var embeddedCoverRecreated = false;
        _fileSystem.FileExists(embeddedCover).Returns(_ => embeddedCoverRecreated);
        _metadataService.ExtractMetadataAsync(@"C:\Music\Embedded.mp3", folder.Path, true).Returns(_ =>
        {
            embeddedCoverRecreated = true;
            return Metadata("Embedded", embeddedCover);
        });
        _metadataService.ExtractMetadataAsync(@"C:\Music\Online.mp3", folder.Path, true)
            .Returns(Metadata("Online", null));

        (await _library.Folders.RescanFolderForMusicAsync(folder.Id)).Should().BeTrue();

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var songs = await assertContext.Songs.Include(s => s.Album)
            .ToDictionaryAsync(s => Path.GetFileNameWithoutExtension(s.FilePath));
        songs["Embedded"].AlbumArtUriFromTrack.Should().Be(embeddedCover);
        songs["Embedded"].Album!.CoverArtUri.Should().Be(embeddedCover);
        songs["Online"].AlbumArtUriFromTrack.Should().BeNull();
        songs["Online"].Album!.CoverArtUri.Should().BeNull();
        songs["Online"].Album!.CoverArtLastCheckedUtc.Should().BeNull();
        songs["Split"].Album!.CoverArtUri.Should().Be(splitCover, because: "scanning the other folder recreates it");
    }

    [Fact]
    public async Task Rescan_DeletesReplacedCoversThatNoSongStillUses()
    {
        var timestamp = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        const string orphanedCover = @"C:\cache\albumart\orphaned.jpg";
        const string sharedCover = @"C:\cache\albumart\shared.jpg";
        const string newCover = @"C:\cache\albumart\new.jpg";
        const string staleAlbumCover = @"C:\cache\albumart\stale.jpg";
        var folder = new Folder { Path = @"C:\Music", Name = "Music" };
        Song Track(string name, string cover, Album? album = null) => new()
        {
            Folder = folder,
            FilePath = $@"C:\Music\{name}.mp3",
            DirectoryPath = folder.Path,
            FileModifiedDate = timestamp,
            AlbumArtUriFromTrack = cover,
            Album = album
        };
        var staleAlbum = new Album { Title = "Stale", CoverArtUri = staleAlbumCover };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Songs.AddRange(Track("first", orphanedCover), Track("second", sharedCover),
                Track("unchanged", sharedCover, staleAlbum));
            await context.SaveChangesAsync();
        }
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.FileExists(Arg.Any<string>()).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories).Returns(new[]
        {
            (@"C:\Music\first.mp3", timestamp.AddDays(1)), (@"C:\Music\second.mp3", timestamp.AddDays(1)),
            (@"C:\Music\unchanged.mp3", timestamp)
        });
        foreach (var name in new[] { "first", "second" })
            _metadataService.ExtractMetadataAsync($@"C:\Music\{name}.mp3", folder.Path, true).Returns(new SongFileMetadata
            {
                FilePath = $@"C:\Music\{name}.mp3",
                FileModifiedDate = timestamp.AddDays(1),
                Title = name,
                Artists = ["Artist"],
                CoverArtUri = newCover
            });

        (await _library.Folders.RescanFolderForMusicAsync(folder.Id)).Should().BeTrue();

        _fileSystem.Received(1).DeleteFile(orphanedCover);
        _fileSystem.Received(1).DeleteFile(staleAlbumCover);
        _fileSystem.DidNotReceive().DeleteFile(sharedCover);
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_WithChangedArtists_UpdatesRelationshipsAndCleansUpOrphans()
    {
        var folderId = Guid.NewGuid();
        var folder = new Folder { Id = folderId, Path = "C:\\Music\\Scan", Name = "Scan" };

        var mainArtist = new Artist { Name = "Main Artist" };
        var legacyArtist = new Artist { Name = "Legacy Artist" };

        var album = new Album { Title = "Test Album" };
        album.AlbumArtists.Add(new AlbumArtist { Artist = mainArtist, Order = 0 });

        var song = new Song
        {
            Title = "Test Song",
            FilePath = "C:\\Music\\Scan\\song.mp3",
            DirectoryPath = "C:\\Music\\Scan",
            FolderId = folderId,
            Album = album,
            FileModifiedDate = DateTime.UtcNow.AddDays(-1)
        };
        song.SongArtists.Add(new SongArtist { Artist = mainArtist, Order = 0 });
        song.SongArtists.Add(new SongArtist { Artist = legacyArtist, Order = 1 });

        album.SyncDenormalizedFields();
        song.SyncDenormalizedFields();

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Artists.AddRange(mainArtist, legacyArtist);
            context.Albums.Add(album);
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { ("C:\\Music\\Scan\\song.mp3", DateTime.UtcNow) });

        var collaboratorName = "Collaborator";
        _metadataService.ExtractMetadataAsync("C:\\Music\\Scan\\song.mp3", Arg.Any<string?>())
            .Returns(new SongFileMetadata
            {
                FilePath = "C:\\Music\\Scan\\song.mp3",
                Title = "Test Song",
                Album = "Test Album",
                Artists = new List<string> { "Main Artist", collaboratorName },
                AlbumArtists = new List<string> { "Main Artist", collaboratorName },
                FileModifiedDate = DateTime.UtcNow
            });

        var result = await _library.Folders.RescanFolderForMusicAsync(folderId);

        result.Should().BeTrue();
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var dbAlbum = await context.Albums
                .Include(a => a.AlbumArtists)
                .ThenInclude(aa => aa.Artist)
                .FirstOrDefaultAsync(a => a.Title == "Test Album");

            dbAlbum.Should().NotBeNull();
            dbAlbum!.Id.Should().Be(album.Id);
            dbAlbum.AlbumArtists.Should().HaveCount(2);
            dbAlbum.AlbumArtists.OrderBy(aa => aa.Order).Select(aa => aa.Artist.Name)
                .Should().ContainInOrder("Main Artist", "Collaborator");
            dbAlbum.ArtistName.Should().Be("Main Artist & Collaborator");

            var dbSong = await context.Songs
                .Include(s => s.SongArtists)
                .ThenInclude(sa => sa.Artist)
                .FirstOrDefaultAsync(s => s.FilePath == "C:\\Music\\Scan\\song.mp3");

            dbSong.Should().NotBeNull();
            dbSong.SongArtists.Should().HaveCount(2);
            dbSong.SongArtists.OrderBy(sa => sa.Order).Select(sa => sa.Artist.Name)
                .Should().ContainInOrder("Main Artist", "Collaborator");
            dbSong.ArtistName.Should().Be("Main Artist & Collaborator");

            var legacyArtistFromDb = await context.Artists.FirstOrDefaultAsync(a => a.Name == "Legacy Artist");
            legacyArtistFromDb.Should().BeNull("Legacy Artist should be deleted because it is no longer referenced");

            var collaboratorFromDb = await context.Artists.FirstOrDefaultAsync(a => a.Name == "Collaborator");
            collaboratorFromDb.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task ForceRescanMetadataAsync_PreservesMediaAssetsWhileUpdatingGenres()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\Genres", Name = "Genres" };
        var artist = new Artist { Name = "Artist" };
        var existingGenre = new Genre { Name = "Electronic" };
        var oldGenre = new Genre { Name = "Electronic\\Techno" };
        var filePath = "C:\\Music\\Genres\\song.mp3";
        var modified = DateTime.UtcNow.AddDays(-1);
        var song = new Song
        {
            Title = "Song",
            FilePath = filePath,
            DirectoryPath = folder.Path,
            FolderId = folder.Id,
            FileModifiedDate = modified,
            AlbumArtUriFromTrack = "cached-cover.jpg",
            LightSwatchId = "light",
            DarkSwatchId = "dark",
            LrcFilePath = "cached-lyrics.lrc"
        };
        song.SongArtists.Add(new SongArtist { Artist = artist, Order = 0 });
        song.Genres.Add(existingGenre);
        song.Genres.Add(oldGenre);
        song.SyncDenormalizedFields();

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.FileExists("cached-cover.jpg").Returns(true);
        _fileSystem.FileExists("cached-lyrics.lrc").Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (filePath, modified) });
        _metadataService.ExtractMetadataAsync(filePath, folder.Path, false)
            .Returns(new SongFileMetadata
            {
                FilePath = filePath,
                FileModifiedDate = modified,
                Title = "Song",
                Artists = ["Artist"],
                AlbumArtists = ["Artist"],
                Genres = ["Electronic", "Techno"]
            });

        var result = await _library.Folders.ForceRescanMetadataAsync().WaitAsync(TimeSpan.FromSeconds(5));

        result.Should().BeTrue();
        await _metadataService.Received(1).ExtractMetadataAsync(filePath, folder.Path, false);
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        var updated = await assertContext.Songs.Include(s => s.Genres).SingleAsync();
        updated.Genres.Select(g => g.Name).Should().BeEquivalentTo("Electronic", "Techno");
        updated.AlbumArtUriFromTrack.Should().Be("cached-cover.jpg");
        updated.LightSwatchId.Should().Be("light");
        updated.DarkSwatchId.Should().Be("dark");
        updated.LrcFilePath.Should().Be("cached-lyrics.lrc");
    }

    [Fact]
    public async Task ForceRescanMetadataAsync_ReadsArtForFilesThatChanged()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\Changed", Name = "Changed" };
        var filePath = "C:\\Music\\Changed\\song.mp3";
        var modified = DateTime.UtcNow.AddDays(-1);
        var song = new Song
        {
            Title = "Song",
            FilePath = filePath,
            DirectoryPath = folder.Path,
            FolderId = folder.Id,
            FileModifiedDate = modified,
            AlbumArtUriFromTrack = "old-cover.jpg"
        };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Songs.Add(song);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (filePath, modified.AddHours(1)) });
        _metadataService.ExtractMetadataAsync(filePath, folder.Path, true)
            .Returns(new SongFileMetadata
            {
                FilePath = filePath,
                FileModifiedDate = modified.AddHours(1),
                Title = "Song",
                Artists = ["Artist"],
                CoverArtUri = "new-cover.jpg"
            });

        (await _library.Folders.ForceRescanMetadataAsync().WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Songs.SingleAsync()).AlbumArtUriFromTrack.Should().Be("new-cover.jpg");
    }

    [Fact]
    public async Task ForceRescanMetadataAsync_WhenFolderIsUnavailable_ReportsFailure()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "Z:\\Offline", Name = "Offline" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }
        _fileSystem.DirectoryExists(folder.Path).Returns(false);

        var act = () => _library.Folders.ForceRescanMetadataAsync();

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Folders.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_WithUntrimmedArtistNames_DoesNotThrow()
    {
        var folderId = Guid.NewGuid();
        var folder = new Folder { Id = folderId, Path = "C:\\Music\\TrimTest", Name = "TrimTest" };

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { ("C:\\Music\\TrimTest\\song1.mp3", DateTime.UtcNow), ("C:\\Music\\TrimTest\\song2.mp3", DateTime.UtcNow) });

        _metadataService.ExtractMetadataAsync("C:\\Music\\TrimTest\\song1.mp3", Arg.Any<string?>())
            .Returns(new SongFileMetadata
            {
                FilePath = "C:\\Music\\TrimTest\\song1.mp3",
                Title = "Song 1",
                Artists = new List<string> { " Artist One " },
                AlbumArtists = new List<string> { " Artist One " }
            });

        _metadataService.ExtractMetadataAsync("C:\\Music\\TrimTest\\song2.mp3", Arg.Any<string?>())
            .Returns(new SongFileMetadata
            {
                FilePath = "C:\\Music\\TrimTest\\song2.mp3",
                Title = "Song 2",
                Artists = new List<string> { "Artist One" },
                AlbumArtists = new List<string> { "Artist One" }
            });

        var result = await _library.Folders.RescanFolderForMusicAsync(folderId);

        result.Should().BeTrue();

        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            var artists = await context.Artists.ToListAsync();
            artists.Should().HaveCount(1);
            artists[0].Name.Should().Be("Artist One");

            var songs = await context.Songs
                .Include(s => s.SongArtists)
                .ThenInclude(sa => sa.Artist)
                .ToListAsync();

            songs.Should().HaveCount(2);
            songs[0].SongArtists.First().Artist.Name.Should().Be("Artist One");
            songs[1].SongArtists.First().Artist.Name.Should().Be("Artist One");
        }
    }
}
