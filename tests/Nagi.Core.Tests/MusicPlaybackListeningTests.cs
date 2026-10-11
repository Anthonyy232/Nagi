using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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

}
