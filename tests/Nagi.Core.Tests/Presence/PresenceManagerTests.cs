using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations.Presence;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Nagi.Core.Tests.Presence;

/// <summary>
///     Contains unit tests for the <see cref="PresenceManager" />.
///     These tests verify the manager's core logic for activating and deactivating presence services,
///     broadcasting playback events, and reacting to application settings changes in a robust and
///     error-tolerant manner.
/// </summary>
public class PresenceManagerTests : IDisposable
{
    private readonly IPresenceService _discordService;
    private readonly IPresenceService _lastFmService;
    private readonly ILogger<PresenceManager> _logger;
    private readonly PresenceManager _manager;
    private readonly IMusicPlaybackService _playbackService;
    private readonly ISettingsService _settingsService;
    private readonly Song _testSong;

    public PresenceManagerTests()
    {
        _playbackService = Substitute.For<IMusicPlaybackService>();
        _settingsService = Substitute.For<ISettingsService>();
        _logger = Substitute.For<ILogger<PresenceManager>>();

        _discordService = Substitute.For<IPresenceService, IAsyncDisposable>();
        _discordService.Name.Returns("Discord");

        _lastFmService = Substitute.For<IPresenceService, IAsyncDisposable>();
        _lastFmService.Name.Returns("Last.fm");

        var services = new List<IPresenceService> { _discordService, _lastFmService };
        _manager = new PresenceManager(_playbackService, services, _settingsService, _logger);

        _testSong = new Song { Id = Guid.NewGuid(), Title = "Test Song", Duration = TimeSpan.FromMinutes(3) };
    }

