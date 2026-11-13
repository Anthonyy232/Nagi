using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Library;
using Nagi.Core.Lyrics;
using Nagi.Core.Lyrics.Romanization;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Settings;
using Nagi.Presentation.Common;
using Nagi.Presentation.Lyrics;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests.Lyrics;

public class LyricsPageViewModelTests
{
    private readonly IMusicPlaybackService _playback = Substitute.For<IMusicPlaybackService>();
    private readonly ILibraryReader _library = Substitute.For<ILibraryReader>();
    private readonly ILrcService _lyrics = Substitute.For<ILrcService>();
    private readonly ILyricRomanizationService _romanization = Substitute.For<ILyricRomanizationService>();
    private readonly IUIService _ui = Substitute.For<IUIService>();

    public LyricsPageViewModelTests()
    {
        _romanization.ApplyRomanizationAsync(Arg.Any<IEnumerable<LyricLine>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<LyricLine>>(call.Arg<IEnumerable<LyricLine>>().ToList()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Lyrics_ShowTimedLinesOrPlainText(bool timed)
    {
        var song = new Song { Title = "Song" };
        const string raw = "[00:02.00]First";
        LyricLine[] lines = [new(TimeSpan.FromSeconds(2), "First")];
        _lyrics.GetLyricsAsync(song, Arg.Any<CancellationToken>()).Returns(new SongLyrics(raw, lines, timed));
        _library.GetSongByIdAsync(song.Id).Returns(song);
        _playback.CurrentTrack.Returns(song);

        using var viewModel = CreateViewModel();

        viewModel.HasLyrics.Should().Be(timed);
        viewModel.HasUnsyncedLyrics.Should().Be(!timed);
        viewModel.EditableLyrics.Should().Be(raw);
        viewModel.CanEditLyrics.Should().BeTrue();
        viewModel.IsLoading.Should().BeFalse();
        (timed ? viewModel.LyricLines : viewModel.UnsyncedLyricLines).Should().Equal(lines);
    }

    [Fact]
    public void NewTimedLyrics_SignalTheReplacementBeforeTheirFirstCurrentLine()
    {
        var song = new Song { Title = "Song" };
        LyricLine[] lines = [new(TimeSpan.FromSeconds(1), "First"), new(TimeSpan.FromSeconds(3), "Second")];
        _lyrics.GetLyricsAsync(song, Arg.Any<CancellationToken>()).Returns(new SongLyrics("raw", lines, true));
        _library.GetSongByIdAsync(song.Id).Returns(song);
        _playback.CurrentTrack.Returns(song);
        _playback.CurrentPosition.Returns(TimeSpan.FromSeconds(4));
        var events = new List<string>();

        var dispatcher = new QueuedDispatcher();
        using var viewModel = CreateViewModel(dispatcher);
        viewModel.LyricsReplaced += () => events.Add($"replaced at {viewModel.CurrentLineIndex}");
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LyricsPageViewModel.CurrentLine))
                events.Add($"line {viewModel.CurrentLineIndex}");
        };
        dispatcher.Drain();

        events.Should().Equal("replaced at -1", "line 1");
    }

