using FluentAssertions;
using Nagi.Core.Library;
using Nagi.Core.Playback;
using Nagi.Core.Settings;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests.Playback;

public partial class MusicPlaybackServiceTests
{
    [Fact]
    public async Task PlayAsync_ServerSongThatCantStream_StopsAndReportsItWithoutCreditingAnyListening()
    {
        await _service.InitializeAsync();
        var song = _testSongs[0];
        song.RemoteId = "s1";
        _musicServers.GetStreamUriAsync(song).Returns((Uri?)null);
        Song? failed = null;
        _service.ServerSongFailed += s => failed = s;

        await _service.PlayAsync(new[] { song.Id }, startIndex: 0);

        _service.CurrentTrack.Should().BeNull();
        failed.Should().BeSameAs(song);
        await _library.Listening.DidNotReceive().StartListenSessionAsync(song.Id, Arg.Any<PlaybackContext>());
        await _audioPlayer.DidNotReceiveWithAnyArgs().LoadAsync(default!, default);
    }

    [Fact]
    public async Task AudioError_ReportsAServerSongButNotASongOnDisk()
    {
        await _service.InitializeAsync();
        var failed = new List<Song>();
        _service.ServerSongFailed += failed.Add;
        await _service.PlayAsync(_testSongs[0].Id);
        _audioPlayer.ErrorOccurred += Raise.Event<Action>();
        await _service.FlushPendingFinalizationAsync();

        var song = _testSongs[1];
        song.RemoteId = "s1";
        _musicServers.GetStreamUriAsync(song).Returns(new Uri("https://music.example.com/rest/stream.view?id=s1"));
        await _service.PlayAsync(song.Id);
        _audioPlayer.ErrorOccurred += Raise.Event<Action>();

        failed.Should().ContainSingle().Which.Should().BeSameAs(song);
    }

    [Fact]
    public async Task InitializeAsync_RestoringAServerSongThatCantStream_KeepsItsPlaceForPlay()
    {
        var song = _testSongs[1];
        song.RemoteId = "s1";
        _musicServers.GetStreamUriAsync(song).Returns((Uri?)null);
        _settingsService.Set(CoreSettings.RestorePlaybackState, true);
        _playbackStateStore.LoadAsync().Returns(new PlaybackState
        {
            PositionId = song.Id,
            PlaybackQueueTrackIds = _testSongs.Select(s => s.Id).ToList(),
            IsLoaded = true
        });

        await _service.InitializeAsync();

        _service.CurrentTrack.Should().BeNull();
        await _audioPlayer.DidNotReceiveWithAnyArgs().LoadAsync(default!, default);

        // Signed in again since, so Play picks up where the queue left off.
        var stream = new Uri("https://music.example.com/rest/stream.view?id=s1");
        _musicServers.GetStreamUriAsync(song).Returns(stream);
        await _service.PlayPauseAsync();

        _service.CurrentTrack.Should().BeSameAs(song);
        await _audioPlayer.Received(1).LoadAsync(song, stream);
    }
}