    public void Dispose()
    {
        _manager.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Helper Methods

    /// <summary>
    ///     Sets up the initial state of the settings mocks and initializes the manager.
    /// </summary>
    private async Task SetupInitialStateAsync(bool discordEnabled, bool lastFmEnabled, bool lastFmHasCreds = true)
    {
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(discordEnabled);

        (string? Username, string? SessionKey)? credentials = lastFmHasCreds ? ("user", "key") : null;
        _settingsService.GetLastFmCredentialsAsync()
            .Returns(Task.FromResult(credentials));

        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(lastFmEnabled);
        _settingsService.GetLastFmNowPlayingEnabledAsync().Returns(false);

        await _manager.InitializeAsync();
    }

    #endregion

    #region Initialization and Shutdown Tests

    public static IEnumerable<object[]> InitializationScenarios()
    {
        yield return new object[] { true, true, 1, 1 }; // Both enabled
        yield return new object[] { true, false, 1, 0 }; // Only Discord enabled
        yield return new object[] { false, true, 0, 1 }; // Only Last.fm enabled
        yield return new object[] { false, false, 0, 0 }; // Both disabled
    }

    /// <summary>
    ///     Verifies that <see cref="PresenceManager.InitializeAsync" /> correctly activates services
    ///     based on their initial configuration settings.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> The manager is initialized with various combinations of settings for Discord
    ///     and Last.fm being enabled or disabled.
    ///     <br />
    ///     <b>Expected Result:</b> Only the services that are configured as 'enabled' and have valid
    ///     credentials (if required) should have their <c>InitializeAsync</c> method called.
    /// </remarks>
    [Theory]
    [MemberData(nameof(InitializationScenarios))]
    public async Task InitializeAsync_WithVaryingSettings_ActivatesCorrectServices(
        bool discordEnabled, bool lastFmEnabled, int discordInitCalls, int lastFmInitCalls)
    {
        // Arrange
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(discordEnabled);
        (string? Username, string? SessionKey)? credentials = ("user", "key");
        _settingsService.GetLastFmCredentialsAsync()
            .Returns(Task.FromResult(credentials));
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(lastFmEnabled);
        _settingsService.GetLastFmNowPlayingEnabledAsync().Returns(false);

        // Act
        await _manager.InitializeAsync();

        // Assert
        await _discordService.Received(discordInitCalls).InitializeAsync();
        await _lastFmService.Received(lastFmInitCalls).InitializeAsync();
    }

    /// <summary>
    ///     Verifies that the Last.fm service is not activated if it is enabled in settings
    ///     but no user credentials are provided.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> The manager is initialized with settings indicating Last.fm is enabled,
    ///     but the settings service returns null for credentials.
    ///     <br />
    ///     <b>Expected Result:</b> The Last.fm service's <c>InitializeAsync</c> method is never called,
    ///     preventing its activation without proper authentication.
    /// </remarks>
    [Fact]
    public async Task InitializeAsync_WhenLastFmHasNoCredentials_DoesNotActivateService()
    {
        // Arrange
        await SetupInitialStateAsync(false, true, false);

        // Act
        // Initialization is called within the helper method.

        // Assert
        await _lastFmService.DidNotReceive().InitializeAsync();
    }

    /// <summary>
    ///     Verifies that <see cref="PresenceManager.ShutdownAsync" /> correctly stops playback reporting
    ///     and disposes all currently active presence services.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> Multiple services are active, and the manager's `ShutdownAsync` method is called.
    ///     <br />
    ///     <b>Expected Result:</b> Each active service receives an <c>OnPlaybackStoppedAsync</c> call to
    ///     clear its remote state, followed by a call to <c>DisposeAsync</c> for proper resource cleanup.
    /// </remarks>
    [Fact]
    public async Task ShutdownAsync_DeactivatesAndDisposesAllActiveServices()
    {
        // Arrange
        await SetupInitialStateAsync(true, true);

        // Act
        await _manager.ShutdownAsync();

        // Assert
        await _discordService.Received(1).OnPlaybackStoppedAsync();
        await _lastFmService.Received(1).OnPlaybackStoppedAsync();
        await ((IAsyncDisposable)_discordService).Received(1).DisposeAsync();
        await ((IAsyncDisposable)_lastFmService).Received(1).DisposeAsync();
    }

    #endregion

    #region Playback Event Handling Tests

    /// <summary>
    ///     Verifies that a `TrackChanged` event from the playback service is correctly broadcast
    ///     to all active presence services.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> A new track begins playing, and the `IMusicPlaybackService.TrackChanged` event is raised.
    ///     <br />
    ///     <b>Expected Result:</b> All active services receive a call to <c>OnTrackChangedAsync</c> with the
    ///     correct song and listen history ID.
    /// </remarks>
    [Fact]
    public async Task OnTrackChanged_WhenTrackChanges_BroadcastsToActiveServices()
    {
        // Arrange
        await SetupInitialStateAsync(true, true);
        _playbackService.CurrentTrack.Returns(_testSong);
        _playbackService.CurrentListenHistoryId.Returns(42L);

        // Act
        _playbackService.TrackChanged += Raise.Event<Action>();

        // Assert
        await _discordService.Received(1).OnTrackChangedAsync(_testSong, 42L);
        await _lastFmService.Received(1).OnTrackChangedAsync(_testSong, 42L);
    }

    /// <summary>
    ///     Verifies that a `PlaybackStateChanged` event (e.g., pause/play) is correctly broadcast
    ///     to all active presence services.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> The playback state changes from paused to playing, and the
    ///     `IMusicPlaybackService.PlaybackStateChanged` event is raised.
    ///     <br />
    ///     <b>Expected Result:</b> All active services receive a call to <c>OnPlaybackStateChangedAsync</c>
    ///     with the new playback state. Inactive services receive no calls.
    /// </remarks>
    [Fact]
    public async Task OnPlaybackStateChanged_WhenStateChanges_BroadcastsToActiveServices()
    {
        // Arrange
        await SetupInitialStateAsync(true, false);
        _playbackService.IsPlaying.Returns(true);

        // Act
        _playbackService.PlaybackStateChanged += Raise.Event<Action>();

        // Assert
        await _discordService.Received(1).OnPlaybackStateChangedAsync(true);
        await _lastFmService.DidNotReceive().OnPlaybackStateChangedAsync(Arg.Any<bool>());
    }

    /// <summary>
    ///     Verifies that a `PositionChanged` event is correctly broadcast to all active services,
    ///     allowing them to update track progress.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> A track is playing, and the `IMusicPlaybackService.PositionChanged` event
    ///     is raised to indicate progress.
    ///     <br />
    ///     <b>Expected Result:</b> All active services receive a call to <c>OnTrackProgressAsync</c>
    ///     with the current position and total duration of the track.
    /// </remarks>
    [Fact]
    public async Task OnPositionChanged_WhenPositionChanges_BroadcastsToActiveServices()
    {
        // Arrange
        await SetupInitialStateAsync(true, true);
        var currentPosition = TimeSpan.FromSeconds(30);
        var duration = TimeSpan.FromMinutes(3);

        _playbackService.CurrentTrack.Returns(_testSong);
        _playbackService.CurrentListenHistoryId.Returns(42L);
        _playbackService.CurrentPosition.Returns(currentPosition);
        _playbackService.Duration.Returns(duration);

        // Act
        _playbackService.TrackChanged += Raise.Event<Action>();
        _playbackService.PositionChanged += Raise.Event<Action>();

        // Assert
        await _discordService.Received(1).OnTrackProgressAsync(currentPosition, duration);
        await _lastFmService.Received(1).OnTrackProgressAsync(currentPosition, duration);
    }

    #endregion

    #region Settings Change Event Handling Tests

    /// <summary>
    ///     Verifies that a previously disabled service is activated when its corresponding setting is enabled.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> The Discord service is initially disabled. The user then enables it in the
    ///     settings, triggering the `DiscordRichPresenceSettingChanged` event.
    ///     <br />
    ///     <b>Expected Result:</b> The manager calls the Discord service's <c>InitializeAsync</c> method
    ///     to activate it.
    /// </remarks>
    [Fact]
    public async Task OnDiscordRichPresenceSettingChanged_WhenSetToTrue_ActivatesService()
    {
        // Arrange
        await SetupInitialStateAsync(false, false);

        // Act
        _settingsService.DiscordRichPresenceSettingChanged += Raise.Event<Action<bool>>(true);

        // Assert
        await _discordService.Received(1).InitializeAsync();
    }

    /// <summary>
    ///     Verifies that a previously active service is deactivated and disposed when its corresponding
    ///     setting is disabled.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> The Discord service is initially active. The user then disables it in the
    ///     settings, triggering the `DiscordRichPresenceSettingChanged` event.
    ///     <br />
    ///     <b>Expected Result:</b> The manager calls the service's <c>OnPlaybackStoppedAsync</c> method
    ///     to clear its state, followed by <c>DisposeAsync</c> to clean up resources.
    /// </remarks>
    [Fact]
    public async Task OnDiscordRichPresenceSettingChanged_WhenSetToFalse_DeactivatesService()
    {
        // Arrange
        await SetupInitialStateAsync(true, false);

        // Act
        _settingsService.DiscordRichPresenceSettingChanged += Raise.Event<Action<bool>>(false);

        // Assert
        await _discordService.Received(1).OnPlaybackStoppedAsync();
        await ((IAsyncDisposable)_discordService).Received(1).DisposeAsync();
    }

    /// <summary>
    ///     Verifies that the Last.fm service is activated when its settings (e.g., credentials)
    ///     are updated to a valid state.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> The Last.fm service is initially disabled due to missing credentials. The
    ///     user then logs in, credentials become available, and the `LastFmSettingsChanged` event is fired.
    ///     <br />
    ///     <b>Expected Result:</b> The manager re-evaluates the Last.fm service's state and calls its
    ///     <c>InitializeAsync</c> method to activate it.
    /// </remarks>
    [Fact]
    public async Task OnLastFmSettingsChanged_WhenSettingsChangeToEnabled_ActivatesService()
    {
        // Arrange
        await SetupInitialStateAsync(false, false, false);

        (string? Username, string? SessionKey)? credentials = ("user", "key");
        _settingsService.GetLastFmCredentialsAsync()
            .Returns(Task.FromResult(credentials));
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(true);

        // Act
        _settingsService.LastFmSettingsChanged += Raise.Event<Action>();

        // Assert
        await _lastFmService.Received(1).InitializeAsync();
    }

    #endregion

    #region Edge Case and Error Handling Tests

    /// <summary>
    ///     Verifies that if one service fails to initialize, it does not prevent other services from
    ///     initializing successfully.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> Both Discord and Last.fm services are enabled. The Discord service is
    ///     configured to throw an exception during its `InitializeAsync` call.
    ///     <br />
    ///     <b>Expected Result:</b> An error is logged for the failed Discord service, but the Last.fm
    ///     service is still initialized and becomes active.
    /// </remarks>
    [Fact]
    public async Task InitializeAsync_WhenServiceFailsToInitialize_LogsErrorAndDoesNotStopOthers()
    {
        // Arrange
        _discordService.InitializeAsync().ThrowsAsync<InvalidOperationException>();
        await SetupInitialStateAsync(true, true);

        // Act
        // Initialization is called within the helper method.

        // Assert
        _logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o =>
                o != null && o.ToString()!.Contains("Failed to initialize 'Discord' presence service.")),
            Arg.Any<InvalidOperationException>(),
            Arg.Any<Func<object, Exception?, string>>());

