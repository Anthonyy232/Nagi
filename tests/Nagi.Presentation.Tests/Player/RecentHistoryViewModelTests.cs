using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Library;
using Nagi.Core.Listening;
using Nagi.Core.Playback;
using Nagi.Presentation.Common;
using Nagi.Presentation.Player;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests.Player;

public class RecentHistoryViewModelTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task PlaybackRefresh_PrependsNewListensWithoutResettingRowsOrLosingLoadedPages(int daysLater)
    {
        var library = Substitute.For<ILibraryReader>();
        var listening = Substitute.For<IListeningService>();
        var today = DateTime.Today.AddHours(12).ToUniversalTime();
        var listens = Enumerable.Range(0, 102)
            .Select(index => new RecentListen(index, Guid.NewGuid(), $"Song {index}", "Artist", today.AddMinutes(-index)))
            .ToArray();
        listening.GetRecentListensAsync("", 0, 100, Arg.Any<CancellationToken>()).Returns(listens.Take(100).ToArray());
        listening.GetRecentListensAsync("", 100, 100, Arg.Any<CancellationToken>()).Returns(listens.Skip(100).ToArray());
        using var viewModel = new RecentHistoryViewModel(library, listening, new ImmediateDispatcher(), NullLogger.Instance);
        viewModel.SetActive(true);
        await viewModel.SearchAsync();
        await ((IIncrementalItemSource)viewModel.Entries).LoadMoreItemsAsync();
        var originalTracks = viewModel.Entries.OfType<RecentListenEntry>().ToArray();
        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        viewModel.Entries.CollectionChanged += (_, args) => changes.Add(args.Action);
        var latest = new RecentListen(102, listens[0].SongId, "Played again", "Artist", today.AddDays(daysLater).AddMinutes(1));
        listening.GetRecentListensAsync("", 0, 100, Arg.Any<CancellationToken>())
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
        var listening = Substitute.For<IListeningService>();
        listening.GetRecentListensAsync("", 0, 100, Arg.Any<CancellationToken>()).Returns(FullPage("Original"));
        listening.GetRecentListensAsync("New", 0, 100, Arg.Any<CancellationToken>()).Returns(FullPage("New"));
        listening.GetRecentListensAsync("New", 100, 100, Arg.Any<CancellationToken>())
            .Returns([new RecentListen(100, Guid.NewGuid(), "New page", "Artist", DateTime.UtcNow)]);
        var oldPage = new TaskCompletionSource<IReadOnlyList<RecentListen>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listening.GetRecentListensAsync("", 100, 100, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            entered.TrySetResult();
            return oldPage.Task;
        });
        using var viewModel = new RecentHistoryViewModel(library, listening, new ImmediateDispatcher(), NullLogger.Instance);
        viewModel.SetActive(true);
        await viewModel.SearchAsync();
        var entries = (IIncrementalItemSource)viewModel.Entries;
        var pending = entries.LoadMoreItemsAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.SearchTerm = "New";
        await viewModel.SearchAsync();
        (await entries.LoadMoreItemsAsync().WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(1);
        oldPage.SetResult([new RecentListen(101, Guid.NewGuid(), "Stale", "Artist", DateTime.UtcNow)]);

        (await pending).Should().Be(0);
        viewModel.Entries.OfType<RecentListenEntry>().Should().HaveCount(101).And.OnlyContain(entry => entry.Title.StartsWith("New"));
    }

    [Fact]
    public async Task IncrementalHistory_CoalescesRequestsAndPreservesDateGroupsAcrossPages()
    {
        var library = Substitute.For<ILibraryReader>();
        var listening = Substitute.For<IListeningService>();
        var today = DateTime.Today.AddHours(12).ToUniversalTime();
        var firstPage = Enumerable.Range(0, 100)
            .Select(index => new RecentListen(index, Guid.NewGuid(), $"Song {index}", "Artist", today.AddMinutes(-index)))
            .ToArray();
        listening.GetRecentListensAsync("", 0, 100, Arg.Any<CancellationToken>()).Returns(firstPage);
        var nextPage = new TaskCompletionSource<IReadOnlyList<RecentListen>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listening.GetRecentListensAsync("", 100, 100, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            entered.TrySetResult();
            return nextPage.Task;
        });
        using var viewModel = new RecentHistoryViewModel(library, listening, new ImmediateDispatcher(), NullLogger.Instance);
        viewModel.SetActive(true);
        await viewModel.SearchAsync();
        var entries = (IIncrementalItemSource)viewModel.Entries;
        var firstLoad = entries.LoadMoreItemsAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicateLoad = entries.LoadMoreItemsAsync();
        nextPage.SetResult([
            new RecentListen(100, Guid.NewGuid(), "Earlier today", "Artist", today.AddHours(-2)),
            new RecentListen(101, Guid.NewGuid(), "Yesterday", "Artist", today.AddDays(-1))
        ]);
        (await firstLoad).Should().Be(3);
        (await duplicateLoad).Should().Be(3);

        viewModel.Entries.OfType<RecentListenEntry>().Should().HaveCount(102);
        viewModel.Entries.OfType<RecentListenDateHeader>().Select(header => header.Title)
            .Should().Equal(Strings.Listening_Today, Strings.Listening_Yesterday);
        entries.HasMoreItems.Should().BeFalse();
        await listening.Received(1).GetRecentListensAsync("", 100, 100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchSupersedesPendingLoadAndHistoryLoadsOnlyWhileVisible()
    {
        var library = Substitute.For<ILibraryReader>();
        var listening = Substitute.For<IListeningService>();
        var old = new TaskCompletionSource<IReadOnlyList<RecentListen>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latest = new RecentListen(2, Guid.NewGuid(), "Found", "Artist", DateTime.UtcNow);
        listening.GetRecentListensAsync("", 0, 100, Arg.Any<CancellationToken>()).Returns(_ => { oldEntered.TrySetResult(); return old.Task; });
        listening.GetRecentListensAsync("Found", 0, 100, Arg.Any<CancellationToken>()).Returns(new[] { latest });
        using var viewModel = new RecentHistoryViewModel(library, listening, new ImmediateDispatcher(), NullLogger.Instance);
        await viewModel.SearchAsync();
        await listening.DidNotReceive().GetRecentListensAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        viewModel.SetActive(true);
        await oldEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.SearchTerm = "Found";
        await viewModel.SearchAsync();
        viewModel.SetActive(false);

        viewModel.Entries.OfType<RecentListenEntry>().Should().ContainSingle().Which.Listen.Should().Be(latest);
        await viewModel.SearchAsync();
        await listening.Received(1).GetRecentListensAsync("Found", 0, 100, Arg.Any<CancellationToken>());
    }
}
