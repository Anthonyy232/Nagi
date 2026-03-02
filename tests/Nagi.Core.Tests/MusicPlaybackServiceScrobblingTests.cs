using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Implementations;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

/// <summary>
///     Tests that verify <see cref="MusicPlaybackService"/> independently marks listen sessions as
///     eligible for scrobbling once the standard threshold is met (track &gt; 30 s, played ≥ 50% or
///     ≥ 4 min), regardless of whether Last.fm is configured.
/// </summary>
public class MusicPlaybackServiceScrobblingTests
{
    private readonly IAudioPlayer _audioPlayer;
    private readonly ILibraryService _libraryService;
    private readonly MusicPlaybackService _service;
    private readonly List<Song> _testSongs;
    private readonly List<(Song Song, long SessionId)> _raisedEvents = new();

    public MusicPlaybackServiceScrobblingTests()
    {
        _audioPlayer = Substitute.For<IAudioPlayer>();
        _libraryService = Substitute.For<ILibraryService>();
        var settingsService = Substitute.For<ISettingsService>();
        var metadataService = Substitute.For<IMetadataService>();
        var logger = Substitute.For<ILogger<MusicPlaybackService>>();

        settingsService.GetInitialVolumeAsync().Returns(0.5);
        settingsService.GetInitialMuteStateAsync().Returns(false);
        settingsService.GetInitialShuffleStateAsync().Returns(false);
        settingsService.GetInitialRepeatModeAsync().Returns(RepeatMode.Off);
        settingsService.GetRestorePlaybackStateEnabledAsync().Returns(false);
        settingsService.GetPlaybackStateAsync().Returns((PlaybackState?)null);
        settingsService.GetEqualizerSettingsAsync().Returns((EqualizerSettings?)null);

        _audioPlayer.GetEqualizerBands().Returns(new List<(uint, float)> { (0, 60f), (1, 170f) });

        _service = new MusicPlaybackService(
            settingsService,
            _audioPlayer,
            _libraryService,
            metadataService,
            logger);

        _testSongs = Enumerable.Range(1, 3)
            .Select(i => new Song { Id = Guid.NewGuid(), Title = $"Song {i}", FilePath = $"C:\\music\\song{i}.mp3" })
            .ToList();

        foreach (var song in _testSongs)
            _libraryService.GetSongByIdAsync(song.Id).Returns(song);

        _service.ScrobbleEligibilityReached += (s, id) => _raisedEvents.Add((s, id));
    }

    /// <summary>
    ///     Simulates the <c>PositionChanged</c> event being raised by the audio player.
    ///     This is the trigger that calls <c>MaybeMarkEligibleForScrobbling</c>.
    /// </summary>
    private void RaisePositionChanged() => _audioPlayer.PositionChanged += Raise.Event<Action>();

    // ──────────────────────────────────────────────────────────────────────────
    // Eligibility is marked correctly
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnPositionChanged_WhenHalfDurationPlayed_MarksSessionEligible()
    {
        // Arrange — 3-minute track, 91 seconds played (≥ 50%)
        await _service.InitializeAsync();
        _libraryService.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(1L);
        await _service.PlayAsync(_testSongs[0]);

        _audioPlayer.Duration.Returns(TimeSpan.FromMinutes(3));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(91));

        // Act
        RaisePositionChanged();
        await _service.FlushPendingFinalizationAsync();

