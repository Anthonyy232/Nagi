using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public partial class MusicPlaybackServiceTests
{

    [Fact]
    public async Task SupersededMix_DoesNotReplaceTheQueueOrReportAnEmptySelection()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs);
        var lookup = new TaskCompletionSource<IReadOnlyList<MixCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _libraryService.GetMixCandidatesAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(lookup.Task);
        var mixing = _service.PlayMixAsync(_testSongs.Select(s => s.Id), ShuffleMode.Unheard);
        await _service.NextAsync();
        lookup.SetResult(_testSongs.Select(s => new MixCandidate(s.Id, "Artist", 1, 0, null)).ToArray());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mixing);
        _service.PlaybackQueue.Should().Equal(_testSongs.Select(s => s.Id));
        _service.CurrentTrack.Should().BeSameAs(_testSongs[1]);
    }

    [Fact]
    public async Task EmptyMix_LeavesCurrentPlaybackAndQueueUntouched()
    {
        await _service.InitializeAsync();
        await _service.PlayAsync(_testSongs);
        _libraryService.GetMixCandidatesAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MixCandidate>());
        _audioPlayer.ClearReceivedCalls();

        (await _service.PlayMixAsync(_testSongs.Select(s => s.Id), ShuffleMode.Unheard)).Should().BeFalse();

        _service.PlaybackQueue.Should().Equal(_testSongs.Select(s => s.Id));
        _service.CurrentTrack.Should().BeSameAs(_testSongs[0]);
        await _audioPlayer.DidNotReceive().StopAsync();
        await _audioPlayer.DidNotReceive().LoadAsync(Arg.Any<Song>());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExpiredTimer_FinishesOnlyAnActivelyPlayingTrack(bool finishTrack, bool playing)
    {
        var clock = new FakeTimeProvider();
        await using var service = new MusicPlaybackService(_settingsService, _audioPlayer, _libraryService,
            _metadataService, NullLogger<MusicPlaybackService>.Instance, clock);
        await service.InitializeAsync();
        await service.PlayAsync(_testSongs);
        _audioPlayer.MediaOpened += Raise.Event<Action>();
        _audioPlayer.IsPlaying.Returns(playing);
        service.ScheduleStop(PlaybackStopMode.Timer, TimeSpan.FromMinutes(1), finishTrack);
        clock.Advance(TimeSpan.FromMinutes(2));

        await service.CheckStopScheduleAsync();

        if (finishTrack && playing)
        {
            service.StopMode.Should().Be(PlaybackStopMode.AfterTrack);
            await service.SetRepeatModeAsync(RepeatMode.RepeatOne);
            _audioPlayer.PlaybackEnded += Raise.Event<Action>();
            await service.FlushPendingFinalizationAsync();
        }
        service.CurrentTrack.Should().BeNull();
        service.StopMode.Should().Be(PlaybackStopMode.Off);
    }

}