        await _lastFmService.Received(1).InitializeAsync();
    }

    /// <summary>
    ///     Verifies that if one service fails during an event broadcast, it does not stop the broadcast
    ///     to other active services.
    /// </summary>
    /// <remarks>
    ///     <b>Scenario:</b> Both Discord and Last.fm services are active. The Discord service is
    ///     configured to throw an exception when `OnTrackChangedAsync` is called.
    ///     <br />
    ///     <b>Expected Result:</b> An error is logged for the failed Discord broadcast, but the Last.fm
    ///     service still receives the `OnTrackChangedAsync` call successfully.
    /// </remarks>
    [Fact]
    public async Task BroadcastAsync_WhenServiceFails_LogsErrorAndContinuesBroadcast()
    {
        // Arrange
        await SetupInitialStateAsync(true, true);
        _playbackService.CurrentTrack.Returns(_testSong);
        _playbackService.CurrentListenHistoryId.Returns(42L);
        _discordService.OnTrackChangedAsync(Arg.Any<Song>(), Arg.Any<long>())
            .ThrowsAsync<InvalidOperationException>();

        // Act
        _playbackService.TrackChanged += Raise.Event<Action>();

        // Assert
        _logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Is<object>(o =>
                o != null &&
                o.ToString()!.Contains("An error occurred while broadcasting an event to the 'Discord' service.")),
            Arg.Any<InvalidOperationException>(),
            Arg.Any<Func<object, Exception?, string>>());

        await _lastFmService.Received(1).OnTrackChangedAsync(_testSong, 42L);
    }

    #endregion
}