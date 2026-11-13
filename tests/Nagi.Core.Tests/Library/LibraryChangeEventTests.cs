using FluentAssertions;
using Nagi.Core.Library;
using Nagi.Core.Metadata;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.Library;

public class LibraryChangeEventTests : LibraryTestBase
{
    // What each kind touches decides who reloads: the folder watcher (roots), playlists (songs added or removed), song,
    // album, artist and genre lists (songs), the folders page (folders), song lists again (favorites) and the
    // player's history (listen history).
    [Theory]
    [InlineData(LibraryChangeKind.RootAdded, true, false, false, true, false, false)]
    [InlineData(LibraryChangeKind.RootRemoved, true, true, true, true, false, true)]
    [InlineData(LibraryChangeKind.FolderRescanned, false, true, true, true, false, true)]
    [InlineData(LibraryChangeKind.LibraryRescanned, false, true, true, true, false, true)]
    [InlineData(LibraryChangeKind.LibraryCleared, true, true, true, true, false, true)]
    [InlineData(LibraryChangeKind.SongsChanged, false, false, true, false, false, false)]
    [InlineData(LibraryChangeKind.FavoritesChanged, false, false, false, false, true, false)]
    [InlineData(LibraryChangeKind.ListenHistoryCleared, false, false, false, false, false, true)]
    public void EachKind_SaysWhatItTouched(LibraryChangeKind kind, bool roots, bool addsOrRemoves, bool songs, bool folders,
        bool favorites, bool listenHistory)
    {
        var change = new LibraryChange(kind);

        new[] { change.AffectsRoots, change.AddsOrRemovesSongs, change.AffectsSongs, change.AffectsFolders, change.AffectsFavorites,
                change.AffectsListenHistory }
            .Should().Equal(roots, addsOrRemoves, songs, folders, favorites, listenHistory);
    }

    [Fact]
    public async Task AddFolderAsync_AnnouncesTheRootAdded()
    {
        var folderPath = "C:\\Music\\NewFolder";
        _fileSystem.GetLastWriteTimeUtc(folderPath).Returns(DateTime.UtcNow);

        LibraryChange? eventArgs = null;
        _library.Notifier.Changed += (s, e) => eventArgs = e;

        await _library.Folders.AddFolderAsync(folderPath);

        eventArgs.Should().NotBeNull();
        eventArgs!.Kind.Should().Be(LibraryChangeKind.RootAdded);
        eventArgs.FolderId.Should().NotBeNull();
    }

    [Fact]
    public async Task RemoveFolderAsync_AnnouncesTheRootRemoved()
    {
        var folder = new Folder { Path = "C:\\Music\\FolderToRemove", Name = "FolderToRemove" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        LibraryChange? eventArgs = null;
        _library.Notifier.Changed += (s, e) => eventArgs = e;

        await _library.Folders.RemoveFolderAsync(folder.Id);

        eventArgs.Should().NotBeNull();
        eventArgs!.Kind.Should().Be(LibraryChangeKind.RootRemoved);
        eventArgs.FolderId.Should().Be(folder.Id);
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_WithChanges_AnnouncesTheFolderRescanned()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\ScanChanges", Name = "ScanChanges" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(new[] { ("C:\\Music\\ScanChanges\\new.mp3", DateTime.UtcNow) });

        _metadataService.ExtractMetadataAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new SongFileMetadata { FilePath = "C:\\Music\\ScanChanges\\new.mp3", Title = "New Song" });

        LibraryChange? eventArgs = null;
        _library.Notifier.Changed += (s, e) => eventArgs = e;

        await _library.Folders.RescanFolderForMusicAsync(folder.Id);

        eventArgs.Should().NotBeNull();
        eventArgs!.Kind.Should().Be(LibraryChangeKind.FolderRescanned);
        eventArgs.FolderId.Should().Be(folder.Id);
    }

