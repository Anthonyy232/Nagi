using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public sealed class LibraryFolderWatcherTests : IDisposable
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);

    private readonly string _root = Directory.CreateTempSubdirectory("nagi-watch-").FullName;
    private readonly ILibraryReader _reader = Substitute.For<ILibraryReader>();
    private readonly ILibraryScanner _scanner = Substitute.For<ILibraryScanner>();
    private readonly List<Folder> _folders = new();
    private readonly LibraryFolderWatcher _watcher;

    public LibraryFolderWatcherTests()
    {
        _reader.GetRootFoldersWithCountsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => _folders.Select(f => new FolderSummary(f, 0)).ToList());
        _watcher = new LibraryFolderWatcher(_reader, _scanner, NullLogger<LibraryFolderWatcher>.Instance)
        {
            QuietPeriod = QuietPeriod
        };
    }

    public void Dispose()
    {
        _watcher.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AddingAudioFiles_RefreshesTheFolderOnceAfterItGoesQuiet()
    {
        var folder = await StartWatchingAsync(_root);

        for (var i = 0; i < 5; i++)
            await File.WriteAllTextAsync(Path.Combine(_root, $"song{i}.mp3"), "audio");
        await Task.Delay(Settle);

        await _scanner.Received(1).RefreshFolderInBackgroundAsync(folder.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangingNonAudioFiles_DoesNotRefresh()
    {
        await StartWatchingAsync(_root);

        await File.WriteAllTextAsync(Path.Combine(_root, "notes.txt"), "text");
        await File.WriteAllTextAsync(Path.Combine(_root, "cover.jpg"), "image");
        await Task.Delay(Settle);

        await _scanner.DidNotReceiveWithAnyArgs().RefreshFolderInBackgroundAsync(default);
    }

    [Fact]
    public async Task DeletingASubfolder_RefreshesTheFolder()
    {
        var album = Directory.CreateDirectory(Path.Combine(_root, "Album"));
        var folder = await StartWatchingAsync(_root);

        album.Delete();
        await Task.Delay(Settle);

        await _scanner.Received(1).RefreshFolderInBackgroundAsync(folder.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddedLibraryFolder_IsWatchedToo()
    {
        await StartWatchingAsync(_root);
        var second = Directory.CreateDirectory(Path.Combine(_root, "..", Path.GetFileName(_root) + "-second")).FullName;
        try
        {
            var added = new Folder { Name = "Second", Path = second };
            _folders.Add(added);
            _scanner.LibraryContentChanged += Raise.EventWith(new LibraryContentChangedEventArgs(LibraryChangeType.FolderAdded, added.Id));
            await Task.Delay(Settle);

            await File.WriteAllTextAsync(Path.Combine(second, "song.flac"), "audio");
            await Task.Delay(Settle);

            await _scanner.Received(1).RefreshFolderInBackgroundAsync(added.Id, Arg.Any<CancellationToken>());
        }
        finally
        {
            _watcher.Dispose();
            Directory.Delete(second, recursive: true);
        }
    }

    private async Task<Folder> StartWatchingAsync(string path)
    {
        var folder = new Folder { Name = Path.GetFileName(path), Path = path };
        _folders.Add(folder);
        _watcher.Start();
        // Start subscribes asynchronously; give it a moment before touching the disk.
        await Task.Delay(QuietPeriod);
        return folder;
    }
}
