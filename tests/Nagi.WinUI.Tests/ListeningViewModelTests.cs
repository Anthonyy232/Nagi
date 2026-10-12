using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml.Data;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Models;
using Nagi.WinUI.ViewModels;
using NSubstitute;
using Xunit;

namespace Nagi.WinUI.Tests;

public partial class PlayerViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpNext_FollowsPlaybackAndRestoresEarlierSongsWhenGoingBackOrRepeating(bool shuffle)
    {
        var songs = Enumerable.Range(0, 6).Select(index => new Song { Title = $"Song {index}" }).ToArray();
        var ids = songs.Select(song => song.Id).ToArray();
        var shuffledIds = ids.Reverse().ToArray();
        var order = shuffle ? shuffledIds : ids;
        _playback.PlaybackQueue.Returns(ids);
        _playback.ShuffledQueue.Returns(shuffledIds);
        _playback.IsShuffleEnabled.Returns(shuffle);
        _playback.CurrentRepeatMode.Returns(RepeatMode.RepeatAll);
        _playback.GetQueueSongsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(songs.ToDictionary(song => song.Id));
        _viewModel.IsQueuePanelOpen = true;

        foreach (var index in new[] { 3, 4, 2, 0 })
        {
            _playback.CurrentTrack.Returns(songs.Single(song => song.Id == order[index]));
            _playback.CurrentQueueIndex.Returns(Array.IndexOf(ids, order[index]));
            _playback.CurrentShuffledIndex.Returns(Array.IndexOf(shuffledIds, order[index]));
            _playback.TrackChanged += Raise.Event<Action>();

            _viewModel.SidebarQueue.Select(entry => entry.Id).Should().Equal(order.Skip(index));
            _viewModel.SidebarQueue.Select(entry => entry.Position).Should().Equal(Enumerable.Range(1, order.Length - index));
        }

        _playback.IsShuffleEnabled.Returns(!shuffle);
        _playback.ShuffleModeChanged += Raise.Event<Action>();
        var switchedOrder = shuffle ? ids : shuffledIds;
        var switchedIndex = shuffle ? _playback.CurrentQueueIndex : _playback.CurrentShuffledIndex;
        _viewModel.SidebarQueue.Select(entry => entry.Id).Should().Equal(switchedOrder.Skip(switchedIndex));
    }

    [Fact]
    public async Task HistoryPlayback_PreservesTheQueueAndSelectsTheRequestedTrackEvenWithRepeatOne()
    {
        var current = Guid.NewGuid();
        var historical = Guid.NewGuid();
        _playback.PlaybackQueue.Returns(new[] { current, historical });
        _playback.CurrentRepeatMode.Returns(RepeatMode.RepeatOne);
        var entry = new RecentListenEntry(new RecentListen(1, historical, "Historical", "Artist", DateTime.UtcNow));

        await _viewModel.PlayHistoryNowCommand.ExecuteAsync(entry);

        await _playback.Received(1).PlayNextAsync(historical);
        await _playback.Received(1).PlayQueueItemAsync(1);
        await _playback.DidNotReceive().PlayAsync(Arg.Any<Guid>());
        await _playback.DidNotReceive().NextAsync();
    }

    [Fact]
    public async Task FavoriteWriteFinishingAfterTrackChange_DoesNotMarkTheNewTrack()
    {
        var original = new Song { Title = "Original", FolderId = Guid.NewGuid() };
        var next = new Song { Title = "Next" };
        _playback.CurrentTrack.Returns(original);
        _playback.TrackChanged += Raise.Event<Action>();
        var write = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _library.Writer.SetSongsLovedAsync(Arg.Any<IEnumerable<Guid>>(), true).Returns(write.Task);

        var favoriting = _viewModel.ToggleFavoriteCommand.ExecuteAsync(null);
        _playback.CurrentTrack.Returns(next);
        _playback.TrackChanged += Raise.Event<Action>();
        write.SetResult(true);
        await favoriting;

        _viewModel.IsCurrentTrackFavorite.Should().BeFalse();
        await _library.Writer.Received(1).SetSongsLovedAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.Single() == original.Id), true);
    }
}