    [Fact]
    public async Task RescanFolderForMusicAsync_CancelledPartWay_StillAnnouncesTheFolder()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\Cancelled", Name = "Cancelled" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(new[] { ("C:\\Music\\Cancelled\\new.mp3", DateTime.UtcNow) });
        using var cts = new CancellationTokenSource();
        _metadataService.ExtractMetadataAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(_ =>
        {
            cts.Cancel();
            return Task.FromResult(new SongFileMetadata { FilePath = "C:\\Music\\Cancelled\\new.mp3", Title = "New Song" });
        });
        var changes = new List<LibraryChange>();
        _library.Notifier.Changed += (_, e) => changes.Add(e);

        var changed = await _library.Folders.RescanFolderForMusicAsync(folder.Id, cancellationToken: cts.Token);

        // What the scan wrote before it was cancelled stays in the library, so lists must hear about it.
        changed.Should().BeFalse();
        changes.Should().Equal(new LibraryChange(LibraryChangeKind.FolderRescanned, folder.Id));
    }

    [Fact]
    public async Task RefreshAllFoldersAsync_WithChanges_AnnouncesTheLibraryRescanned()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\ScanChanges", Name = "ScanChanges" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.Add(folder);
            await context.SaveChangesAsync();
        }

        _fileSystem.DirectoryExists(folder.Path).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(folder.Path, Arg.Any<string>(), Arg.Any<SearchOption>())
            .Returns(new[] { ("C:\\Music\\ScanChanges\\new.mp3", DateTime.UtcNow) });
        _metadataService.ExtractMetadataAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new SongFileMetadata { FilePath = "C:\\Music\\ScanChanges\\new.mp3", Title = "New Song" });

        var changes = new List<LibraryChange>();
        _library.Notifier.Changed += (_, e) => changes.Add(e);

        await _library.Folders.RefreshAllFoldersAsync();

        // Not once per folder as well, which would reload every list again.
        changes.Should().Equal(new LibraryChange(LibraryChangeKind.LibraryRescanned));
    }

    [Fact]
    public async Task ForceRescanMetadataAsync_CancelledInFinalFolder_StillAnnouncesTheFolderThatChanged()
    {
        var firstFolder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\A", Name = "A" };
        var finalFolder = new Folder { Id = Guid.NewGuid(), Path = "C:\\Music\\B", Name = "B" };
        await using (var context = _dbHelper.ContextFactory.CreateDbContext())
        {
            context.Folders.AddRange(firstFolder, finalFolder);
            await context.SaveChangesAsync();
        }

        var firstFile = "C:\\Music\\A\\first.mp3";
        var finalFile = "C:\\Music\\B\\final.mp3";
        _fileSystem.DirectoryExists(Arg.Any<string>()).Returns(true);
        _fileSystem.EnumerateFilesWithLastWriteTime(firstFolder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (firstFile, DateTime.UtcNow) });
        _fileSystem.EnumerateFilesWithLastWriteTime(finalFolder.Path, "*.*", SearchOption.AllDirectories)
            .Returns(new[] { (finalFile, DateTime.UtcNow) });

        using var cts = new CancellationTokenSource();
        _metadataService.ExtractMetadataAsync(firstFile, firstFolder.Path)
            .Returns(new SongFileMetadata { FilePath = firstFile, Title = "First" });
        _metadataService.ExtractMetadataAsync(finalFile, finalFolder.Path)
            .Returns(_ =>
            {
                cts.Cancel();
                return Task.FromResult(new SongFileMetadata { FilePath = finalFile, Title = "Final" });
            });

        var changeTypes = new List<LibraryChangeKind>();
        _library.Notifier.Changed += (_, e) => changeTypes.Add(e.Kind);

        var result = await _library.Folders.ForceRescanMetadataAsync(cancellationToken: cts.Token);

        result.Should().BeFalse();
        changeTypes.Should().Equal(LibraryChangeKind.LibraryRescanned);
    }
}
