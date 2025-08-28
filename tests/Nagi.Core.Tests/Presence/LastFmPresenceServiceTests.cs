using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations.Presence;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Nagi.Core.Tests.Presence;

/// <summary>
///     Contains unit tests for the <see cref="LastFmPresenceService" />.
///     These tests verify the service's logic for updating "Now Playing" status and determining
///     scrobble eligibility based on track progress.
/// </summary>
public class LastFmPresenceServiceTests : IDisposable {
    private readonly ILibraryWriter _libraryWriter;
    private readonly ILogger<LastFmPresenceService> _logger;
    private readonly ILastFmScrobblerService _scrobblerService;
    private readonly LastFmPresenceService _service;
    private readonly ISettingsService _settingsService;

    public LastFmPresenceServiceTests() {
        _scrobblerService = Substitute.For<ILastFmScrobblerService>();
        _libraryWriter = Substitute.For<ILibraryWriter>();
        _settingsService = Substitute.For<ISettingsService>();
        _logger = Substitute.For<ILogger<LastFmPresenceService>>();

        _service = new LastFmPresenceService(
            _scrobblerService,
            _libraryWriter,
            _settingsService,
            _logger);
    }

    public void Dispose() {
        _service.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private async Task InitializeServiceAsync(bool nowPlaying, bool scrobbling) {
        _settingsService.GetLastFmNowPlayingEnabledAsync().Returns(nowPlaying);
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(scrobbling);
        await _service.InitializeAsync();
    }

    private static Song CreateTestSong(TimeSpan duration) =>
        new() { Id = Guid.NewGuid(), Title = "Test Song", Duration = duration };

    #region Now Playing Tests

    /// <summary>
    ///     Verifies that when "Now Playing" is enabled, a track change triggers an update to Last.fm.
    /// </summary>
    [Fact]
    public async Task OnTrackChangedAsync_WithNowPlayingEnabled_UpdatesNowPlaying() {
        // Arrange
        await InitializeServiceAsync(nowPlaying: true, scrobbling: false);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));

        // Act
        await _service.OnTrackChangedAsync(song, 1);

