using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Playback;
using Nagi.Core.Settings;
using System.Diagnostics;

namespace Nagi.Core.Presence;

/// <summary>
///     Turns the presence services, such as Discord, on and off as their settings change, and passes playback on to
///     the ones that are on.
/// </summary>
public class PresenceManager : IAsyncDisposable, IDisposable
{
    private readonly List<IPresenceService> _activeServices = new();
    private readonly ILogger<PresenceManager> _logger;
    private readonly IMusicPlaybackService _playbackService;
    private readonly IReadOnlyList<IPresenceService> _presenceServices;
    private readonly ISettingsService _settingsService;

    // Queued event handlers can outlive shutdown; keep this managed semaphore usable until they drain.
    private readonly SemaphoreSlim _servicesLock = new(1, 1);

    // Each service's calls are chained, so it sees events in the order they happened
    // while a slow service (a Discord reconnect, say) doesn't hold up the others.
    private readonly Dictionary<IPresenceService, Task> _pendingCalls = new();

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

        _presenceServices = presenceServices.ToList();
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

        // On a background thread, so blocking here can't deadlock with a continuation that needs the UI thread.
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
            foreach (var service in _presenceServices)
                await SafeExecuteAsync(service, s => SetServiceActiveCoreAsync(s, s.IsEnabled)).ConfigureAwait(false);

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

            _pendingCalls.Clear();
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
        _settingsService.SettingChanged += OnSettingChanged;
    }

    private void UnsubscribeFromEvents()
    {
        _playbackService.TrackChanged -= OnTrackChanged;
        _playbackService.PlaybackStateChanged -= OnPlaybackStateChanged;
        _playbackService.PositionChanged -= OnPositionChanged;
        _settingsService.SettingChanged -= OnSettingChanged;
    }

    private void OnSettingChanged(Setting setting) =>
        _ = HandleEventAsync(RefreshServicesAsync, $"{setting.Key} change");

    private async Task RefreshServicesAsync()
    {
        await _servicesLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_isInitialized || _isDisposed) return;
            foreach (var service in _presenceServices)
                await SafeExecuteAsync(service, s => SetServiceActiveCoreAsync(s, s.IsEnabled)).ConfigureAwait(false);
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
            _pendingCalls.Remove(service);
            await StopAndDisposeAsync(service).ConfigureAwait(false);
            _logger.LogDebug("Deactivated '{ServiceName}' presence service.", service.GetType().Name);
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
        _logger.LogDebug("Activated '{ServiceName}' presence service.", service.GetType().Name);

        if (_currentTrack is not null)
        {
            await service.OnTrackChangedAsync(_currentTrack).ConfigureAwait(false);
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

        // A song played again gets a new listen, which restarts its time on the profile.
        if (_currentTrack?.Id == newTrack?.Id && _currentListenSessionId == newSessionId) return;

        _currentTrack = newTrack;
        _currentListenSessionId = newSessionId;

        _lastPresencePositionUpdate = null;

        _logger.LogDebug("Track changed to '{TrackTitle}' (Session: {SessionId}). Broadcasting to active services.",
            _currentTrack?.Title ?? "None", _currentListenSessionId);

        _ = HandleEventAsync(
            () => newTrack is not null
                ? BroadcastAsync(s => s.OnTrackChangedAsync(newTrack))
                : BroadcastAsync(s => s.OnPlaybackStoppedAsync()),
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

    private async Task BroadcastAsync(Func<IPresenceService, Task> action)
    {
        var queued = new List<(IPresenceService Service, Task Previous, TaskCompletionSource Done)>();
        await _servicesLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_isInitialized || _isDisposed) return;
            foreach (var service in _activeServices)
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                queued.Add((service, _pendingCalls.GetValueOrDefault(service, Task.CompletedTask), done));
                _pendingCalls[service] = done.Task;
            }
        }
        finally
        {
            _servicesLock.Release();
        }

        await Task.WhenAll(queued.Select(call => CallAfterAsync(call.Previous, call.Service, action, call.Done))).ConfigureAwait(false);
    }

    // Runs at once when the service has nothing pending, which keeps the common case synchronous.
    private async Task CallAfterAsync(Task previous, IPresenceService service, Func<IPresenceService, Task> action,
        TaskCompletionSource done)
    {
        try
        {
            await previous.ConfigureAwait(false);
            await SafeExecuteAsync(service, action).ConfigureAwait(false);
        }
        finally
        {
            done.SetResult();
        }
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
                service.GetType().Name);
        }
    }
}
