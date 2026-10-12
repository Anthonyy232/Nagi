using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Tests that verify <see cref="MusicPlaybackService"/> independently marks listen sessions as
///     eligible for scrobbling once the standard threshold is met (track &gt; 30 s, played ≥ 50% or
///     ≥ 4 min), regardless of whether Last.fm is configured.
/// </summary>
public class MusicPlaybackServiceScrobblingTests : IAsyncDisposable
{
    private readonly IAudioPlayer _audioPlayer;
    private readonly ILibraryFake _libraryService;
    private readonly MusicPlaybackService _service;
    private readonly List<Song> _testSongs;
    private readonly List<(Song Song, long SessionId, DateTime ListenStartedUtc)> _raisedEvents = new();

    public MusicPlaybackServiceScrobblingTests()
    {
        _audioPlayer = Substitute.For<IAudioPlayer>();
        _libraryService = Substitute.For<ILibraryFake>();
        _libraryService.MarkListenAsEligibleForScrobblingAsync(Arg.Any<long>()).Returns(true);
        var settingsService = Substitute.For<ISettingsService>();
        var metadataService = Substitute.For<IMetadataService>();
        var logger = Substitute.For<ILogger<MusicPlaybackService>>();

        settingsService.GetInitialVolumeAsync().Returns(0.5);

        _audioPlayer.GetEqualizerBands().Returns(new List<(uint, float)> { (0, 60f), (1, 170f) });

        _service = new MusicPlaybackService(
            settingsService,
            _audioPlayer,
            _libraryService, _libraryService,
            metadataService,
            logger);

        _testSongs = Enumerable.Range(1, 2)
            .Select(i => new Song { Id = Guid.NewGuid(), Title = $"Song {i}", FilePath = $"C:\\music\\song{i}.mp3" })
            .ToList();

        foreach (var song in _testSongs)
            _libraryService.GetSongByIdAsync(song.Id).Returns(song);

        _service.ScrobbleEligibilityReached +=
            (song, sessionId, listenStartedUtc) => _raisedEvents.Add((song, sessionId, listenStartedUtc));
    }

    public ValueTask DisposeAsync() => _service.DisposeAsync();

    private void RaisePositionChanged() => _audioPlayer.PositionChanged += Raise.Event<Action>();

    [Fact]
    public async Task Eligibility_DuringTrackLoad_DoesNotCountThePreviousTracksPosition()
    {
        _libraryService.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(1L);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _audioPlayer.LoadAsync(_testSongs[0]).Returns(release.Task);
        var playback = _service.PlayAsync(_testSongs[0]);
        _audioPlayer.Duration.Returns(TimeSpan.FromMinutes(3));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(91));
        _audioPlayer.IsPlaying.Returns(true);
        _audioPlayer.StateChanged += Raise.Event<Action>();

        RaisePositionChanged();
        release.SetResult();
        await playback;
        await _service.FlushPendingFinalizationAsync();

        _raisedEvents.Should().BeEmpty();
        await _libraryService.DidNotReceive().MarkListenAsEligibleForScrobblingAsync(Arg.Any<long>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Eligibility_WhenDatabaseUpdateFails_RetriesWithoutEmittingSuccess(bool throws)
    {
        _libraryService.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(1L);
        _libraryService.MarkListenAsEligibleForScrobblingAsync(1L).Returns(_ =>
            throws ? Task.FromException<bool>(new InvalidOperationException("Database unavailable")) : Task.FromResult(false));
        await _service.PlayAsync(_testSongs[0]);
        _audioPlayer.Duration.Returns(TimeSpan.FromMinutes(3));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(91));

        RaisePositionChanged();
        await _service.FlushPendingFinalizationAsync();
        _raisedEvents.Should().BeEmpty();
        _libraryService.MarkListenAsEligibleForScrobblingAsync(1L).Returns(true);
        RaisePositionChanged();
        await _service.FlushPendingFinalizationAsync();

        _raisedEvents.Should().ContainSingle();
        await _libraryService.Received(2).MarkListenAsEligibleForScrobblingAsync(1L);
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
        _libraryService.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(1L);
        await _service.PlayAsync(_testSongs[0]);
        _audioPlayer.Duration.Returns(TimeSpan.FromSeconds(durationSeconds));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(playedSeconds));

