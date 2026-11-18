using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nagi.Core.Library;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.Library;

public sealed class LibraryFolderWatcherTests : IDisposable
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    private readonly string _root = Directory.CreateTempSubdirectory("nagi-watch-").FullName;
    private readonly ILibraryReader _reader = Substitute.For<ILibraryReader>();
    private readonly ILibraryNotifier _notifier = Substitute.For<ILibraryNotifier>();
    private readonly ILibraryFolderService _folderService = Substitute.For<ILibraryFolderService>();
    private readonly SchedulingTimeProvider _time = new();
    private readonly List<Folder> _folders = new();
    private readonly LibraryFolderWatcher _watcher;

    public LibraryFolderWatcherTests()
    {
        _reader.GetRootFoldersOnDiskAsync(Arg.Any<CancellationToken>()).Returns(_ => _folders.ToList());
        _folderService.RefreshFolderInBackgroundAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _watcher = new LibraryFolderWatcher(_reader, _notifier, _folderService, NullLogger<LibraryFolderWatcher>.Instance, _time);
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
        // File system events arrive on their own thread; the first one schedules the refresh.
        await _time.Scheduled.WaitAsync(EventTimeout);

        _time.Advance(LibraryFolderWatcher.QuietPeriod - TimeSpan.FromTicks(1));
        await _folderService.DidNotReceiveWithAnyArgs().RefreshFolderInBackgroundAsync(default);
        await AdvanceUntilRefreshedAsync();
        await _folderService.Received(1).RefreshFolderInBackgroundAsync(folder.Id, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(@"C:\Music\song.mp3", false, true)]
    [InlineData(@"C:\Music\SONG.FLAC", true, true)]
    [InlineData(@"C:\Music\notes.txt", true, false)]
    [InlineData(@"C:\Music\cover.jpg", true, false)]
    [InlineData(@"C:\Music\Album", true, true)]
    // A folder's Changed event only says something inside it changed, which raises its own event.
    [InlineData(@"C:\Music\Album", false, false)]
    public void OnlyAudioFilesAndMovedFolders_CanChangeTheLibrary(string path, bool includeFolders, bool expected)
    {
        LibraryFolderWatcher.CanChangeLibrary(path, includeFolders).Should().Be(expected);
    }

    [Fact]
    public async Task DeletingASubfolder_RefreshesTheFolder()
    {
        var album = Directory.CreateDirectory(Path.Combine(_root, "Album"));
        var folder = StartWatching(_root);

        album.Delete();
        await _time.Scheduled.WaitAsync(EventTimeout);
        _time.Advance(LibraryFolderWatcher.QuietPeriod);

        await _folderService.Received(1).RefreshFolderInBackgroundAsync(folder.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FolderThatCantBeWatched_DoesNotStopTheOthersBeingWatched()
    {
        _folders.Add(new Folder { Name = "Offline", Path = Path.Combine(_root, "not-connected") });
        var folder = StartWatching(_root);

        await File.WriteAllTextAsync(Path.Combine(_root, "song.mp3"), "audio");
        await _time.Scheduled.WaitAsync(EventTimeout);
        await AdvanceUntilRefreshedAsync();

        await _folderService.Received(1).RefreshFolderInBackgroundAsync(folder.Id, Arg.Any<CancellationToken>());
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
            _notifier.Changed += Raise.Event<EventHandler<LibraryChange>>(_notifier, new LibraryChange(LibraryChangeKind.RootAdded, added.Id));
            await File.WriteAllTextAsync(Path.Combine(second, "song.flac"), "audio");
            await _time.Scheduled.WaitAsync(EventTimeout);
            await AdvanceUntilRefreshedAsync();

            await _folderService.Received(1).RefreshFolderInBackgroundAsync(added.Id, Arg.Any<CancellationToken>());
        }
        finally
        {
            _watcher.Dispose();
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public async Task FolderAddedWhileAnOlderSyncIsQuerying_StaysWatched()
    {
        var root = new Folder { Name = "Root", Path = _root };
        _folders.Add(root);
        var startupQuery = new TaskCompletionSource<IReadOnlyList<Folder>>();
        _reader.GetRootFoldersOnDiskAsync(Arg.Any<CancellationToken>())
            .Returns(_ => startupQuery.Task, _ => Task.FromResult<IReadOnlyList<Folder>>(_folders.ToList()));
        _watcher.Start();

        var added = new Folder { Name = "Second", Path = Directory.CreateDirectory(Path.Combine(_root, "Second")).FullName };
        _folders.Add(added);
        _notifier.Changed += Raise.Event<EventHandler<LibraryChange>>(_notifier, new LibraryChange(LibraryChangeKind.RootAdded, added.Id));
        startupQuery.SetResult([root]);
        await _time.SecondFolderWatched.WaitAsync(EventTimeout);

        _time.UnwatchedFolders.Should().Be(0);
    }

    // A fresh write raises Created and then Changed events on the watcher's thread. A late one re-arms the debounce,
    // as in production, and one that lands inside FakeTimeProvider.Advance can make it skip the due timer, so the
    // test keeps advancing until the refresh fires. The timer callback runs inside Advance, so the call is visible
    // as soon as it returns.
    private async Task AdvanceUntilRefreshedAsync()
    {
        var deadline = DateTime.UtcNow + EventTimeout;
        while (true)
        {
            _time.Advance(LibraryFolderWatcher.QuietPeriod);
            if (_folderService.ReceivedCalls().Any() || DateTime.UtcNow > deadline) return;
            await Task.Delay(10);
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

    /// <summary>
    ///     A fake clock that tells when the watcher schedules a refresh, which a file system event on another thread does.
    ///     The watcher creates one timer per watched folder and disposes it when it stops watching the folder.
    /// </summary>
    private sealed class SchedulingTimeProvider : FakeTimeProvider
    {
        private readonly TaskCompletionSource _scheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondFolderWatched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _created;
        private int _disposed;

        public Task Scheduled => _scheduled.Task;

        public Task SecondFolderWatched => _secondFolderWatched.Task;

        public int UnwatchedFolders => Volatile.Read(ref _disposed);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new SignallingTimer(base.CreateTimer(callback, state, dueTime, period), this);
            if (Interlocked.Increment(ref _created) == 2) _secondFolderWatched.TrySetResult();
            return timer;
        }

        private sealed class SignallingTimer(ITimer timer, SchedulingTimeProvider owner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                var changed = timer.Change(dueTime, period);
                owner._scheduled.TrySetResult();
                return changed;
            }

            public void Dispose()
            {
                Interlocked.Increment(ref owner._disposed);
                timer.Dispose();
            }

            public ValueTask DisposeAsync() => timer.DisposeAsync();
        }
    }
}
