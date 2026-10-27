using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations.Presence;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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

    [Fact]
    public async Task PlaybackEvents_WaitingForServiceActivation_PreserveEventValues()
    {
        _settingsService.GetLastFmNowPlayingEnabledAsync().Returns(true);
        await _manager.InitializeAsync();
        var initializing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var statesReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = new System.Collections.Concurrent.ConcurrentQueue<(Song Song, long Session)>();
        var states = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        _discordService.InitializeAsync().Returns(_ =>
        {
            initializing.TrySetResult();
            return release.Task;
        });
        _lastFmService.OnTrackChangedAsync(Arg.Any<Song>(), Arg.Any<long>()).Returns(call =>
        {
            updates.Enqueue((call.Arg<Song>(), call.Arg<long>()));
            if (updates.Count == 2) received.TrySetResult();
            return Task.CompletedTask;
        });
        _lastFmService.OnPlaybackStateChangedAsync(Arg.Any<bool>()).Returns(call =>
        {
            states.Enqueue(call.Arg<bool>());
            if (states.Count == 2) statesReceived.TrySetResult();
            return Task.CompletedTask;
        });
        _settingsService.DiscordRichPresenceSettingChanged += Raise.Event<Action<bool>>(true);
        try
        {
            await initializing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var first = new Song { Title = "First" };
            var second = new Song { Title = "Second" };
            _playbackService.CurrentTrack.Returns(first);
            _playbackService.CurrentListenHistoryId.Returns(1L);
            _playbackService.TrackChanged += Raise.Event<Action>();
            _playbackService.IsPlaying.Returns(false);
            _playbackService.PlaybackStateChanged += Raise.Event<Action>();
            _playbackService.CurrentTrack.Returns(second);
            _playbackService.CurrentListenHistoryId.Returns(2L);
            _playbackService.TrackChanged += Raise.Event<Action>();
            _playbackService.IsPlaying.Returns(true);
            _playbackService.PlaybackStateChanged += Raise.Event<Action>();
            release.SetResult();

            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await statesReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

            updates.Should().BeEquivalentTo(new[] { (first, 1L), (second, 2L) });
            states.Should().BeEquivalentTo(new[] { false, true });
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task InitializeAsync_WhenCalledTwice_OnlyInitializesOnce()
    {
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _discordService.InitializeAsync().Returns(release.Task);

        var first = _manager.InitializeAsync();
        var second = _manager.InitializeAsync();
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        await _settingsService.Received(1).GetDiscordRichPresenceEnabledAsync();
        _playbackService.PlaybackStateChanged += Raise.Event<Action>();
        await _discordService.Received(1).OnPlaybackStateChangedAsync(false);
    }

    [Fact]
    public async Task ShutdownAsync_DuringInitialization_WaitsAndDisposesActivatedServices()
    {
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _discordService.InitializeAsync().Returns(release.Task);

        var initialization = _manager.InitializeAsync();
        var shutdown = _manager.ShutdownAsync();
        release.SetResult();
        await Task.WhenAll(initialization, shutdown).WaitAsync(TimeSpan.FromSeconds(5));

        await _discordService.Received(1).DisposeAsync();
        _playbackService.PlaybackStateChanged += Raise.Event<Action>();
        await _discordService.DidNotReceive().OnPlaybackStateChangedAsync(Arg.Any<bool>());
    }

    [Fact]
    public async Task ShutdownAsync_WhenStopFails_StillDisposesServiceAndCanRestart()
    {
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(true);
        await _manager.InitializeAsync();
        _discordService.OnPlaybackStoppedAsync().ThrowsAsync(new InvalidOperationException("Stop failed"));

        await _manager.ShutdownAsync();

        await _discordService.Received(1).DisposeAsync();
        await _manager.InitializeAsync();
        await _discordService.Received(2).InitializeAsync();
    }

    [Fact]
    public async Task InitializeAsync_WithRestoredTrack_ImmediatelyUpdatesDiscord()
    {
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(true);
        var song = new Song { Title = "Restored track" };
        _playbackService.CurrentTrack.Returns(song);
        _playbackService.CurrentListenHistoryId.Returns((long?)null);

        await _manager.InitializeAsync();

        await _discordService.Received(1).OnTrackChangedAsync(song, 0);
        await _discordService.Received(1).OnPlaybackStateChangedAsync(false);
    }

    [Fact]
    public async Task InitializeAsync_WhenPlaybackChangesWhileConnecting_SynchronizesCurrentTrack()
    {
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _discordService.InitializeAsync().Returns(release.Task);
        var initialization = _manager.InitializeAsync();
        var song = new Song { Title = "Started while connecting" };
        _playbackService.CurrentTrack.Returns(song);
        _playbackService.CurrentListenHistoryId.Returns(5L);

        release.SetResult();
        await initialization.WaitAsync(TimeSpan.FromSeconds(5));

        await _discordService.Received(1).OnTrackChangedAsync(song, 5L);
    }

    [Fact]
    public async Task InitializeAsync_WhenDiscordDisabled_DoesNotActivateDiscordService()
    {
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(false);

        await _manager.InitializeAsync();

        await _discordService.DidNotReceive().InitializeAsync();
    }

    [Theory]
    [InlineData(false, true, true, 0)]
    [InlineData(true, false, false, 0)]
    [InlineData(true, true, false, 1)]
    [InlineData(true, false, true, 1)]
    public async Task InitializeAsync_ActivatesLastFmOnlyWithCredentialsAndAFeatureEnabled(
        bool hasCredentials, bool scrobbling, bool nowPlaying, int activations)
    {
        _settingsService.GetLastFmCredentialsAsync()
            .Returns(Task.FromResult<(string?, string?)?>(hasCredentials ? ("user", "key") : null));
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(scrobbling);
        _settingsService.GetLastFmNowPlayingEnabledAsync().Returns(nowPlaying);

        await _manager.InitializeAsync();

        await _lastFmService.Received(activations).InitializeAsync();
    }

    [Fact]
    public async Task DiscordRichPresenceSettingChanged_WhenEnabled_ActivatesDiscordService()
    {
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(false);
        await _manager.InitializeAsync();
        _discordService.ClearReceivedCalls();

        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(true);
        _settingsService.DiscordRichPresenceSettingChanged += Raise.Event<Action<bool>>(true);

        await _discordService.Received(1).InitializeAsync();
    }

    [Fact]
    public async Task DiscordRichPresenceSettingChanged_WhenDisabled_DeactivatesDiscordService()
    {
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(true);
        await _manager.InitializeAsync();
        _discordService.ClearReceivedCalls();

        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(false);
        _settingsService.DiscordRichPresenceSettingChanged += Raise.Event<Action<bool>>(false);

        await _discordService.Received(1).OnPlaybackStoppedAsync();
    }

    [Fact]
    public async Task LastFmSettingsChanged_WhenScrobblingEnabled_ActivatesLastFmService()
    {
        _settingsService.GetLastFmCredentialsAsync().Returns((("user", "key"))!);
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(false);
        _settingsService.GetLastFmNowPlayingEnabledAsync().Returns(false);
        await _manager.InitializeAsync();
        _lastFmService.ClearReceivedCalls();

        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(true);
        _settingsService.LastFmSettingsChanged += Raise.Event<Action>();

        await _lastFmService.Received(1).InitializeAsync();
    }

    [Fact]
    public async Task OnTrackChanged_WhenTrackClearedAfterPlaying_BroadcastsStopToActiveServices()
    {
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(true);
        await _manager.InitializeAsync();

        var track = new Song { Title = "Playing Track" };
        _playbackService.CurrentTrack.Returns(track);
        _playbackService.CurrentListenHistoryId.Returns((long?)1L);
        _playbackService.TrackChanged += Raise.Event<Action>();
        _discordService.ClearReceivedCalls();

        _playbackService.CurrentTrack.Returns((Song?)null);
        _playbackService.CurrentListenHistoryId.Returns((long?)null);
        _playbackService.TrackChanged += Raise.Event<Action>();

        await _discordService.Received(1).OnPlaybackStoppedAsync();
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

        await _discordService.Received(1)
            .OnTrackEligibleForScrobblingAsync(song, 42L, listenStartedUtc);
    }

    [Fact]
    public async Task ShutdownAsync_DisposesEveryActiveService()
    {
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(true);
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(true);
        await _manager.InitializeAsync();

        await _manager.ShutdownAsync();

        await _discordService.Received(1).OnPlaybackStoppedAsync();
        await _lastFmService.Received(1).OnPlaybackStoppedAsync();
        await _discordService.Received(1).DisposeAsync();
        await _lastFmService.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task OnPositionChanged_ThrottlesRepeatedUpdatesButResetsForNewTrack()
    {
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(true);
        await _manager.InitializeAsync();
        var duration = TimeSpan.FromMinutes(3);
        var position = TimeSpan.FromSeconds(30);
        _playbackService.CurrentTrack.Returns(new Song { Title = "First" });
        _playbackService.CurrentListenHistoryId.Returns(42L);
        _playbackService.CurrentPosition.Returns(position);
        _playbackService.Duration.Returns(duration);

        _playbackService.TrackChanged += Raise.Event<Action>();
        _playbackService.PositionChanged += Raise.Event<Action>();
        _playbackService.PositionChanged += Raise.Event<Action>();
        await _discordService.Received(1).OnTrackProgressAsync(position, duration);

        _playbackService.CurrentTrack.Returns(new Song { Title = "Second" });
        _playbackService.CurrentListenHistoryId.Returns(43L);
        _playbackService.TrackChanged += Raise.Event<Action>();
        _playbackService.PositionChanged += Raise.Event<Action>();
        await _discordService.Received(2).OnTrackProgressAsync(position, duration);
    }

    [Theory]
    [InlineData("token", 1)]
    [InlineData(null, 0)]
    public async Task ListenBrainzSettingsChanged_RequiresTokenForActivation(string? token, int activations)
    {
        var service = Substitute.For<IPresenceService>();
        service.Name.Returns("ListenBrainz");
        await using var manager = new PresenceManager(_playbackService, [service], _settingsService, _logger);
        await manager.InitializeAsync();
        _settingsService.GetListenBrainzUserTokenAsync().Returns(token);
        _settingsService.GetListenBrainzScrobblingEnabledAsync().Returns(true);

        _settingsService.ListenBrainzSettingsChanged += Raise.Event<Action>();

        await service.Received(activations).InitializeAsync();
    }

    [Fact]
    public async Task InitializeAsync_WhenServiceFails_CleansItUpAndActivatesOthers()
    {
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(true);
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(true);
        _discordService.InitializeAsync().ThrowsAsync(new InvalidOperationException("Initialization failed"));

        await _manager.InitializeAsync();
        _playbackService.PlaybackStateChanged += Raise.Event<Action>();

        await _discordService.Received(1).DisposeAsync();
        await _discordService.DidNotReceive().OnPlaybackStateChangedAsync(Arg.Any<bool>());
        await _lastFmService.Received(1).InitializeAsync();
        await _lastFmService.Received(1).OnPlaybackStateChangedAsync(false);
    }

    [Fact]
    public async Task Broadcast_WhenOneServiceFails_StillReachesOthers()
    {
        _settingsService.GetDiscordRichPresenceEnabledAsync().Returns(true);
        _settingsService.GetLastFmScrobblingEnabledAsync().Returns(true);
        await _manager.InitializeAsync();
        _discordService.OnPlaybackStateChangedAsync(true).ThrowsAsync(new InvalidOperationException("Provider failed"));

        _playbackService.IsPlaying.Returns(true);
        _playbackService.PlaybackStateChanged += Raise.Event<Action>();

        await _discordService.Received(1).OnPlaybackStateChangedAsync(true);
        await _lastFmService.Received(1).OnPlaybackStateChangedAsync(true);
    }
}
