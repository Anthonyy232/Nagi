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
    private readonly TaskCompletionSource _refreshed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LibraryFolderWatcherTests()
    {
        _reader.GetRootFoldersWithCountsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => _folders.Select(f => new FolderSummary(f, 0)).ToList());
        // File system events arrive on their own thread, so tests wait for this signal rather than a fixed time.
        _scanner.RefreshFolderInBackgroundAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _refreshed.TrySetResult();
            return true;
        });
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
        var folder = StartWatching(_root);

        for (var i = 0; i < 5; i++)
            await File.WriteAllTextAsync(Path.Combine(_root, $"song{i}.mp3"), "audio");

        await _refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await _scanner.Received(1).RefreshFolderInBackgroundAsync(folder.Id, Arg.Any<CancellationToken>());
        await Task.Delay(QuietPeriod * 2);
        await _scanner.Received(1).RefreshFolderInBackgroundAsync(folder.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangingNonAudioFiles_DoesNotRefresh()
    {
        StartWatching(_root);

        await File.WriteAllTextAsync(Path.Combine(_root, "notes.txt"), "text");
        await File.WriteAllTextAsync(Path.Combine(_root, "cover.jpg"), "image");
        await Task.Delay(Settle);

        await _scanner.DidNotReceiveWithAnyArgs().RefreshFolderInBackgroundAsync(default);
    }

    [Fact]
    public async Task DeletingASubfolder_RefreshesTheFolder()
    {
        var album = Directory.CreateDirectory(Path.Combine(_root, "Album"));
        var folder = StartWatching(_root);

        album.Delete();

        await _refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await _scanner.Received(1).RefreshFolderInBackgroundAsync(folder.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddedLibraryFolder_IsWatchedToo()
    {
        StartWatching(_root);
        var second = Directory.CreateDirectory(Path.Combine(_root, "..", Path.GetFileName(_root) + "-second")).FullName;
        try
        {
            var added = new Folder { Name = "Second", Path = second };
            _folders.Add(added);
            _scanner.LibraryContentChanged += Raise.EventWith(new LibraryContentChangedEventArgs(LibraryChangeType.FolderAdded, added.Id));
            await File.WriteAllTextAsync(Path.Combine(second, "song.flac"), "audio");

            await _refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await _scanner.Received(1).RefreshFolderInBackgroundAsync(added.Id, Arg.Any<CancellationToken>());
        }
        finally
        {
            _watcher.Dispose();
            Directory.Delete(second, recursive: true);
        }
    }

    // The stubbed folder list completes synchronously, so the watchers exist as soon as Start returns.
    private Folder StartWatching(string path)
    {
        var folder = new Folder { Name = Path.GetFileName(path), Path = path };
        _folders.Add(folder);
        _watcher.Start();
        return folder;
    }

}
