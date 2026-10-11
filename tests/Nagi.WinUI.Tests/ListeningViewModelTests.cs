using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.WinUI.Models;
using Nagi.WinUI.Services.Implementations;
using Nagi.WinUI.ViewModels;
using NSubstitute;
using Xunit;

namespace Nagi.WinUI.Tests;

public partial class PlayerViewModelTests
{
    [Fact]
    public async Task HistoryPlayback_PreservesTheQueueAndSelectsTheRequestedTrackEvenWithRepeatOne()
    {
        var current = Guid.NewGuid();
        var historical = Guid.NewGuid();
        _playback.PlaybackQueue.Returns(new[] { current, historical });
        _playback.CurrentRepeatMode.Returns(RepeatMode.RepeatOne);
        var entry = new RecentListenEntry(new RecentListen(1, historical, "Historical", "Artist", DateTime.UtcNow), "");

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
        _library.SetSongsLovedAsync(Arg.Any<IEnumerable<Guid>>(), true).Returns(write.Task);

        var favoriting = _viewModel.ToggleFavoriteCommand.ExecuteAsync(null);
        _playback.CurrentTrack.Returns(next);
        _playback.TrackChanged += Raise.Event<Action>();
        write.SetResult(true);
        await favoriting;

        _viewModel.IsCurrentTrackFavorite.Should().BeFalse();
        await _library.Received(1).SetSongsLovedAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.Single() == original.Id), true);
    }
}

public class RecentHistoryViewModelTests
{
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

        viewModel.Entries.Should().ContainSingle().Which.Listen.Should().Be(latest);
        await viewModel.SearchAsync();
        await library.Received(1).GetRecentListensAsync("Found", 0, 100, Arg.Any<CancellationToken>());
    }
}