public class RecentHistoryViewModelTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task PlaybackRefresh_PrependsNewListensWithoutResettingRowsOrLosingLoadedPages(int daysLater)
    {
        var library = Substitute.For<ILibraryReader>();
        var today = DateTime.Today.AddHours(12).ToUniversalTime();
        var listens = Enumerable.Range(0, 102)
            .Select(index => new RecentListen(index, Guid.NewGuid(), $"Song {index}", "Artist", today.AddMinutes(-index)))
            .ToArray();
        library.GetRecentListensAsync("", 0, 100, Arg.Any<CancellationToken>()).Returns(listens.Take(100).ToArray());
        library.GetRecentListensAsync("", 100, 100, Arg.Any<CancellationToken>()).Returns(listens.Skip(100).ToArray());
        using var viewModel = new RecentHistoryViewModel(library, new ImmediateDispatcher(), NullLogger.Instance);
        viewModel.SetActive(true);
        await viewModel.SearchAsync();
        await ((ISupportIncrementalLoading)viewModel.Entries).LoadMoreItemsAsync(100).AsTask();
        var originalTracks = viewModel.Entries.OfType<RecentListenEntry>().ToArray();
        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        viewModel.Entries.CollectionChanged += (_, args) => changes.Add(args.Action);
        var latest = new RecentListen(102, listens[0].SongId, "Played again", "Artist", today.AddDays(daysLater).AddMinutes(1));
        library.GetRecentListensAsync("", 0, 100, Arg.Any<CancellationToken>())
            .Returns(new[] { latest }.Concat(listens.Take(99)).ToArray());

        await viewModel.RefreshAsync();

        var tracks = viewModel.Entries.OfType<RecentListenEntry>().ToArray();
        tracks[0].Listen.Should().Be(latest);
        tracks.Skip(1).Should().Equal(originalTracks, (actual, original) => ReferenceEquals(actual, original));
        viewModel.Entries.OfType<RecentListenDateHeader>().Select(header => header.Date)
            .Should().Equal(daysLater == 0 ? [DateTime.Today] : new[] { DateTime.Today.AddDays(1), DateTime.Today });
        changes.Should().NotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset);
        viewModel.HasMore.Should().BeFalse();
        changes.Clear();

        await viewModel.RefreshAsync();

        changes.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchDuringIncrementalLoad_DoesNotBlockNewPagesOrAppendOldResults()
    {
        static RecentListen[] FullPage(string title) => Enumerable.Range(0, 100)
            .Select(index => new RecentListen(index, Guid.NewGuid(), title, "Artist", DateTime.UtcNow)).ToArray();
        var library = Substitute.For<ILibraryReader>();
        library.GetRecentListensAsync("", 0, 100, Arg.Any<CancellationToken>()).Returns(FullPage("Original"));
        library.GetRecentListensAsync("New", 0, 100, Arg.Any<CancellationToken>()).Returns(FullPage("New"));
        library.GetRecentListensAsync("New", 100, 100, Arg.Any<CancellationToken>())
            .Returns([new RecentListen(100, Guid.NewGuid(), "New page", "Artist", DateTime.UtcNow)]);
        var oldPage = new TaskCompletionSource<IReadOnlyList<RecentListen>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        library.GetRecentListensAsync("", 100, 100, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            entered.TrySetResult();
            return oldPage.Task;
        });
        using var viewModel = new RecentHistoryViewModel(library, new ImmediateDispatcher(), NullLogger.Instance);
        viewModel.SetActive(true);
        await viewModel.SearchAsync();
        var entries = (ISupportIncrementalLoading)viewModel.Entries;
        var pending = entries.LoadMoreItemsAsync(100).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.SearchTerm = "New";
        await viewModel.SearchAsync();
        (await entries.LoadMoreItemsAsync(100).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Count.Should().Be(1);
        oldPage.SetResult([new RecentListen(101, Guid.NewGuid(), "Stale", "Artist", DateTime.UtcNow)]);

        (await pending).Count.Should().Be(0);
        viewModel.Entries.OfType<RecentListenEntry>().Should().HaveCount(101).And.OnlyContain(entry => entry.Title.StartsWith("New"));
    }

    [Fact]
    public async Task IncrementalHistory_CoalescesRequestsAndPreservesDateGroupsAcrossPages()
    {
        var library = Substitute.For<ILibraryReader>();
        var today = DateTime.Today.AddHours(12).ToUniversalTime();
        var firstPage = Enumerable.Range(0, 100)
            .Select(index => new RecentListen(index, Guid.NewGuid(), $"Song {index}", "Artist", today.AddMinutes(-index)))
            .ToArray();
        library.GetRecentListensAsync("", 0, 100, Arg.Any<CancellationToken>()).Returns(firstPage);
        var nextPage = new TaskCompletionSource<IReadOnlyList<RecentListen>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        library.GetRecentListensAsync("", 100, 100, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            entered.TrySetResult();
            return nextPage.Task;
        });
        using var viewModel = new RecentHistoryViewModel(library, new ImmediateDispatcher(), NullLogger.Instance);
        viewModel.SetActive(true);
        await viewModel.SearchAsync();
        var entries = (ISupportIncrementalLoading)viewModel.Entries;
        var firstLoad = entries.LoadMoreItemsAsync(100).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicateLoad = entries.LoadMoreItemsAsync(100).AsTask();
        nextPage.SetResult([
            new RecentListen(100, Guid.NewGuid(), "Earlier today", "Artist", today.AddHours(-2)),
            new RecentListen(101, Guid.NewGuid(), "Yesterday", "Artist", today.AddDays(-1))
        ]);
        (await firstLoad).Count.Should().Be(3);
        (await duplicateLoad).Count.Should().Be(3);

        viewModel.Entries.OfType<RecentListenEntry>().Should().HaveCount(102);
        viewModel.Entries.OfType<RecentListenDateHeader>().Select(header => header.Title)
            .Should().Equal(Nagi.WinUI.Resources.Strings.Listening_Today, Nagi.WinUI.Resources.Strings.Listening_Yesterday);
        entries.HasMoreItems.Should().BeFalse();
        await library.Received(1).GetRecentListensAsync("", 100, 100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchSupersedesPendingLoadAndHistoryLoadsOnlyWhileVisible()
    {
        var library = Substitute.For<ILibraryReader>();
        var old = new TaskCompletionSource<IReadOnlyList<RecentListen>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latest = new RecentListen(2, Guid.NewGuid(), "Found", "Artist", DateTime.UtcNow);
        library.GetRecentListensAsync("", 0, 100, Arg.Any<CancellationToken>()).Returns(_ => { oldEntered.TrySetResult(); return old.Task; });
        library.GetRecentListensAsync("Found", 0, 100, Arg.Any<CancellationToken>()).Returns(new[] { latest });
        using var viewModel = new RecentHistoryViewModel(library, new ImmediateDispatcher(), NullLogger.Instance);
        await viewModel.SearchAsync();
        await library.DidNotReceive().GetRecentListensAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        viewModel.SetActive(true);
        await oldEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.SearchTerm = "Found";
        await viewModel.SearchAsync();
        old.SetResult([new RecentListen(1, Guid.NewGuid(), "Stale", "Artist", DateTime.UtcNow)]);
        viewModel.SetActive(false);

        viewModel.Entries.OfType<RecentListenEntry>().Should().ContainSingle().Which.Listen.Should().Be(latest);
        await viewModel.SearchAsync();
        await library.Received(1).GetRecentListensAsync("Found", 0, 100, Arg.Any<CancellationToken>());
    }
}
