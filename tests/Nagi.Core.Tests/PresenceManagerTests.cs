using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations.Presence;
using NSubstitute;
using Xunit;

namespace Nagi.Core.Tests;

public class PresenceManagerTests : IAsyncDisposable
{
    private readonly IMusicPlaybackService _playbackService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<PresenceManager> _logger;
    private readonly IPresenceService _discordService;
    private readonly IPresenceService _lastFmService;
    private readonly PresenceManager _manager;

    public PresenceManagerTests()
    {
        _playbackService = Substitute.For<IMusicPlaybackService>();
        _settingsService = Substitute.For<ISettingsService>();
        _logger = Substitute.For<ILogger<PresenceManager>>();
        _discordService = Substitute.For<IPresenceService>();
        _lastFmService = Substitute.For<IPresenceService>();

        _discordService.Name.Returns("Discord");
        _lastFmService.Name.Returns("Last.fm");

        // Defaults: everything disabled (safe baseline)
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(false);
        _settingsService.GetLastFmCredentialsAsync().Returns((("user", "key"))!);
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(false);
        _settingsService.GetLastFmNowPlayingEnabledAsync().Returns(false);

        _manager = new PresenceManager(
            _playbackService,
            new[] { _discordService, _lastFmService },
            _settingsService,
            _logger);
    }

    public async ValueTask DisposeAsync() => await _manager.DisposeAsync();

    // -------------------------------------------------------------------------
    // InitializeAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task InitializeAsync_WhenCalledTwice_OnlyInitializesOnce()
    {
        await _manager.InitializeAsync();
        await _manager.InitializeAsync();

        // GetDiscordRichPresenceEnabledAsync should only be called once (not twice)
        await _settingsService.Received(1).GetDiscordRichPresenceEnabledAsync();
    }

    [Fact]
    public async Task InitializeAsync_WhenDiscordEnabled_ActivatesDiscordService()
    {
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(true);

        await _manager.InitializeAsync();

        await _discordService.Received(1).InitializeAsync();
    }

    [Fact]
    public async Task InitializeAsync_WhenDiscordDisabled_DoesNotActivateDiscordService()
    {
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(false);

        await _manager.InitializeAsync();

        await _discordService.DidNotReceive().InitializeAsync();
    }

    // -------------------------------------------------------------------------
    // IsLastFmServiceEnabled logic
    // -------------------------------------------------------------------------

    [Fact]
    public async Task InitializeAsync_WhenLastFmHasNoCredentials_DoesNotActivateLastFmService()
    {
        // No credentials → service disabled regardless of scrobbling/now-playing settings
        _settingsService.GetLastFmCredentialsAsync().Returns(Task.FromResult<(string?, string?)?>(null));
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(true);
        _settingsService.GetLastFmNowPlayingEnabledAsync().Returns(true);

        await _manager.InitializeAsync();

        await _lastFmService.DidNotReceive().InitializeAsync();
    }

    [Fact]
    public async Task InitializeAsync_WhenLastFmHasCredentialsButBothDisabled_DoesNotActivateLastFmService()
    {
        _settingsService.GetLastFmCredentialsAsync().Returns((("user", "key"))!);
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(false);
        _settingsService.GetLastFmNowPlayingEnabledAsync().Returns(false);

        await _manager.InitializeAsync();

        await _lastFmService.DidNotReceive().InitializeAsync();
    }

    [Fact]
    public async Task InitializeAsync_WhenLastFmHasCredentialsAndScrobblingEnabled_ActivatesLastFmService()
    {
        _settingsService.GetLastFmCredentialsAsync().Returns((("user", "key"))!);
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(true);
        _settingsService.GetLastFmNowPlayingEnabledAsync().Returns(false);

        await _manager.InitializeAsync();

        await _lastFmService.Received(1).InitializeAsync();
    }

    [Fact]
    public async Task InitializeAsync_WhenLastFmHasCredentialsAndNowPlayingEnabled_ActivatesLastFmService()
    {
        _settingsService.GetLastFmCredentialsAsync().Returns((("user", "key"))!);
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(false);
        _settingsService.GetLastFmNowPlayingEnabledAsync().Returns(true);

        await _manager.InitializeAsync();

        await _lastFmService.Received(1).InitializeAsync();
    }

    // -------------------------------------------------------------------------
    // ShutdownAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ShutdownAsync_WhenNotInitialized_DoesNothing()
    {
        // Should not throw and should not call any services
        await _manager.ShutdownAsync();

        await _discordService.DidNotReceive().OnPlaybackStoppedAsync();
    }

