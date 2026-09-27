using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using System.Diagnostics;

namespace Nagi.Core.Services.Implementations.Presence;

/// <summary>
///     Orchestrates presence services by listening to music playback and settings events.
///     This manager activates, deactivates, and broadcasts updates to all relevant
///     IPresenceService implementations like Discord and Last.fm.
/// </summary>
public class PresenceManager : IPresenceManager, IAsyncDisposable, IDisposable
{
    private readonly List<IPresenceService> _activeServices = new();
    private readonly ILogger<PresenceManager> _logger;
    private readonly IMusicPlaybackService _playbackService;
    private readonly IReadOnlyDictionary<string, IPresenceService> _presenceServices;
    private readonly ISettingsService _settingsService;

    // Queued event handlers can outlive shutdown; keep this managed semaphore usable until they drain.
    private readonly SemaphoreSlim _servicesLock = new(1, 1);

    private Song? _currentTrack;
    private long? _currentListenSessionId;
    private bool _isInitialized;
    private bool _isDisposed;

    private long? _lastPresencePositionUpdate;
    private static readonly TimeSpan PresenceThrottleInterval = TimeSpan.FromSeconds(5);

    public PresenceManager(
        IMusicPlaybackService playbackService,
        IEnumerable<IPresenceService> presenceServices,
        ISettingsService settingsService,
        ILogger<PresenceManager> logger)
    {
        _playbackService = playbackService;
        _settingsService = settingsService;
        _logger = logger;

        _presenceServices = presenceServices.ToDictionary(s => s.Name, s => s);
    }

    private async Task HandleEventAsync(Func<Task> asyncAction, string operationName)
    {
        try
        {
            await asyncAction().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling presence event: {Operation}", operationName);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await ShutdownAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        // Synchronous disposal - use Task.Run to avoid deadlock on UI thread
        Task.Run(() => ShutdownAsync()).GetAwaiter().GetResult();
    }

    public async Task InitializeAsync()
    {
        await _servicesLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_isInitialized) return;
            _logger.LogDebug("Initializing Presence Manager.");

            _currentTrack = _playbackService.CurrentTrack;
            _currentListenSessionId = _playbackService.CurrentListenHistoryId;
            foreach (var service in _presenceServices.Values)
                await SafeExecuteAsync(service, async s =>
                    await SetServiceActiveCoreAsync(s, await IsServiceEnabledAsync(s).ConfigureAwait(false))
                        .ConfigureAwait(false)).ConfigureAwait(false);

            SubscribeToEvents();
            _isInitialized = true;
        }
        finally
        {
            _servicesLock.Release();
        }

