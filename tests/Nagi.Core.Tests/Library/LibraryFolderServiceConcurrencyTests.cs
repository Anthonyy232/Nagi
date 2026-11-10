using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Library;
using Nagi.Core.Metadata;
using Nagi.Core.Playback;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.Library;

// A file database's writers wait for each other the way the app's do; the shared in-memory one locks whole tables.
public class LibraryFolderServiceConcurrencyTests() : LibraryTestBase(useFileDatabase: true)
{
    [Fact]
    public async Task RescanFolderForMusicAsync_WhenDatabaseWriteFails_DoesNotDeadlock()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\BrokenScan", Name = "BrokenScan" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
            await context.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER RejectSongInsert BEFORE INSERT ON Songs
                BEGIN SELECT RAISE(ABORT, 'Simulated database write failure'); END;
                """);
        }

        var songFiles = Enumerable.Range(1, 200)
            .Select(i => ($"C:\\Music\\BrokenScan\\song{i}.mp3", DateTime.UtcNow))
            .ToList();
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(songFiles);
        _metadataService.ExtractMetadataAsync(Arg.Any<string>(), folder.Path)
            .Returns(call => new SongFileMetadata
            {
                FilePath = call.ArgAt<string>(0),
                Title = "Song",
                Artists = ["Artist"]
            });

        var result = await _library.Folders.RescanFolderForMusicAsync(folder.Id)
            .WaitAsync(TimeSpan.FromSeconds(5));

        result.Should().BeFalse();
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_DuringSlowExtraction_AllowsListenHistoryWrites()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\ConcurrentScan", Name = "ConcurrentScan" };
        var existingSong = new Song
        {
            Id = Guid.NewGuid(),
            FolderId = folder.Id,
            FilePath = "C:\\Music\\ConcurrentScan\\existing.mp3",
            Title = "Existing"
        };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            context.Songs.Add(existingSong);
            await context.SaveChangesAsync();
        }

        var slowFile = "C:\\Music\\ConcurrentScan\\slow.mp3";
        var files = Enumerable.Range(1, 100)
            .Select(i => ($"C:\\Music\\ConcurrentScan\\song{i}.mp3", DateTime.UtcNow))
            .Append((slowFile, DateTime.UtcNow))
            .Append((existingSong.FilePath, DateTime.UtcNow))
            .ToList();
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories).Returns(files);

        var releaseSlowExtraction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var onlySlowFileLeft = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var filesRead = 0;
        _metadataService.ExtractMetadataAsync(Arg.Any<string>(), folder.Path)
            .Returns(async call =>
            {
                var path = call.ArgAt<string>(0);
                if (path == slowFile)
                    await releaseSlowExtraction.Task;
                else if (Interlocked.Increment(ref filesRead) == files.Count - 1)
                    onlySlowFileLeft.SetResult();
                return new SongFileMetadata { FilePath = path, Title = Path.GetFileNameWithoutExtension(path) };
            });

        var scanTask = _library.Folders.RescanFolderForMusicAsync(folder.Id);
        try
        {
            await onlySlowFileLeft.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var sessionId = await _library.Listening.StartListenSessionAsync(
                existingSong.Id,
                new PlaybackContext(PlaybackContextType.Library, null));

            sessionId.Should().NotBeNull();
        }
        finally
        {
            releaseSlowExtraction.TrySetResult();
            (await scanTask.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        }
        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Songs.CountAsync()).Should().Be(102);
    }

    [Fact]
    public async Task RemoveFolderAsync_WhenBackgroundRefreshIsScanning_CancelsScanAndRemovesFolder()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\BusyScan", Name = "BusyScan" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        var filePath = "C:\\Music\\BusyScan\\song.mp3";
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (filePath, DateTime.UtcNow) });

        var extractionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowExtractionToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _metadataService.ExtractMetadataAsync(filePath, Arg.Any<string?>())
            .Returns(async _ =>
            {
                extractionStarted.TrySetResult();
                await allowExtractionToFinish.Task;
                return new SongFileMetadata
                {
                    FilePath = filePath,
                    Title = "Song",
                    Artists = new List<string> { "Artist" }
                };
            });

        var refreshTask = _library.Folders.RefreshAllFoldersAsync();
        await extractionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var removeTask = _library.Folders.RemoveFolderAsync(folder.Id);
        var removed = await removeTask.WaitAsync(TimeSpan.FromSeconds(5));
        var refreshResult = await refreshTask.WaitAsync(TimeSpan.FromSeconds(5));
        allowExtractionToFinish.TrySetResult();

        removed.Should().BeTrue();
        refreshResult.Should().BeFalse("the in-flight refresh was cancelled by folder removal");

        await using var assertContext = _dbHelper.ContextFactory.CreateDbContext();
        (await assertContext.Folders.CountAsync()).Should().Be(0);
        (await assertContext.Songs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RemoveFolderAsync_WhenManualRescanIsQueued_CancelsRunningAndQueuedScans()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\QueuedScan", Name = "QueuedScan" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        var filePath = "C:\\Music\\QueuedScan\\song.mp3";
        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (filePath, DateTime.UtcNow) });

        var extractionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowExtractionToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extractionCount = 0;
        _metadataService.ExtractMetadataAsync(filePath, Arg.Any<string?>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref extractionCount);
                extractionStarted.TrySetResult();
                await allowExtractionToFinish.Task;
                return new SongFileMetadata
                {
                    FilePath = filePath,
                    Title = "Song",
                    Artists = new List<string> { "Artist" }
                };
            });

        var refreshTask = _library.Folders.RefreshAllFoldersAsync();
        await extractionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var queuedRescanTask = _library.Folders.RescanFolderForMusicAsync(folder.Id);
        var removeTask = _library.Folders.RemoveFolderAsync(folder.Id);
        allowExtractionToFinish.TrySetResult();

        var removed = await removeTask.WaitAsync(TimeSpan.FromSeconds(5));
        var refreshResult = await refreshTask.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedRescanResult = await queuedRescanTask.WaitAsync(TimeSpan.FromSeconds(5));

        removed.Should().BeTrue();
        refreshResult.Should().BeFalse();
        queuedRescanResult.Should().BeFalse();
        extractionCount.Should().Be(1, "the queued rescan should be cancelled before it starts extracting metadata");
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_SupersedingScansWaitForActiveWorkAndApplyOnlyLatest()
    {
        using var database = new DbContextFactoryTestHelper(useFileDatabase: true);
        using var library = CreateLibrary(database);
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\Concurrent", Name = "Concurrent" };
        await using (var context = database.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(new[] { ("C:\\Music\\Concurrent\\song1.mp3", DateTime.UtcNow) });

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new TaskCompletionSource<SongFileMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        _metadataService.ExtractMetadataAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(_ =>
        {
            started.TrySetResult();
            return metadata.Task;
        });

        var first = library.Folders.RescanFolderForMusicAsync(folder.Id);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = library.Folders.RescanFolderForMusicAsync(folder.Id);
        var third = library.Folders.RescanFolderForMusicAsync(folder.Id);
        try
        {
            third.IsCompleted.Should().BeFalse();
        }
        finally
        {
            metadata.TrySetResult(new SongFileMetadata
            {
                FilePath = "C:\\Music\\Concurrent\\song1.mp3",
                Title = "Concurrent Song",
                Artists = new List<string> { "Artist" }
            });
        }

        (await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(5)))
            .Should().Equal(false, false, true);
        await _metadataService.Received(2).ExtractMetadataAsync(Arg.Any<string>(), Arg.Any<string?>());

        await using (var assertContext = database.ContextFactory.CreateDbContext())
        {
            var songs = await assertContext.Songs.Where(s => s.FolderId == folder.Id).ToListAsync();

            songs.Should().HaveCount(1);
            songs[0].Title.Should().Be("Concurrent Song");
        }
    }
}
