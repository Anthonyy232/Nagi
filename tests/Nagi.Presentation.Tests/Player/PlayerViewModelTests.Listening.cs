using FluentAssertions;
using Nagi.Core.Library;
using Nagi.Core.Listening;
using Nagi.Core.Playback;
using Nagi.Presentation.Player;
using NSubstitute;
using Xunit;

namespace Nagi.Presentation.Tests.Player;

public partial class PlayerViewModelTests
{
    [Fact]
    public void UpNext_ShowsTheUpcomingQueueAndFollowsItWhenGoingBackOrRepeating()
    {
        var songs = Enumerable.Range(0, 6).Select(index => new Song { Title = $"Song {index}" }).ToArray();
        var ids = songs.Select(song => song.Id).ToArray();
        _playback.GetQueueSongsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(songs.ToDictionary(song => song.Id));
        _viewModel.IsQueuePanelOpen = true;

        foreach (var index in new[] { 3, 4, 2, 0 })
        {
            var upcoming = ids.Skip(index).Concat(ids.Take(index)).ToArray();
            _playback.UpcomingQueue.Returns(upcoming);
            _playback.TrackChanged += Raise.Event<Action>();

            _viewModel.SidebarQueue.Select(entry => entry.Id).Should().Equal(upcoming);
            _viewModel.SidebarQueue.Select(entry => entry.Position).Should().Equal(Enumerable.Range(1, ids.Length));
        }

        var shuffled = ids.Reverse().ToArray();
        _playback.UpcomingQueue.Returns(shuffled);
        _playback.ShuffleModeChanged += Raise.Event<Action>();
        _viewModel.SidebarQueue.Select(entry => entry.Id).Should().Equal(shuffled);
    }

    [Fact]
    public async Task HistoryPlayback_PreservesTheQueueAndSelectsTheRequestedTrackEvenWithRepeatOne()
    {
        var historical = Guid.NewGuid();
        _playback.CurrentRepeatMode.Returns(RepeatMode.RepeatOne);
        var entry = new RecentListenEntry(new RecentListen(1, historical, "Historical", "Artist", DateTime.UtcNow));

        await _viewModel.PlayHistoryNowCommand.ExecuteAsync(entry);

        await _playback.Received(1).PlayNextAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(new[] { historical })));
        await _playback.Received(1).PlayQueueItemAsync(historical);
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
        _library.Listening.SetSongsLovedAsync(Arg.Any<IEnumerable<Guid>>(), true).Returns(write.Task);

        var favoriting = _viewModel.ToggleFavoriteCommand.ExecuteAsync(null);
        _playback.CurrentTrack.Returns(next);
        _playback.TrackChanged += Raise.Event<Action>();
        write.SetResult(true);
        await favoriting;

        _viewModel.IsCurrentTrackFavorite.Should().BeFalse();
        await _library.Listening.Received(1).SetSongsLovedAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.Single() == original.Id), true);
    }
}