        // Assert
        await _scrobblerService.Received(1).UpdateNowPlayingAsync(song);
    }

    /// <summary>
    ///     Verifies that when "Now Playing" is disabled, a track change does not trigger an update.
    /// </summary>
    [Fact]
    public async Task OnTrackChangedAsync_WithNowPlayingDisabled_DoesNotUpdateNowPlaying() {
        // Arrange
        await InitializeServiceAsync(nowPlaying: false, scrobbling: false);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));

        // Act
        await _service.OnTrackChangedAsync(song, 1);

        // Assert
        await _scrobblerService.DidNotReceive().UpdateNowPlayingAsync(Arg.Any<Song>());
    }

    #endregion

    #region Scrobbling Tests

    /// <summary>
    ///     Verifies that a track is marked as eligible and scrobbled when it has been played for
    ///     at least half of its duration.
    /// </summary>
    [Fact]
    public async Task OnTrackProgressAsync_WhenEligibleByHalfDuration_MarksAndAttemptsScrobble() {
        // Arrange
        await InitializeServiceAsync(nowPlaying: false, scrobbling: true);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));
        await _service.OnTrackChangedAsync(song, 1);

        // Act
        await _service.OnTrackProgressAsync(TimeSpan.FromSeconds(91), song.Duration);

        // Assert
        await _libraryWriter.Received(1).MarkListenAsEligibleForScrobblingAsync(1);
        await _scrobblerService.Received(1).ScrobbleAsync(song, Arg.Any<DateTime>());
    }

    /// <summary>
    ///     Verifies that a track is marked as eligible and scrobbled when it has been played for
    ///     at least four minutes, even if that is less than half its duration.
    /// </summary>
    [Fact]
    public async Task OnTrackProgressAsync_WhenEligibleByFourMinutes_MarksAndAttemptsScrobble() {
        // Arrange
        await InitializeServiceAsync(nowPlaying: false, scrobbling: true);
        var song = CreateTestSong(TimeSpan.FromMinutes(10));
        await _service.OnTrackChangedAsync(song, 1);

        // Act
        await _service.OnTrackProgressAsync(TimeSpan.FromMinutes(4), song.Duration);

        // Assert
        await _libraryWriter.Received(1).MarkListenAsEligibleForScrobblingAsync(1);
        await _scrobblerService.Received(1).ScrobbleAsync(song, Arg.Any<DateTime>());
    }

    /// <summary>
    ///     Verifies that a track is not scrobbled if its total duration is 30 seconds or less.
    /// </summary>
    [Fact]
    public async Task OnTrackProgressAsync_WhenTrackIsTooShort_DoesNotScrobble() {
        // Arrange
        await InitializeServiceAsync(nowPlaying: false, scrobbling: true);
        var song = CreateTestSong(TimeSpan.FromSeconds(30));
        await _service.OnTrackChangedAsync(song, 1);

        // Act
        await _service.OnTrackProgressAsync(TimeSpan.FromSeconds(20), song.Duration);

        // Assert
        await _libraryWriter.DidNotReceive().MarkListenAsEligibleForScrobblingAsync(Arg.Any<long>());
        await _scrobblerService.DidNotReceive().ScrobbleAsync(Arg.Any<Song>(), Arg.Any<DateTime>());
    }

    /// <summary>
    ///     Verifies that if the real-time scrobble succeeds, the listen is marked as scrobbled in the database.
    /// </summary>
    [Fact]
    public async Task OnTrackProgressAsync_WhenRealtimeScrobbleSucceeds_MarksAsScrobbled() {
        // Arrange
        await InitializeServiceAsync(nowPlaying: false, scrobbling: true);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));
        await _service.OnTrackChangedAsync(song, 1);
        _scrobblerService.ScrobbleAsync(song, Arg.Any<DateTime>()).Returns(true);

        // Act
        await _service.OnTrackProgressAsync(TimeSpan.FromMinutes(2), song.Duration);

        // Assert
        await _libraryWriter.Received(1).MarkListenAsScrobbledAsync(1);
    }

    /// <summary>
    ///     Verifies that if the real-time scrobble fails (returns false), the listen is not marked as scrobbled,
    ///     allowing a background service to retry later.
    /// </summary>
    [Fact]
    public async Task OnTrackProgressAsync_WhenRealtimeScrobbleFails_DoesNotMarkAsScrobbled() {
        // Arrange
        await InitializeServiceAsync(nowPlaying: false, scrobbling: true);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));
        await _service.OnTrackChangedAsync(song, 1);
        _scrobblerService.ScrobbleAsync(song, Arg.Any<DateTime>()).Returns(false);

        // Act
        await _service.OnTrackProgressAsync(TimeSpan.FromMinutes(2), song.Duration);

        // Assert
        await _libraryWriter.DidNotReceive().MarkListenAsScrobbledAsync(Arg.Any<long>());
    }

    /// <summary>
    ///     Verifies that if the real-time scrobble throws an exception, the listen is not marked as scrobbled.
    /// </summary>
    [Fact]
    public async Task OnTrackProgressAsync_WhenRealtimeScrobbleThrows_DoesNotMarkAsScrobbled() {
        // Arrange
        await InitializeServiceAsync(nowPlaying: false, scrobbling: true);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));
        await _service.OnTrackChangedAsync(song, 1);
        _scrobblerService.ScrobbleAsync(song, Arg.Any<DateTime>()).ThrowsAsync(new Exception("Network error"));

        // Act
        await _service.OnTrackProgressAsync(TimeSpan.FromMinutes(2), song.Duration);

        // Assert
        await _libraryWriter.DidNotReceive().MarkListenAsScrobbledAsync(Arg.Any<long>());
    }

    /// <summary>
    ///     Verifies that once a track is marked as eligible for scrobbling, subsequent progress updates
    ///     for the same listen do not trigger another scrobble attempt.
    /// </summary>
    [Fact]
    public async Task OnTrackProgressAsync_AfterBecomingEligible_DoesNotScrobbleAgain() {
        // Arrange
        await InitializeServiceAsync(nowPlaying: false, scrobbling: true);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));
        await _service.OnTrackChangedAsync(song, 1);

        // Act
        await _service.OnTrackProgressAsync(TimeSpan.FromMinutes(2), song.Duration);
        await _service.OnTrackProgressAsync(TimeSpan.FromMinutes(2.5), song.Duration);

        // Assert
        await _libraryWriter.Received(1).MarkListenAsEligibleForScrobblingAsync(1);
        await _scrobblerService.Received(1).ScrobbleAsync(song, Arg.Any<DateTime>());
    }

    #endregion

    #region State Management

    /// <summary>
    ///     Verifies that stopping playback clears the internal state for the current song and listen ID.
    /// </summary>
    [Fact]
    public async Task OnPlaybackStoppedAsync_ClearsInternalState() {
        // Arrange
        await InitializeServiceAsync(nowPlaying: false, scrobbling: true);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));
        await _service.OnTrackChangedAsync(song, 1);

        // Act
        await _service.OnPlaybackStoppedAsync();
        // Try to scrobble after stopping; it should do nothing as the song is cleared.
        await _service.OnTrackProgressAsync(TimeSpan.FromMinutes(2), song.Duration);

        // Assert
        await _libraryWriter.DidNotReceive().MarkListenAsEligibleForScrobblingAsync(Arg.Any<long>());
    }

    #endregion
}