        // Assert
        await _libraryService.Received(1).MarkListenAsEligibleForScrobblingAsync(1L);
        _raisedEvents.Should().ContainSingle();
        _raisedEvents[0].Song.Id.Should().Be(_testSongs[0].Id);
        _raisedEvents[0].SessionId.Should().Be(1L);
    }

    [Fact]
    public async Task OnPositionChanged_WhenFourMinutesPlayed_MarksSessionEligible()
    {
        // Arrange — 10-minute track, 4 minutes played (< 50% but meets 4-min rule)
        await _service.InitializeAsync();
        _libraryService.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(2L);
        await _service.PlayAsync(_testSongs[0]);

        _audioPlayer.Duration.Returns(TimeSpan.FromMinutes(10));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromMinutes(4));

        // Act
        RaisePositionChanged();
        await _service.FlushPendingFinalizationAsync();

        // Assert
        await _libraryService.Received(1).MarkListenAsEligibleForScrobblingAsync(2L);
        _raisedEvents.Should().ContainSingle();
        _raisedEvents[0].SessionId.Should().Be(2L);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Eligibility is NOT marked when threshold is not met
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnPositionChanged_WhenTrackIsTooShort_DoesNotMarkEligible()
    {
        // Arrange — 30-second track (must be > 30 s)
        await _service.InitializeAsync();
        _libraryService.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(3L);
        await _service.PlayAsync(_testSongs[0]);

        _audioPlayer.Duration.Returns(TimeSpan.FromSeconds(30));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(20));

        // Act
        RaisePositionChanged();
        await _service.FlushPendingFinalizationAsync();

        // Assert
        await _libraryService.DidNotReceive().MarkListenAsEligibleForScrobblingAsync(Arg.Any<long>());
        _raisedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task OnPositionChanged_WhenThresholdNotYetMet_DoesNotMarkEligible()
    {
        // Arrange — 3-minute track, only 30 seconds played (< 50%)
        await _service.InitializeAsync();
        _libraryService.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(4L);
        await _service.PlayAsync(_testSongs[0]);

        _audioPlayer.Duration.Returns(TimeSpan.FromMinutes(3));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(30));

        // Act
        RaisePositionChanged();
        await _service.FlushPendingFinalizationAsync();

        // Assert
        await _libraryService.DidNotReceive().MarkListenAsEligibleForScrobblingAsync(Arg.Any<long>());
        _raisedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task OnPositionChanged_WhenNoActiveSession_DoesNotMarkEligible()
    {
        // Arrange — no track is playing (CurrentListenHistoryId is null)
        await _service.InitializeAsync();

        _audioPlayer.Duration.Returns(TimeSpan.FromMinutes(3));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromMinutes(2));

        // Act
        RaisePositionChanged();
        await _service.FlushPendingFinalizationAsync();

        // Assert
        await _libraryService.DidNotReceive().MarkListenAsEligibleForScrobblingAsync(Arg.Any<long>());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Idempotence — marked at most once per session
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnPositionChanged_AfterEligibilityMet_DoesNotMarkAgain()
    {
        // Arrange
        await _service.InitializeAsync();
        _libraryService.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(5L);
        await _service.PlayAsync(_testSongs[0]);

        _audioPlayer.Duration.Returns(TimeSpan.FromMinutes(3));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(91));

        // Act — raise the event twice
        RaisePositionChanged();
        RaisePositionChanged();
        await _service.FlushPendingFinalizationAsync();

        // Assert — only one DB call and one event despite two position events
        await _libraryService.Received(1).MarkListenAsEligibleForScrobblingAsync(5L);
        _raisedEvents.Should().ContainSingle();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Eligibility flag resets when a new track starts
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnPositionChanged_AfterTrackChange_ResetsEligibilityAndMarksNewSession()
    {
        // Arrange — play first track, meet threshold
        await _service.InitializeAsync();
        _libraryService.StartListenSessionAsync(_testSongs[0].Id, Arg.Any<PlaybackContext>()).Returns(10L);
        _libraryService.StartListenSessionAsync(_testSongs[1].Id, Arg.Any<PlaybackContext>()).Returns(11L);
        await _service.PlayAsync(_testSongs, startIndex: 0);

        _audioPlayer.Duration.Returns(TimeSpan.FromMinutes(3));
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(91));
        RaisePositionChanged(); // marks session 10 as eligible
        await _service.FlushPendingFinalizationAsync();

        // Move to next track — this resets _isEligibilityMarked
        await _service.NextAsync();
        await _service.FlushPendingFinalizationAsync();

        // Now simulate the threshold being met for the new session
        _audioPlayer.CurrentPosition.Returns(TimeSpan.FromSeconds(91));
        RaisePositionChanged();
        await _service.FlushPendingFinalizationAsync();

        // Assert — both sessions were individually marked
        await _libraryService.Received(1).MarkListenAsEligibleForScrobblingAsync(10L);
        await _libraryService.Received(1).MarkListenAsEligibleForScrobblingAsync(11L);
    }
}
