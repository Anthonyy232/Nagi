using System.Threading.Tasks;
using System.Net.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Artwork;
using Nagi.Core.Library;
using Nagi.Core.Metadata;
using Nagi.Core.OnlineProviders;
using Nagi.Core.OnlineProviders.Pipelines;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class LibraryChangeEventTests : IDisposable
{
    private readonly DbContextFactoryTestHelper _dbHelper;
    private readonly IFileSystemService _fileSystem;
    private readonly TestLibrary _library;
    private readonly LibraryReader _libraryReader;
    private readonly IMetadataService _metadataService;
    private readonly ProviderPipelineProvider _pipelines;

    public LibraryChangeEventTests()
    {
        _fileSystem = Substitute.For<IFileSystemService>();
        _fileSystem.NormalizePath(Arg.Any<string>()).Returns(call => PathCanonicalizer.Normalize(call.Arg<string>()));
        _metadataService = Substitute.For<IMetadataService>();

        _dbHelper = new DbContextFactoryTestHelper();

        _pipelines = TestProviderPipeline.Build(ProviderCatalog.ImageDownload.ProviderId);

        _libraryReader = new LibraryReader(_dbHelper.ContextFactory, _fileSystem, TestSettings.Create());

        _library = new TestLibrary(
            _dbHelper.ContextFactory,
            _fileSystem,
            _metadataService,
            Substitute.For<IArtistMetadataService>(),
            Substitute.For<IAlbumCoverService>(),
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IPathConfiguration>(),
            TestSettings.Create(),
            Substitute.For<IReplayGainService>(),
            Substitute.For<IImageProcessor>(),
            _pipelines);
    }

    public void Dispose()
    {
        _library.Dispose();
        _pipelines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _dbHelper.Dispose();
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

        LibraryChange? eventArgs = null;
        _library.Notifier.Changed += (s, e) => eventArgs = e;

        await _library.Folders.RefreshAllFoldersAsync();

        eventArgs.Should().NotBeNull();
        eventArgs!.Kind.Should().Be(LibraryChangeKind.LibraryRescanned);
        eventArgs.FolderId.Should().BeNull();
    }

    [Fact]
    public async Task ForceRescanMetadataAsync_CancelledInFinalFolder_DoesNotFireLibraryRescanned()
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
        changeTypes.Should().NotContain(LibraryChangeKind.LibraryRescanned);
    }
}
