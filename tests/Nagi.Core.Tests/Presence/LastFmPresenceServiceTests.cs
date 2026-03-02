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
///     Threshold evaluation (when a track becomes eligible) is handled by
///     <see cref="MusicPlaybackService" /> and tested in its own test class. These tests verify
///     only the Last.fm-specific behaviour: Now Playing updates and scrobble submission on the
///     centralized eligibility signal.
/// </summary>
public class LastFmPresenceServiceTests : IDisposable
{
    private readonly ILibraryWriter _libraryWriter;
    private readonly ILogger<LastFmPresenceService> _logger;
    private readonly ILastFmScrobblerService _scrobblerService;
    private readonly LastFmPresenceService _service;
    private readonly ISettingsService _settingsService;

    public LastFmPresenceServiceTests()
    {
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

    public void Dispose()
    {
        _service.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private async Task InitializeServiceAsync(bool nowPlaying, bool scrobbling)
    {
        _settingsService.GetLastFmNowPlayingEnabledAsync().Returns(nowPlaying);
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(scrobbling);
        await _service.InitializeAsync();
    }

    private static Song CreateTestSong(TimeSpan duration)
    {
        return new Song { Id = Guid.NewGuid(), Title = "Test Song", Duration = duration };
    }

    #region Now Playing Tests

    /// <summary>
    ///     Verifies that when "Now Playing" is enabled, a track change triggers an update to Last.fm.
    /// </summary>
    [Fact]
    public async Task OnTrackChangedAsync_WithNowPlayingEnabled_UpdatesNowPlaying()
    {
        // Arrange
        await InitializeServiceAsync(true, false);
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
    public async Task OnTrackChangedAsync_WithNowPlayingDisabled_DoesNotUpdateNowPlaying()
    {
        // Arrange
        await InitializeServiceAsync(false, false);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));

        // Act
        await _service.OnTrackChangedAsync(song, 1);

        // Assert
        await _scrobblerService.DidNotReceive().UpdateNowPlayingAsync(Arg.Any<Song>());
    }

    #endregion

    #region Scrobbling Tests

    /// <summary>
    ///     Verifies that when the eligibility signal fires and scrobbling is enabled,
    ///     the service attempts to scrobble the track.
    /// </summary>
    [Fact]
    public async Task OnTrackEligibleForScrobblingAsync_WhenScrobblingEnabled_AttemptsScrobble()
    {
        // Arrange
        await InitializeServiceAsync(false, true);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));
        await _service.OnTrackChangedAsync(song, 1);

        // Act
        await _service.OnTrackEligibleForScrobblingAsync(song, 1);

        // Assert
        await _scrobblerService.Received(1).ScrobbleAsync(song, Arg.Any<DateTime>());
    }

    /// <summary>
    ///     Verifies that when scrobbling is disabled, the eligibility signal is ignored.
    /// </summary>
    [Fact]
    public async Task OnTrackEligibleForScrobblingAsync_WhenScrobblingDisabled_DoesNotScrobble()
    {
        // Arrange
        await InitializeServiceAsync(false, false);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));
        await _service.OnTrackChangedAsync(song, 1);

        // Act
        await _service.OnTrackEligibleForScrobblingAsync(song, 1);

        // Assert
        await _scrobblerService.DidNotReceive().ScrobbleAsync(Arg.Any<Song>(), Arg.Any<DateTime>());
    }

    /// <summary>
    ///     Verifies that if the real-time scrobble succeeds, the listen is marked as scrobbled in the database.
    /// </summary>
    [Fact]
    public async Task OnTrackEligibleForScrobblingAsync_WhenRealtimeScrobbleSucceeds_MarksAsScrobbled()
    {
        // Arrange
        await InitializeServiceAsync(false, true);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));
        await _service.OnTrackChangedAsync(song, 1);
        _scrobblerService.ScrobbleAsync(song, Arg.Any<DateTime>()).Returns(true);

        // Act
        await _service.OnTrackEligibleForScrobblingAsync(song, 1);

        // Assert
        await _libraryWriter.Received(1).MarkListenAsScrobbledAsync(1);
    }

    /// <summary>
    ///     Verifies that if the real-time scrobble fails (returns false), the listen is NOT marked as
    ///     scrobbled, allowing a background service to retry later.
    /// </summary>
    [Fact]
    public async Task OnTrackEligibleForScrobblingAsync_WhenRealtimeScrobbleFails_DoesNotMarkAsScrobbled()
    {
        // Arrange
        await InitializeServiceAsync(false, true);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));
        await _service.OnTrackChangedAsync(song, 1);
        _scrobblerService.ScrobbleAsync(song, Arg.Any<DateTime>()).Returns(false);

        // Act
        await _service.OnTrackEligibleForScrobblingAsync(song, 1);

        // Assert
        await _libraryWriter.DidNotReceive().MarkListenAsScrobbledAsync(Arg.Any<long>());
    }

    /// <summary>
    ///     Verifies that if the real-time scrobble throws an exception, the listen is not marked as scrobbled.
    /// </summary>
    [Fact]
    public async Task OnTrackEligibleForScrobblingAsync_WhenRealtimeScrobbleThrows_DoesNotMarkAsScrobbled()
    {
        // Arrange
        await InitializeServiceAsync(false, true);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));
        await _service.OnTrackChangedAsync(song, 1);
        _scrobblerService.ScrobbleAsync(song, Arg.Any<DateTime>()).ThrowsAsync(new Exception("Network error"));

        // Act
        await _service.OnTrackEligibleForScrobblingAsync(song, 1);

        // Assert
        await _libraryWriter.DidNotReceive().MarkListenAsScrobbledAsync(Arg.Any<long>());
    }

    /// <summary>
    ///     Verifies that OnTrackProgressAsync is a no-op — it should never trigger scrobbling.
    ///     Threshold evaluation is the sole responsibility of MusicPlaybackService.
    /// </summary>
    [Fact]
    public async Task OnTrackProgressAsync_NeverTriggersScrobble()
    {
        // Arrange
        await InitializeServiceAsync(false, true);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));
        await _service.OnTrackChangedAsync(song, 1);

        // Act — call with values that would have met the old threshold
        await _service.OnTrackProgressAsync(TimeSpan.FromMinutes(2), song.Duration);

        // Assert
        await _scrobblerService.DidNotReceive().ScrobbleAsync(Arg.Any<Song>(), Arg.Any<DateTime>());
        await _libraryWriter.DidNotReceive().MarkListenAsEligibleForScrobblingAsync(Arg.Any<long>());
        await _libraryWriter.DidNotReceive().MarkListenAsScrobbledAsync(Arg.Any<long>());
    }

    #endregion
}