    [Fact]
    public void QueuedReset_FromOldTrack_CannotClearNewLyrics()
    {
        var dispatcher = new QueuedDispatcher();
        using var viewModel = CreateViewModel(dispatcher);
        dispatcher.Drain();
        var oldSong = ConfigureSong("Old");
        ChangeTrack(oldSong);
        dispatcher.Actions.Reader.TryRead(out var oldReset).Should().BeTrue();

        var nextSong = ConfigureSong("Next");
        ChangeTrack(nextSong);
        dispatcher.Drain();
        oldReset!();

        viewModel.SongTitle.Should().Be("Next");
        viewModel.LyricLines.Should().ContainSingle().Which.Text.Should().Be("Next");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObsoleteFetchFailure_CannotChangeLoadingOrEditingState(bool dispose)
    {
        var dispatcher = new QueuedDispatcher();
        using var viewModel = CreateViewModel(dispatcher);
        dispatcher.Drain();
        var oldSong = new Song { Title = "Old" };
        var pending = new TaskCompletionSource<SongLyrics?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _lyrics.GetLyricsAsync(oldSong, Arg.Any<CancellationToken>()).Returns(pending.Task);
        ChangeTrack(oldSong);
        dispatcher.Drain();
        var next = new TaskCompletionSource<SongLyrics?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (dispose) viewModel.Dispose();
        else
        {
            var nextSong = new Song { Title = "Next" };
            _lyrics.GetLyricsAsync(nextSong, Arg.Any<CancellationToken>()).Returns(next.Task);
            ChangeTrack(nextSong);
            dispatcher.Drain();
        }

        pending.SetException(new IOException("Old fetch failed"));
        var failedUpdate = await dispatcher.ReadAsync();
        failedUpdate();

        viewModel.IsLoading.Should().BeTrue();
        viewModel.CanEditLyrics.Should().BeFalse();
        next.SetResult(null);
    }

    [Fact]
    public void QueuedPosition_FromOldLyrics_CannotSeekTheNewTrackDisplay()
    {
        var dispatcher = new QueuedDispatcher();
        using var viewModel = CreateViewModel(dispatcher);
        dispatcher.Drain();
        ChangeTrack(ConfigureSong("Old"));
        dispatcher.Drain();
        _playback.CurrentPosition.Returns(TimeSpan.FromMinutes(4));
        _playback.PositionChanged += Raise.Event<Action>();
        dispatcher.Actions.Reader.TryRead(out var oldPosition).Should().BeTrue();
        _playback.CurrentPosition.Returns(TimeSpan.FromSeconds(3));
        ChangeTrack(ConfigureSong("Next"));
        dispatcher.Drain();

        oldPosition!();

        viewModel.CurrentPosition.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Prefetch_WaitsForCurrentLyricsBeforeFetchingNextTrack()
    {
        var current = new Song { Title = "Current" };
        var next = new Song { Title = "Next" };
        var currentLyrics = new TaskCompletionSource<SongLyrics?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _library.GetSongByIdAsync(current.Id).Returns(current);
        _library.GetSongByIdAsync(next.Id).Returns(next);
        _lyrics.GetLyricsAsync(current, Arg.Any<CancellationToken>()).Returns(currentLyrics.Task);
        _lyrics.GetLyricsAsync(next, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            nextRequested.SetResult();
            return Task.FromResult<SongLyrics?>(null);
        });
        _playback.NextTrackId.Returns(next.Id);
        _playback.CurrentTrack.Returns(current);
        using var viewModel = CreateViewModel();

        await _lyrics.DidNotReceive().GetLyricsAsync(next, Arg.Any<CancellationToken>());
        currentLyrics.SetResult(null);
        await nextRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.IsLoading.Should().BeFalse();
        await _lyrics.Received(1).GetLyricsAsync(next, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prefetch_FindsANextTrackFromOutsideTheLibraryInTheQueue()
    {
        var current = new Song { Title = "Current" };
        var next = new Song { Title = "Dropped file", FilePath = @"C:\temp\next.mp3" };
        _library.GetSongByIdAsync(current.Id).Returns(current);
        _library.GetSongByIdAsync(next.Id).Returns((Song?)null);
        _playback.GetQueueSongsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Song> { [next.Id] = next });
        var nextRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lyrics.GetLyricsAsync(next, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            nextRequested.SetResult();
            return Task.FromResult<SongLyrics?>(null);
        });
        _playback.NextTrackId.Returns(next.Id);
        _playback.CurrentTrack.Returns(current);

        using var viewModel = CreateViewModel();

        await nextRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PrefetchLookup_CompletesAfterTrackChange_DoesNotBorrowNewTracksCancellationToken()
    {
        var current = new Song { Title = "Current" };
        var next = new Song { Title = "Old next" };
        var pending = new TaskCompletionSource<Song?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _library.GetSongByIdAsync(next.Id).Returns(pending.Task);
        _playback.NextTrackId.Returns(next.Id);
        _playback.CurrentTrack.Returns(current);
        using var viewModel = CreateViewModel();
        var prefetch = viewModel.PendingPrefetch;

        _playback.NextTrackId.Returns((Guid?)null);
        ChangeTrack(new Song { Title = "Replacement" });
        pending.SetResult(next);
        await prefetch.WaitAsync(TimeSpan.FromSeconds(5));

        await _lyrics.DidNotReceive().GetLyricsAsync(next, Arg.Any<CancellationToken>());
        await _library.Received(1).GetSongByIdAsync(next.Id);
    }

    private Song ConfigureSong(string title)
    {
        var song = new Song { Title = title, ArtistName = string.Empty };
        _lyrics.GetLyricsAsync(song, Arg.Any<CancellationToken>())
            .Returns(new SongLyrics(title, [new LyricLine(TimeSpan.Zero, title)], IsSynced: true));
        return song;
    }

    [Fact]
    public async Task EditLyrics_SavesTheEditedTextWithPlatformLineEndings()
    {
        var song = PlayingSongWithLyrics("Old", cached: false);
        using var viewModel = CreateViewModel();
        _ui.ShowLyricsEditorAsync("Old", false).Returns(new LyricsEdit(LyricsEditAction.Save, "First\nSecond"));

        await viewModel.EditLyricsCommand.ExecuteAsync(null);

        await _lyrics.Received(1).SaveLyricsAsync(song, $"First{Environment.NewLine}Second");
    }

    [Fact]
    public async Task EditLyrics_RemovesCachedLyricsWhenAsked()
    {
        var song = PlayingSongWithLyrics("Old", cached: true);
        using var viewModel = CreateViewModel();
        _ui.ShowLyricsEditorAsync("Old", true).Returns(new LyricsEdit(LyricsEditAction.Remove));

        await viewModel.EditLyricsCommand.ExecuteAsync(null);

        await _lyrics.Received(1).RemoveCachedLyricsAsync(song);
        await _lyrics.DidNotReceiveWithAnyArgs().SaveLyricsAsync(default!, default!);
    }

    [Fact]
    public async Task EditLyrics_WhenCancelled_ChangesNothing()
    {
        PlayingSongWithLyrics("Old", cached: true);
        using var viewModel = CreateViewModel();
        _ui.ShowLyricsEditorAsync("Old", true).Returns((LyricsEdit?)null);

        await viewModel.EditLyricsCommand.ExecuteAsync(null);

        await _lyrics.DidNotReceiveWithAnyArgs().SaveLyricsAsync(default!, default!);
        await _lyrics.DidNotReceiveWithAnyArgs().RemoveCachedLyricsAsync(default!);
    }

    [Fact]
    public async Task EditLyrics_WhenSavingFails_TellsTheUser()
    {
        var song = PlayingSongWithLyrics("Old", cached: false);
        using var viewModel = CreateViewModel();
        _ui.ShowLyricsEditorAsync("Old", false).Returns(new LyricsEdit(LyricsEditAction.Save, "New"));
        _lyrics.SaveLyricsAsync(song, "New").Returns(Task.FromException(new IOException("Disk full")));

        await viewModel.EditLyricsCommand.ExecuteAsync(null);

        await _ui.Received(1).ShowMessageDialogAsync(Strings.LyricsPage_EditLyrics_Error_Title,
            Strings.LyricsPage_EditLyrics_Error_Message);
    }

    private Song PlayingSongWithLyrics(string raw, bool cached)
    {
        var song = new Song { Title = "Song", Lyrics = raw };
        _lyrics.GetLyricsAsync(song, Arg.Any<CancellationToken>()).Returns(LyricsParser.Parse(raw));
        _lyrics.HasCachedLyrics(song).Returns(cached);
        _library.GetSongByIdAsync(song.Id).Returns(song);
        _playback.CurrentTrack.Returns(song);
        return song;
    }

    private void ChangeTrack(Song song)
    {
        _playback.CurrentTrack.Returns(song);
        _playback.TrackChanged += Raise.Event<Action>();
    }

    [Theory]
    [InlineData(0, 15, 0.5, 5)]
    [InlineData(2, 40, 0.5, 20)]
    [InlineData(1, 25, 1, 0)]
    public void CurrentLineProgress_RunsUntilTheNextLineOrTheSongEnds(int lineIndex, int positionSeconds,
        double fraction, int remainingSeconds)
    {
        using var viewModel = CreateViewModel();
        var lines = new[] { 10, 20, 20 }.Select(start => new LyricLine(TimeSpan.FromSeconds(start), "line")).ToList();
        viewModel.LyricLines.ReplaceRange(lines);
        viewModel.SongDuration = TimeSpan.FromSeconds(60);
        viewModel.CurrentLine = lines[lineIndex];
        viewModel.CurrentPosition = TimeSpan.FromSeconds(positionSeconds);

        viewModel.GetCurrentLineProgress().Should().Be((fraction, TimeSpan.FromSeconds(remainingSeconds)));
    }

    [Fact]
    public void CurrentLineSungText_GivesTheWaitForTheNextWordAtThePlaybackRate()
    {
        _playback.PlaybackRate.Returns(2);
        using var viewModel = CreateViewModel();
        viewModel.CurrentLine = LyricsParser.Parse("[00:10.00]<00:10.00>Hello <00:13.00>world")!.Lines[0];
        viewModel.CurrentPosition = TimeSpan.FromSeconds(11);

        viewModel.GetCurrentLineSungText().Should().Be((6, TimeSpan.FromSeconds(1)));
    }

    private LyricsPageViewModel CreateViewModel(IDispatcherService? dispatcher = null) => new(
        _playback, dispatcher ?? new ImmediateDispatcher(), _lyrics, _library, _romanization,
        Substitute.For<ISettingsService>(), Substitute.For<IRomanizationPackManager>(), _ui,
        NullLogger<LyricsPageViewModel>.Instance);

    private sealed class QueuedDispatcher : IDispatcherService
    {
        public Channel<Action> Actions { get; } = Channel.CreateUnbounded<Action>();
        public bool HasThreadAccess => false;
        public bool TryEnqueue(Action action) => Actions.Writer.TryWrite(action);
        public Task EnqueueAsync(Func<Task> function) => function();
        public Task<T> EnqueueAsync<T>(Func<T> function) => Task.FromResult(function());
        public Task<Action> ReadAsync() => Actions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public void Drain()
        {
            while (Actions.Reader.TryRead(out var action)) action();
        }
    }
}
