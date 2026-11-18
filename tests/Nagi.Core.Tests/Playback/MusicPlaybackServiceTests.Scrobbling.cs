using AwesomeAssertions;
using Nagi.Core.Playback;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.Playback;

// Tests that verify the playback service independently marks listen sessions as
// eligible for scrobbling once the standard threshold is met (track > 30 s, played ≥ 50% or
// ≥ 4 min), regardless of whether Last.fm is configured.
public partial class MusicPlaybackServiceTests
{
    private int _eligibilityEvents;

    private void RaisePositionChanged() => _audioPlayer.PositionChanged += Raise.Event<Action>();

    [Fact]
    public async Task Eligibility_DuringTrackLoad_DoesNotCountThePreviousTracksPosition()
    {
        _library.Listening.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(1L);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _audioPlayer.LoadAsync(_testSongs[0]).Returns(release.Task);
        var playback = _service.PlayAsync(_testSongs[0].Id);
        _audioPlayer.Duration.Returns(TimeSpan.FromMinutes(3));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(91));
        _audioPlayer.IsPlaying.Returns(true);
        _audioPlayer.StateChanged += Raise.Event<Action>();

        RaisePositionChanged();
        release.SetResult();
        await playback;
        await _service.AwaitPendingOperationsAsync();

        _eligibilityEvents.Should().Be(0);
        await _library.Listening.DidNotReceive().MarkListenAsEligibleForScrobblingAsync(Arg.Any<long>());
    }

    [Fact]
    public async Task Eligibility_WhenTheListenWasClearedFromHistory_IsNotRetried()
    {
        _library.Listening.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(1L);
        _library.Listening.MarkListenAsEligibleForScrobblingAsync(1L).Returns(false);
        await _service.PlayAsync(_testSongs[0].Id);
        _audioPlayer.Duration.Returns(TimeSpan.FromMinutes(3));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(91));

        RaisePositionChanged();
        await _service.AwaitPendingOperationsAsync();
        RaisePositionChanged();
        await _service.AwaitPendingOperationsAsync();

        _eligibilityEvents.Should().Be(0);
        await _library.Listening.Received(1).MarkListenAsEligibleForScrobblingAsync(1L);
    }

    [Fact]
    public async Task Eligibility_WhenDatabaseUpdateFails_RetriesWithoutEmittingSuccess()
    {
        _library.Listening.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(1L);
        _library.Listening.MarkListenAsEligibleForScrobblingAsync(1L)
            .Returns(Task.FromException<bool>(new InvalidOperationException("Database unavailable")));
        await _service.PlayAsync(_testSongs[0].Id);
        _audioPlayer.Duration.Returns(TimeSpan.FromMinutes(3));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(91));

        RaisePositionChanged();
        await _service.AwaitPendingOperationsAsync();
        _eligibilityEvents.Should().Be(0);
        _library.Listening.MarkListenAsEligibleForScrobblingAsync(1L).Returns(true);
        RaisePositionChanged();
        await _service.AwaitPendingOperationsAsync();

        _eligibilityEvents.Should().Be(1);
        await _library.Listening.Received(2).MarkListenAsEligibleForScrobblingAsync(1L);
    }

    [Theory]
    [InlineData(180, 90, true)]
    [InlineData(600, 240, true)]
    [InlineData(30, 20, false)]
    [InlineData(180, 30, false)]
    public async Task OnPositionChanged_RespectsDurationAndListeningThresholds(
        int durationSeconds, int playedSeconds, bool eligible)
    {
        await _service.InitializeAsync();
        _library.Listening.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(1L);
        await _service.PlayAsync(_testSongs[0].Id);
        _audioPlayer.Duration.Returns(TimeSpan.FromSeconds(durationSeconds));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(playedSeconds));

        RaisePositionChanged();
        await _service.AwaitPendingOperationsAsync();

        await _library.Listening.Received(eligible ? 1 : 0).MarkListenAsEligibleForScrobblingAsync(1L);
        _eligibilityEvents.Should().Be(eligible ? 1 : 0);
    }

    [Fact]
    public async Task OnPositionChanged_WhenNoActiveSession_DoesNotMarkEligible()
    {
        await _service.InitializeAsync();

        _audioPlayer.Duration.Returns(TimeSpan.FromMinutes(3));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromMinutes(2));

        RaisePositionChanged();
        await _service.AwaitPendingOperationsAsync();

        await _library.Listening.DidNotReceive().MarkListenAsEligibleForScrobblingAsync(Arg.Any<long>());
    }

    [Fact]
    public async Task OnPositionChanged_AfterEligibilityMet_DoesNotMarkAgain()
    {
        await _service.InitializeAsync();
        _library.Listening.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(5L);
        await _service.PlayAsync(_testSongs[0].Id);

        _audioPlayer.Duration.Returns(TimeSpan.FromMinutes(3));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(91));

        RaisePositionChanged();
        RaisePositionChanged();
        await _service.AwaitPendingOperationsAsync();

        await _library.Listening.Received(1).MarkListenAsEligibleForScrobblingAsync(5L);
        _eligibilityEvents.Should().Be(1);
    }

    [Fact]
    public async Task EligibilityEvent_WhenTheMarkFinishesAfterATrackChange_IsStillRaised()
    {
        await _service.InitializeAsync();
        _library.Listening.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(20L);
        _library.Listening.StartListenSessionAsync(_testSongs[1].Id, Arg.Any<PlaybackContext>()).Returns(21L);
        var markStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMark = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _library.Listening.MarkListenAsEligibleForScrobblingAsync(20L).Returns(_ =>
        {
            markStarted.TrySetResult();
            return releaseMark.Task;
        });

        await _service.PlayAsync(_testSongs.Select(song => song.Id), startIndex: 0);
        _audioPlayer.Duration.Returns(TimeSpan.FromMinutes(3));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(91));
        RaisePositionChanged();
        await markStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await _service.NextAsync();
        releaseMark.TrySetResult(true);
        await _service.AwaitPendingOperationsAsync();

        _eligibilityEvents.Should().Be(1);
        await _library.Listening.DidNotReceive().MarkListenAsEligibleForScrobblingAsync(21L);
    }

    [Fact]
    public async Task OnPositionChanged_AfterTrackChange_ResetsEligibilityAndMarksNewSession()
    {
        await _service.InitializeAsync();
        _library.Listening.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(10L);
        _library.Listening.StartListenSessionAsync(_testSongs[1].Id, Arg.Any<PlaybackContext>()).Returns(11L);
        await _service.PlayAsync(_testSongs.Select(song => song.Id), startIndex: 0);

        _audioPlayer.Duration.Returns(TimeSpan.FromMinutes(3));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(91));
        RaisePositionChanged();
        await _service.AwaitPendingOperationsAsync();

        await _service.NextAsync();
        await _service.AwaitPendingOperationsAsync();

        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(91));
        RaisePositionChanged();
        await _service.AwaitPendingOperationsAsync();

        await _library.Listening.Received(1).MarkListenAsEligibleForScrobblingAsync(10L);
        await _library.Listening.Received(1).MarkListenAsEligibleForScrobblingAsync(11L);
    }
}
