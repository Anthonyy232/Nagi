using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations.Presence;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Nagi.Core.Tests.Presence;

/// <summary>
///     Contains unit tests for the <see cref="ListenBrainzPresenceService" />.
///     Threshold evaluation (when a track becomes eligible) is handled by
///     <see cref="MusicPlaybackService" /> and tested in its own test class. These tests verify
///     only the ListenBrainz-specific behaviour: "Playing Now" updates and listen submission on the
///     centralized eligibility signal.
/// </summary>
public class ListenBrainzPresenceServiceTests : IDisposable
{
    private readonly ILibraryWriter _libraryWriter;
    private readonly ILogger<ListenBrainzPresenceService> _logger;
    private readonly IListenBrainzScrobblerService _scrobblerService;
    private readonly ListenBrainzPresenceService _service;
    private readonly ISettingsService _settingsService;

    public ListenBrainzPresenceServiceTests()
    {
        _scrobblerService = Substitute.For<IListenBrainzScrobblerService>();
        _libraryWriter = Substitute.For<ILibraryWriter>();
        _settingsService = Substitute.For<ISettingsService>();
        _logger = Substitute.For<ILogger<ListenBrainzPresenceService>>();

        _service = new ListenBrainzPresenceService(
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
        _settingsService.GetListenBrainzNowPlayingEnabledAsync().Returns(nowPlaying);
        _settingsService.GetListenBrainzScrobblingEnabledAsync().Returns(scrobbling);
        await _service.InitializeAsync();
    }

    private static Song CreateTestSong(TimeSpan duration)
    {
        return new Song { Id = Guid.NewGuid(), Title = "Test Song", Duration = duration };
    }

    #region Metadata

    /// <summary>
    ///     Verifies that the service exposes the canonical "ListenBrainz" name used by
    ///     <c>PresenceManager</c> when routing provider-specific signals.
    /// </summary>
    [Fact]
    public void Name_IsListenBrainz()
    {
        // Assert
        Assert.Equal("ListenBrainz", _service.Name);
    }

    #endregion

    #region Playing Now Tests

    /// <summary>
    ///     Verifies that when "Playing Now" is enabled, a track change triggers an update to ListenBrainz.
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
    ///     Verifies that when "Playing Now" is disabled, a track change does not trigger an update.
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

    #region Submission Tests

    /// <summary>
    ///     Verifies that when the eligibility signal fires and scrobbling is enabled,
    ///     the service attempts to submit the listen.
    /// </summary>
    [Fact]
    public async Task OnTrackEligibleForScrobblingAsync_WhenScrobblingEnabled_AttemptsSubmit()
    {
        // Arrange
        await InitializeServiceAsync(false, true);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));
        await _service.OnTrackChangedAsync(song, 1);

        // Act
        await _service.OnTrackEligibleForScrobblingAsync(song, 1, DateTime.UnixEpoch);

        // Assert
        await _scrobblerService.Received(1).SubmitListenAsync(song, DateTime.UnixEpoch);
    }

    /// <summary>
    ///     Verifies that when scrobbling is disabled, the eligibility signal is ignored.
    /// </summary>
    [Fact]
    public async Task OnTrackEligibleForScrobblingAsync_WhenScrobblingDisabled_DoesNotSubmit()
    {
        // Arrange
        await InitializeServiceAsync(false, false);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));
        await _service.OnTrackChangedAsync(song, 1);

        // Act
        await _service.OnTrackEligibleForScrobblingAsync(song, 1, DateTime.UnixEpoch);

        // Assert
        await _scrobblerService.DidNotReceive().SubmitListenAsync(Arg.Any<Song>(), Arg.Any<DateTime>());
        await _libraryWriter.DidNotReceive().MarkListenAsSubmittedToListenBrainzAsync(Arg.Any<long>());
    }

    /// <summary>
    ///     Verifies that if the real-time submission succeeds, the listen is marked as submitted in the database.
    /// </summary>
    [Fact]
    public async Task OnTrackEligibleForScrobblingAsync_WhenRealtimeSubmitSucceeds_MarksAsSubmitted()
    {
        // Arrange
        await InitializeServiceAsync(false, true);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));
        await _service.OnTrackChangedAsync(song, 1);
        _scrobblerService.SubmitListenAsync(song, Arg.Any<DateTime>()).Returns(true);

        // Act
        await _service.OnTrackEligibleForScrobblingAsync(song, 1, DateTime.UnixEpoch);

        // Assert
        await _libraryWriter.Received(1).MarkListenAsSubmittedToListenBrainzAsync(1);
    }

    /// <summary>
    ///     Verifies that if the real-time submission fails (returns false), the listen is NOT marked as
    ///     submitted, allowing a background service to retry later.
    /// </summary>
    [Fact]
    public async Task OnTrackEligibleForScrobblingAsync_WhenRealtimeSubmitFails_DoesNotMarkAsSubmitted()
    {
        // Arrange
        await InitializeServiceAsync(false, true);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));
        await _service.OnTrackChangedAsync(song, 1);
        _scrobblerService.SubmitListenAsync(song, Arg.Any<DateTime>()).Returns(false);

        // Act
        await _service.OnTrackEligibleForScrobblingAsync(song, 1, DateTime.UnixEpoch);

        // Assert
        await _libraryWriter.DidNotReceive().MarkListenAsSubmittedToListenBrainzAsync(Arg.Any<long>());
    }

    /// <summary>
    ///     Verifies that if the real-time submission throws an exception, the listen is not marked as submitted.
    /// </summary>
    [Fact]
    public async Task OnTrackEligibleForScrobblingAsync_WhenRealtimeSubmitThrows_DoesNotMarkAsSubmitted()
    {
        // Arrange
        await InitializeServiceAsync(false, true);
        var song = CreateTestSong(TimeSpan.FromMinutes(3));
        await _service.OnTrackChangedAsync(song, 1);
        _scrobblerService.SubmitListenAsync(song, Arg.Any<DateTime>()).ThrowsAsync(new Exception("Network error"));

        // Act
        await _service.OnTrackEligibleForScrobblingAsync(song, 1, DateTime.UnixEpoch);

        // Assert
        await _libraryWriter.DidNotReceive().MarkListenAsSubmittedToListenBrainzAsync(Arg.Any<long>());
    }

    #endregion
}