    [Fact]
    public async Task ShutdownAsync_WhenInitialized_BroadcastsStopToActiveServices()
    {
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(true);
        await _manager.InitializeAsync();

        await _manager.ShutdownAsync();

        await _discordService.Received(1).OnPlaybackStoppedAsync();
    }

    // -------------------------------------------------------------------------
    // Settings-driven activation/deactivation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DiscordRichPresenceSettingChanged_WhenEnabled_ActivatesDiscordService()
    {
        // Start with Discord disabled
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(false);
        await _manager.InitializeAsync();
        _discordService.ClearReceivedCalls();

        // The setting changes to enabled
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(true);
        _settingsService.DiscordRichPresenceSettingChanged += Raise.Event<Action<bool>>(true);

        await Task.Delay(200); // let FireAndForgetSafe complete

        await _discordService.Received(1).InitializeAsync();
    }

    [Fact]
    public async Task DiscordRichPresenceSettingChanged_WhenDisabled_DeactivatesDiscordService()
    {
        // Start with Discord enabled
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(true);
        await _manager.InitializeAsync();
        _discordService.ClearReceivedCalls();

        // The setting changes to disabled
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(false);
        _settingsService.DiscordRichPresenceSettingChanged += Raise.Event<Action<bool>>(false);

        await Task.Delay(200);

        await _discordService.Received(1).OnPlaybackStoppedAsync();
    }

    [Fact]
    public async Task LastFmSettingsChanged_WhenScrobblingEnabled_ActivatesLastFmService()
    {
        // Start with Last.fm disabled
        _settingsService.GetLastFmCredentialsAsync().Returns((("user", "key"))!);
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(false);
        _settingsService.GetLastFmNowPlayingEnabledAsync().Returns(false);
        await _manager.InitializeAsync();
        _lastFmService.ClearReceivedCalls();

        // Settings change: scrobbling enabled
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(true);
        _settingsService.LastFmSettingsChanged += Raise.Event<Action>();

        await Task.Delay(200);

        await _lastFmService.Received(1).InitializeAsync();
    }

    // -------------------------------------------------------------------------
    // Playback event broadcasting
    // -------------------------------------------------------------------------

    [Fact]
    public async Task OnTrackChanged_WhenActiveServiceExists_BroadcastsTrackChangeToServices()
    {
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(true);
        await _manager.InitializeAsync();

        var track = new Song { Title = "Test Track" };
        _playbackService.CurrentTrack.Returns(track);
        _playbackService.CurrentListenHistoryId.Returns((long?)1L);
        _playbackService.TrackChanged += Raise.Event<Action>();

        await Task.Delay(200);

        await _discordService.Received(1).OnTrackChangedAsync(track, 1L);
    }

    [Fact]
    public async Task OnTrackChanged_WhenTrackClearedAfterPlaying_BroadcastsStopToActiveServices()
    {
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(true);
        await _manager.InitializeAsync();

        // First, set a real track to establish state
        var track = new Song { Title = "Playing Track" };
        _playbackService.CurrentTrack.Returns(track);
        _playbackService.CurrentListenHistoryId.Returns((long?)1L);
        _playbackService.TrackChanged += Raise.Event<Action>();
        await Task.Delay(200);
        _discordService.ClearReceivedCalls();

        // Now clear the track (playback stopped)
        _playbackService.CurrentTrack.Returns((Song?)null);
        _playbackService.CurrentListenHistoryId.Returns((long?)null);
        _playbackService.TrackChanged += Raise.Event<Action>();

        await Task.Delay(200);

        await _discordService.Received(1).OnPlaybackStoppedAsync();
    }

    [Fact]
    public async Task OnPlaybackStateChanged_BroadcastsStateToActiveServices()
    {
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(true);
        await _manager.InitializeAsync();

        _playbackService.IsPlaying.Returns(true);
        _playbackService.PlaybackStateChanged += Raise.Event<Action>();

        await Task.Delay(200);

        await _discordService.Received(1).OnPlaybackStateChangedAsync(true);
    }

    [Fact]
    public async Task OnScrobbleEligibilityReached_BroadcastsEligibilityToActiveServices()
    {
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(true);
        await _manager.InitializeAsync();

        var song = new Song { Title = "Eligible Song" };
        var listenStartedUtc = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        _playbackService.ScrobbleEligibilityReached +=
            Raise.Event<Action<Song, long, DateTime>>(song, 42L, listenStartedUtc);

        await Task.Delay(200);

        await _discordService.Received(1)
            .OnTrackEligibleForScrobblingAsync(song, 42L, listenStartedUtc);
    }
}
