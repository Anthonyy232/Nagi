using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;
using Nagi.Core.Services.Implementations.Presence;
using Nagi.Core.Settings;
using Nagi.Core.Tests.Utils;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Nagi.Core.Tests;

public class PresenceManagerTests : IAsyncDisposable
{
    private readonly IMusicPlaybackService _playbackService;
    private readonly SettingsService _settingsService;
    private readonly ILogger<PresenceManager> _logger;
    private readonly IPresenceService _discordService;
    private readonly IPresenceService _otherService;
    private readonly PresenceManager _manager;

    public PresenceManagerTests()
    {
        _playbackService = Substitute.For<IMusicPlaybackService>();
        _settingsService = TestSettings.Create();
        _logger = Substitute.For<ILogger<PresenceManager>>();
        _discordService = Substitute.For<IPresenceService>();
        _otherService = Substitute.For<IPresenceService>();

        _discordService.IsEnabled.Returns(_ => _settingsService.Get(CoreSettings.DiscordRichPresence));
        // Stands in for another service with a setting of its own.
        _otherService.IsEnabled.Returns(_ => _settingsService.Get(CoreSettings.LastFmNowPlaying));


        _manager = new PresenceManager(
            _playbackService,
            new[] { _discordService, _otherService },
            _settingsService,
            _logger);
    }

    public async ValueTask DisposeAsync() => await _manager.DisposeAsync();

