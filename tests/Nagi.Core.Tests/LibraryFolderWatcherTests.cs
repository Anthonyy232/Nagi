using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

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
        _reader.GetRootFoldersWithCountsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => _folders.Select(f => new FolderSummary(f, 0, IsServer: false)).ToList());
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
        _time.Advance(LibraryFolderWatcher.QuietPeriod);
        await _folderService.Received(1).RefreshFolderInBackgroundAsync(folder.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangingNonAudioFiles_DoesNotScheduleARefresh()
    {
        StartWatching(_root);

        await File.WriteAllTextAsync(Path.Combine(_root, "notes.txt"), "text");
        await File.WriteAllTextAsync(Path.Combine(_root, "cover.jpg"), "image");
        // Nothing signals that an event was ignored, so the test gives the events time to arrive.
        await Task.Delay(TimeSpan.FromSeconds(1));

        _time.Scheduled.IsCompleted.Should().BeFalse();
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
            _time.Advance(LibraryFolderWatcher.QuietPeriod);

            await _folderService.Received(1).RefreshFolderInBackgroundAsync(added.Id, Arg.Any<CancellationToken>());
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

    /// <summary>
    ///     A fake clock that tells when the watcher schedules a refresh, which a file system event on another thread does.
    /// </summary>
    private sealed class SchedulingTimeProvider : FakeTimeProvider
    {
        private readonly TaskCompletionSource _scheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Scheduled => _scheduled.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new SignallingTimer(base.CreateTimer(callback, state, dueTime, period), _scheduled);

        private sealed class SignallingTimer(ITimer timer, TaskCompletionSource scheduled) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                var changed = timer.Change(dueTime, period);
                scheduled.TrySetResult();
                return changed;
            }

            public void Dispose() => timer.Dispose();

            public ValueTask DisposeAsync() => timer.DisposeAsync();
        }
    }
}