        RaisePositionChanged();
        await _service.FlushPendingFinalizationAsync();

        await _libraryService.Received(eligible ? 1 : 0).MarkListenAsEligibleForScrobblingAsync(1L);
        _raisedEvents.Should().HaveCount(eligible ? 1 : 0);
        if (eligible)
        {
            _raisedEvents[0].Song.Id.Should().Be(_testSongs[0].Id);
            _raisedEvents[0].SessionId.Should().Be(1L);
            _raisedEvents[0].ListenStartedUtc.Should().NotBe(default);
        }
    }

    [Fact]
    public async Task OnPositionChanged_WhenNoActiveSession_DoesNotMarkEligible()
    {
        await _service.InitializeAsync();

        _audioPlayer.Duration.Returns(TimeSpan.FromMinutes(3));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromMinutes(2));

        RaisePositionChanged();
        await _service.FlushPendingFinalizationAsync();

        await _libraryService.DidNotReceive().MarkListenAsEligibleForScrobblingAsync(Arg.Any<long>());
    }

    [Fact]
    public async Task OnPositionChanged_AfterEligibilityMet_DoesNotMarkAgain()
    {
        await _service.InitializeAsync();
        _libraryService.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(5L);
        await _service.PlayAsync(_testSongs[0]);

        _audioPlayer.Duration.Returns(TimeSpan.FromMinutes(3));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(91));

        RaisePositionChanged();
        RaisePositionChanged();
        await _service.FlushPendingFinalizationAsync();

        await _libraryService.Received(1).MarkListenAsEligibleForScrobblingAsync(5L);
        _raisedEvents.Should().ContainSingle();
    }

    [Fact]
    public async Task EligibilityEvent_WhenDatabaseMarkIsDelayed_KeepsOriginalSessionStartTime()
    {
        await _service.InitializeAsync();
        _libraryService.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(20L);
        _libraryService.StartListenSessionAsync(_testSongs[1].Id, Arg.Any<PlaybackContext>()).Returns(21L);
        var markStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMark = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _libraryService.MarkListenAsEligibleForScrobblingAsync(20L).Returns(_ =>
        {
            markStarted.TrySetResult();
            return releaseMark.Task;
        });

        await _service.PlayAsync(_testSongs, startIndex: 0);
        var firstSessionStartedBy = DateTime.UtcNow;
        _audioPlayer.Duration.Returns(TimeSpan.FromMinutes(3));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(91));
        RaisePositionChanged();
        await markStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await _service.NextAsync();
        releaseMark.TrySetResult(true);
        await _service.FlushPendingFinalizationAsync();

        _raisedEvents.Should().ContainSingle();
        _raisedEvents[0].SessionId.Should().Be(20L);
        _raisedEvents[0].ListenStartedUtc.Should().BeOnOrBefore(firstSessionStartedBy);
    }

    [Fact]
    public async Task OnPositionChanged_AfterTrackChange_ResetsEligibilityAndMarksNewSession()
    {
        await _service.InitializeAsync();
        _libraryService.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(10L);
        _libraryService.StartListenSessionAsync(_testSongs[1].Id, Arg.Any<PlaybackContext>()).Returns(11L);
        await _service.PlayAsync(_testSongs, startIndex: 0);

        _audioPlayer.Duration.Returns(TimeSpan.FromMinutes(3));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(91));
        RaisePositionChanged();
        await _service.FlushPendingFinalizationAsync();

        await _service.NextAsync();
        await _service.FlushPendingFinalizationAsync();

        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(91));
        RaisePositionChanged();
        await _service.FlushPendingFinalizationAsync();

        await _libraryService.Received(1).MarkListenAsEligibleForScrobblingAsync(10L);
        await _libraryService.Received(1).MarkListenAsEligibleForScrobblingAsync(11L);
    }
}