    [Fact]
    public async Task PlaybackEvents_WaitingForServiceActivation_ReachAServiceInOrder()
    {
        _settingsService.Set(CoreSettings.LastFmNowPlaying, true);
        await _manager.InitializeAsync();
        var initializing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var statesReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCallStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCallGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = new System.Collections.Concurrent.ConcurrentQueue<Song>();
        var states = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        var callsInFlight = 0;
        var overlapped = false;
        _discordService.InitializeAsync().Returns(_ =>
        {
            initializing.TrySetResult();
            return release.Task;
        });
        // The first call stays open; any other call starting meanwhile would be out of order.
        _otherService.OnTrackChangedAsync(Arg.Any<Song>()).Returns(async call =>
        {
            if (Interlocked.Increment(ref callsInFlight) > 1) overlapped = true;
            updates.Enqueue(call.Arg<Song>());
            if (updates.Count == 1)
            {
                firstCallStarted.TrySetResult();
                await firstCallGate.Task;
            }
            Interlocked.Decrement(ref callsInFlight);
            if (updates.Count == 2) received.TrySetResult();
        });
        _otherService.OnPlaybackStateChangedAsync(Arg.Any<bool>()).Returns(call =>
        {
            if (Interlocked.Increment(ref callsInFlight) > 1) overlapped = true;
            states.Enqueue(call.Arg<bool>());
            Interlocked.Decrement(ref callsInFlight);
            if (states.Count == 2) statesReceived.TrySetResult();
            return Task.CompletedTask;
        });
        _settingsService.Set(CoreSettings.DiscordRichPresence, true);
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
            await firstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Give out-of-order broadcasts the chance to start before the first call finishes.
            await Task.Delay(100);
            firstCallGate.SetResult();

            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await statesReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

            overlapped.Should().BeFalse();
            updates.Should().Equal(first, second);
            states.Should().Equal(false, true);
        }
        finally
        {
            release.TrySetResult();
            firstCallGate.TrySetResult();
        }
    }

    [Fact]
    public async Task InitializeAsync_WhenCalledTwice_OnlyInitializesOnce()
    {
        _settingsService.Set(CoreSettings.DiscordRichPresence, true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _discordService.InitializeAsync().Returns(release.Task);

        var first = _manager.InitializeAsync();
        var second = _manager.InitializeAsync();
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        await _discordService.Received(1).InitializeAsync();
        _playbackService.PlaybackStateChanged += Raise.Event<Action>();
        await _discordService.Received(1).OnPlaybackStateChangedAsync(false);
    }

    [Fact]
    public async Task ShutdownAsync_DuringInitialization_WaitsAndDisposesActivatedServices()
    {
        _settingsService.Set(CoreSettings.DiscordRichPresence, true);
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
        _settingsService.Set(CoreSettings.DiscordRichPresence, true);
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
        _settingsService.Set(CoreSettings.DiscordRichPresence, true);
        var song = new Song { Title = "Restored track" };
        _playbackService.CurrentTrack.Returns(song);
        _playbackService.CurrentListenHistoryId.Returns((long?)null);

        await _manager.InitializeAsync();

        await _discordService.Received(1).OnTrackChangedAsync(song);
        await _discordService.Received(1).OnPlaybackStateChangedAsync(false);
    }

    [Fact]
    public async Task InitializeAsync_WhenPlaybackChangesWhileConnecting_SynchronizesCurrentTrack()
    {
        _settingsService.Set(CoreSettings.DiscordRichPresence, true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _discordService.InitializeAsync().Returns(release.Task);
        var initialization = _manager.InitializeAsync();
        var song = new Song { Title = "Started while connecting" };
        _playbackService.CurrentTrack.Returns(song);
        _playbackService.CurrentListenHistoryId.Returns(5L);

        release.SetResult();
        await initialization.WaitAsync(TimeSpan.FromSeconds(5));

        await _discordService.Received(1).OnTrackChangedAsync(song);
    }

    [Fact]
    public async Task InitializeAsync_WhenDiscordDisabled_DoesNotActivateDiscordService()
    {
        _settingsService.Set(CoreSettings.DiscordRichPresence, false);

        await _manager.InitializeAsync();

        await _discordService.DidNotReceive().InitializeAsync();
    }

    [Fact]
    public async Task InitializeAsync_ActivatesOnlyTheServicesThatAreOn()
    {
        _settingsService.Set(CoreSettings.DiscordRichPresence, false);
        _settingsService.Set(CoreSettings.LastFmNowPlaying, true);

        await _manager.InitializeAsync();

        await _discordService.DidNotReceive().InitializeAsync();
        await _otherService.Received(1).InitializeAsync();
    }

    [Fact]
    public async Task DiscordRichPresenceChanged_WhenEnabled_ActivatesDiscordService()
    {
        _settingsService.Set(CoreSettings.DiscordRichPresence, false);
        await _manager.InitializeAsync();
        _discordService.ClearReceivedCalls();

        _settingsService.Set(CoreSettings.DiscordRichPresence, true);

        await _discordService.Received(1).InitializeAsync();
    }

    [Fact]
    public async Task DiscordRichPresenceChanged_WhenDisabled_DeactivatesDiscordService()
    {
        _settingsService.Set(CoreSettings.DiscordRichPresence, true);
        await _manager.InitializeAsync();
        _discordService.ClearReceivedCalls();

        _settingsService.Set(CoreSettings.DiscordRichPresence, false);

        await _discordService.Received(1).OnPlaybackStoppedAsync();
    }

    [Fact]
    public async Task SettingThatTurnsAServiceOn_ActivatesIt()
    {
        _settingsService.Set(CoreSettings.LastFmNowPlaying, false);
        await _manager.InitializeAsync();
        _otherService.ClearReceivedCalls();

        _settingsService.Set(CoreSettings.LastFmNowPlaying, true);

        await _otherService.Received(1).InitializeAsync();
    }

    [Fact]
    public async Task OnTrackChanged_WhenTrackClearedAfterPlaying_BroadcastsStopToActiveServices()
    {
        _settingsService.Set(CoreSettings.DiscordRichPresence, true);
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
    public async Task ShutdownAsync_DisposesEveryActiveService()
    {
        _settingsService.Set(CoreSettings.DiscordRichPresence, true);
        _settingsService.Set(CoreSettings.LastFmNowPlaying, true);
        await _manager.InitializeAsync();

        await _manager.ShutdownAsync();

        await _discordService.Received(1).OnPlaybackStoppedAsync();
        await _otherService.Received(1).OnPlaybackStoppedAsync();
        await _discordService.Received(1).DisposeAsync();
        await _otherService.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task OnPositionChanged_ThrottlesRepeatedUpdatesButResetsForNewTrack()
    {
        _settingsService.Set(CoreSettings.DiscordRichPresence, true);
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

    [Fact]
    public async Task InitializeAsync_WhenServiceFails_CleansItUpAndActivatesOthers()
    {
        _settingsService.Set(CoreSettings.DiscordRichPresence, true);
        _settingsService.Set(CoreSettings.LastFmNowPlaying, true);
        _discordService.InitializeAsync().ThrowsAsync(new InvalidOperationException("Initialization failed"));

        await _manager.InitializeAsync();
        _playbackService.PlaybackStateChanged += Raise.Event<Action>();

        await _discordService.Received(1).DisposeAsync();
        await _discordService.DidNotReceive().OnPlaybackStateChangedAsync(Arg.Any<bool>());
        await _otherService.Received(1).InitializeAsync();
        await _otherService.Received(1).OnPlaybackStateChangedAsync(false);
    }

    [Fact]
    public async Task Broadcast_WhenOneServiceFails_StillReachesOthers()
    {
        _settingsService.Set(CoreSettings.DiscordRichPresence, true);
        _settingsService.Set(CoreSettings.LastFmNowPlaying, true);
        await _manager.InitializeAsync();
        _discordService.OnPlaybackStateChangedAsync(true).ThrowsAsync(new InvalidOperationException("Provider failed"));

        _playbackService.IsPlaying.Returns(true);
        _playbackService.PlaybackStateChanged += Raise.Event<Action>();

        await _discordService.Received(1).OnPlaybackStateChangedAsync(true);
        await _otherService.Received(1).OnPlaybackStateChangedAsync(true);
    }
}