        // Playback may have changed while services were connecting, before event subscription.
        OnTrackChanged();
    }

    public async Task ShutdownAsync()
    {
        await _servicesLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_isInitialized) return;
            _logger.LogDebug("Shutting down Presence Manager.");
            _isInitialized = false;
            UnsubscribeFromEvents();

            await Task.WhenAll(_activeServices.Select(StopAndDisposeAsync)).ConfigureAwait(false);
            _activeServices.Clear();
        }
        finally
        {
            _servicesLock.Release();
        }
    }

    private void SubscribeToEvents()
    {
        _playbackService.TrackChanged += OnTrackChanged;
        _playbackService.PlaybackStateChanged += OnPlaybackStateChanged;
        _playbackService.PositionChanged += OnPositionChanged;
        _playbackService.ScrobbleEligibilityReached += OnScrobbleEligibilityReached;
        _settingsService.LastFmSettingsChanged += OnLastFmSettingsChanged;
        _settingsService.ListenBrainzSettingsChanged += OnListenBrainzSettingsChanged;
        _settingsService.DiscordRichPresenceSettingChanged += OnDiscordRichPresenceSettingChanged;
    }

    private void UnsubscribeFromEvents()
    {
        _playbackService.TrackChanged -= OnTrackChanged;
        _playbackService.PlaybackStateChanged -= OnPlaybackStateChanged;
        _playbackService.PositionChanged -= OnPositionChanged;
        _playbackService.ScrobbleEligibilityReached -= OnScrobbleEligibilityReached;
        _settingsService.LastFmSettingsChanged -= OnLastFmSettingsChanged;
        _settingsService.ListenBrainzSettingsChanged -= OnListenBrainzSettingsChanged;
        _settingsService.DiscordRichPresenceSettingChanged -= OnDiscordRichPresenceSettingChanged;
    }

    private void OnDiscordRichPresenceSettingChanged(bool isEnabled)
    {
        _ = HandleEventAsync(
            async () =>
            {
                if (_presenceServices.TryGetValue("Discord", out var service))
                    await SetServiceActiveAsync(service, isEnabled).ConfigureAwait(false);
            },
            "Discord presence setting change");
    }

    private void OnLastFmSettingsChanged()
    {
        _ = HandleEventAsync(
            async () =>
            {
                if (_presenceServices.TryGetValue("Last.fm", out var service))
                    await SetServiceActiveAsync(service).ConfigureAwait(false);
            },
            "Last.fm settings change");
    }

    private void OnListenBrainzSettingsChanged()
    {
        _ = HandleEventAsync(
            async () =>
            {
                if (_presenceServices.TryGetValue("ListenBrainz", out var service))
                    await SetServiceActiveAsync(service).ConfigureAwait(false);
            },
            "ListenBrainz settings change");
    }

    private Task<bool> IsServiceEnabledAsync(IPresenceService service) =>
        service.Name switch
        {
            "Discord" => _settingsService.GetDiscordRichPresenceEnabledAsync(),
            "Last.fm" => IsLastFmServiceEnabledAsync(),
            "ListenBrainz" => IsListenBrainzServiceEnabledAsync(),
            _ => Task.FromResult(false)
        };

    private async Task<bool> IsLastFmServiceEnabledAsync()
    {
        var hasCredentials = await _settingsService.GetLastFmCredentialsAsync().ConfigureAwait(false) != null;
        if (!hasCredentials) return false;

        var isScrobblingEnabled = await _settingsService.GetLastFmScrobblingEnabledAsync().ConfigureAwait(false);
        var isNowPlayingEnabled = await _settingsService.GetLastFmNowPlayingEnabledAsync().ConfigureAwait(false);

        return isScrobblingEnabled || isNowPlayingEnabled;
    }

    private async Task<bool> IsListenBrainzServiceEnabledAsync()
    {
        var token = await _settingsService.GetListenBrainzUserTokenAsync().ConfigureAwait(false);
        if (string.IsNullOrEmpty(token)) return false;

        var isScrobblingEnabled = await _settingsService.GetListenBrainzScrobblingEnabledAsync().ConfigureAwait(false);
        var isNowPlayingEnabled = await _settingsService.GetListenBrainzNowPlayingEnabledAsync().ConfigureAwait(false);

        return isScrobblingEnabled || isNowPlayingEnabled;
    }

    private async Task SetServiceActiveAsync(IPresenceService service, bool? shouldBeActive = null)
    {
        await _servicesLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_isInitialized || _isDisposed) return;
            await SetServiceActiveCoreAsync(service,
                shouldBeActive ?? await IsServiceEnabledAsync(service).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            _servicesLock.Release();
        }
    }

    private async Task SetServiceActiveCoreAsync(IPresenceService service, bool shouldBeActive)
    {
        if (shouldBeActive == _activeServices.Contains(service)) return;

        if (!shouldBeActive)
        {
            _activeServices.Remove(service);
            await StopAndDisposeAsync(service).ConfigureAwait(false);
            _logger.LogDebug("Deactivated '{ServiceName}' presence service.", service.Name);
            return;
        }

        try
        {
            await service.InitializeAsync().ConfigureAwait(false);
        }
        catch
        {
            await SafeExecuteAsync(service, s => s.DisposeAsync().AsTask()).ConfigureAwait(false);
            throw;
        }

        _activeServices.Add(service);
        _logger.LogDebug("Activated '{ServiceName}' presence service.", service.Name);

        if (_currentTrack is not null)
        {
            var sessionId = _playbackService.CurrentListenHistoryId;
            if (service.Name == "Discord" || sessionId.HasValue)
                await service.OnTrackChangedAsync(_currentTrack, sessionId ?? 0).ConfigureAwait(false);
            await service.OnPlaybackStateChangedAsync(_playbackService.IsPlaying).ConfigureAwait(false);
        }
    }

    private async Task StopAndDisposeAsync(IPresenceService service)
    {
        await SafeExecuteAsync(service, s => s.OnPlaybackStoppedAsync()).ConfigureAwait(false);
        await SafeExecuteAsync(service, s => s.DisposeAsync().AsTask()).ConfigureAwait(false);
    }

    private void OnTrackChanged()
    {
        var newTrack = _playbackService.CurrentTrack;
        var newSessionId = _playbackService.CurrentListenHistoryId;

        // Return if both track and session ID are unchanged (prevents redundant updates, but allows loops)
        if (_currentTrack?.Id == newTrack?.Id && _currentListenSessionId == newSessionId) return;

        _currentTrack = newTrack;
        _currentListenSessionId = newSessionId;

        // Reset throttle state so new track gets immediate presence update
        _lastPresencePositionUpdate = null;

        _logger.LogDebug("Track changed to '{TrackTitle}' (Session: {SessionId}). Broadcasting to active services.",
            _currentTrack?.Title ?? "None", _currentListenSessionId);

        _ = HandleEventAsync(
            async () =>
            {
                if (newTrack is not null)
                {
                    await BroadcastAsync(s =>
                    {
                        // Discord needs to know about the track immediately (even at startup restoration)
                        // Last.fm should only receive tracks that have a valid session ID to avoid scrobbling errors
                        if (s.Name == "Discord" || newSessionId.HasValue)
                        {
                            return s.OnTrackChangedAsync(newTrack, newSessionId ?? 0);
                        }

                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }
                else
                {
                    await BroadcastAsync(s => s.OnPlaybackStoppedAsync()).ConfigureAwait(false);
                }
            },
            "Track change broadcast");
    }

    private void OnPlaybackStateChanged()
    {
        var isPlaying = _playbackService.IsPlaying;
        _logger.LogDebug("Playback state changed. IsPlaying: {IsPlaying}. Broadcasting to active services.",
            isPlaying);

        _ = HandleEventAsync(
            () => BroadcastAsync(s => s.OnPlaybackStateChangedAsync(isPlaying)),
            "Playback state change broadcast");
    }

    private void OnPositionChanged()
    {
        if (_currentTrack is null || _playbackService.Duration <= TimeSpan.Zero) return;

        // Throttle presence updates to every 5 seconds
        var now = Stopwatch.GetTimestamp();
        if (_lastPresencePositionUpdate is { } lastUpdate &&
            Stopwatch.GetElapsedTime(lastUpdate, now) < PresenceThrottleInterval)
            return;
        _lastPresencePositionUpdate = now;

        var position = _playbackService.CurrentPosition;
        var duration = _playbackService.Duration;
        _ = HandleEventAsync(
            () => BroadcastAsync(s => s.OnTrackProgressAsync(position, duration)),
            "Position change broadcast");
    }

    private void OnScrobbleEligibilityReached(Song song, long listenHistoryId, DateTime listenStartedUtc)
    {
        _ = HandleEventAsync(
            () => BroadcastAsync(s => s.OnTrackEligibleForScrobblingAsync(song, listenHistoryId, listenStartedUtc)),
            "Scrobble eligibility broadcast");
    }

    private async Task BroadcastAsync(Func<IPresenceService, Task> action)
    {
        List<IPresenceService> services;
        await _servicesLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_isInitialized || _isDisposed) return;
            services = _activeServices.ToList();
        }
        finally
        {
            _servicesLock.Release();
        }

        var broadcastTasks = services.Select(service => SafeExecuteAsync(service, action));
        await Task.WhenAll(broadcastTasks).ConfigureAwait(false);
    }

    private async Task SafeExecuteAsync(IPresenceService service, Func<IPresenceService, Task> action)
    {
        try
        {
            await action(service).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An operation failed for the '{ServiceName}' presence service.",
                service.Name);
        }
    }
}
