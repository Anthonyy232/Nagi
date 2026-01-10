using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations.Presence;

/// <summary>
///     Orchestrates presence services by listening to music playback and settings events.
///     This manager activates, deactivates, and broadcasts updates to all relevant
///     IPresenceService implementations like Discord and Last.fm.
/// </summary>
public class PresenceManager : IPresenceManager, IDisposable
{
    private readonly List<IPresenceService> _activeServices = new();
    private readonly ILogger<PresenceManager> _logger;
    private readonly IMusicPlaybackService _playbackService;
    private readonly IReadOnlyDictionary<string, IPresenceService> _presenceServices;
    private readonly ISettingsService _settingsService;
    private readonly SemaphoreSlim _servicesLock = new(1, 1);

    private Song? _currentTrack;
    private bool _isInitialized;

    // Presence position updates don't need to be frequent
    private DateTime _lastPresencePositionUpdate = DateTime.MinValue;
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

        // Store services in a dictionary for efficient lookups by name.
        _presenceServices = presenceServices.ToDictionary(s => s.Name, s => s);
    }

    public void Dispose()
    {
        ShutdownAsync().GetAwaiter().GetResult();
        _servicesLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;
        _logger.LogDebug("Initializing Presence Manager.");

        // Activate services based on their initial settings in parallel.
        var initTasks = _presenceServices.Values.Select(UpdateServiceActivationAsync);
        await Task.WhenAll(initTasks).ConfigureAwait(false);

        SubscribeToEvents();
        _isInitialized = true;
    }

    public async Task ShutdownAsync()
    {
        if (!_isInitialized) return;
        _logger.LogDebug("Shutting down Presence Manager.");

        UnsubscribeFromEvents();

        await BroadcastAsync(service => service.OnPlaybackStoppedAsync()).ConfigureAwait(false);

        List<IAsyncDisposable> disposables;
        await _servicesLock.WaitAsync().ConfigureAwait(false);
        try
        {
            disposables = _activeServices.OfType<IAsyncDisposable>().ToList();
            _activeServices.Clear();
        }
        finally
        {
            _servicesLock.Release();
        }

        var disposalTasks = disposables.Select(service => service.DisposeAsync().AsTask());
        await Task.WhenAll(disposalTasks).ConfigureAwait(false);
        
        _isInitialized = false;
    }

    private void SubscribeToEvents()
    {
        _playbackService.TrackChanged += OnTrackChanged;
        _playbackService.PlaybackStateChanged += OnPlaybackStateChanged;
        _playbackService.PositionChanged += OnPositionChanged;
        _settingsService.LastFmSettingsChanged += OnLastFmSettingsChanged;
        _settingsService.DiscordRichPresenceSettingChanged += OnDiscordRichPresenceSettingChanged;
    }

    private void UnsubscribeFromEvents()
    {
        _playbackService.TrackChanged -= OnTrackChanged;
        _playbackService.PlaybackStateChanged -= OnPlaybackStateChanged;
        _playbackService.PositionChanged -= OnPositionChanged;
        _settingsService.LastFmSettingsChanged -= OnLastFmSettingsChanged;
        _settingsService.DiscordRichPresenceSettingChanged -= OnDiscordRichPresenceSettingChanged;
    }

    private async void OnDiscordRichPresenceSettingChanged(bool isEnabled)
    {
        try
        {
            if (_presenceServices.TryGetValue("Discord", out var service)) await SetServiceActiveAsync(service, isEnabled).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update Discord presence setting.");
        }
    }

    private async void OnLastFmSettingsChanged()
    {
        try
        {
            if (_presenceServices.TryGetValue("Last.fm", out var service)) await UpdateServiceActivationAsync(service).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update Last.fm presence setting.");
        }
    }

    private async Task UpdateServiceActivationAsync(IPresenceService service)
    {
        var shouldActivate = service.Name switch
        {
            "Discord" => await _settingsService.GetDiscordRichPresenceEnabledAsync().ConfigureAwait(false),
            "Last.fm" => await IsLastFmServiceEnabledAsync().ConfigureAwait(false),
            _ => false
        };

        await SetServiceActiveAsync(service, shouldActivate).ConfigureAwait(false);
    }

    private async Task<bool> IsLastFmServiceEnabledAsync()
    {
        var hasCredentials = await _settingsService.GetLastFmCredentialsAsync().ConfigureAwait(false) != null;
        if (!hasCredentials) return false;

        var isScrobblingEnabled = await _settingsService.GetLastFmScrobblingEnabledAsync().ConfigureAwait(false);
        var isNowPlayingEnabled = await _settingsService.GetLastFmNowPlayingEnabledAsync().ConfigureAwait(false);

        return isScrobblingEnabled || isNowPlayingEnabled;
    }

    private async Task SetServiceActiveAsync(IPresenceService service, bool shouldBeActive)
    {
        await _servicesLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var isActive = _activeServices.Contains(service);
            if (shouldBeActive == isActive) return;

            if (shouldBeActive)
            {
                try
                {
                    await service.InitializeAsync().ConfigureAwait(false);
                    _activeServices.Add(service);
                    _logger.LogDebug("Activated '{ServiceName}' presence service.", service.Name);

                    // If a track is already playing, immediately update the newly activated service.
                    if (_currentTrack is not null && _playbackService.CurrentListenHistoryId.HasValue)
                    {
                        await service.OnTrackChangedAsync(_currentTrack, _playbackService.CurrentListenHistoryId.Value).ConfigureAwait(false);
                        await service.OnPlaybackStateChangedAsync(_playbackService.IsPlaying).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize '{ServiceName}' presence service.", service.Name);
                }
            }
            else
            {
                _activeServices.Remove(service);
                try
                {
                    await service.OnPlaybackStoppedAsync().ConfigureAwait(false);
                    if (service is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    
                    _logger.LogDebug("Deactivated '{ServiceName}' presence service.", service.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to deactivate '{ServiceName}' presence service.", service.Name);
                }
            }
        }
        finally
        {
            _servicesLock.Release();
        }
    }

    private async void OnTrackChanged()
    {
        try
        {
            var newTrack = _playbackService.CurrentTrack;
            if (_currentTrack?.Id == newTrack?.Id) return;

            _currentTrack = newTrack;
            
            // Reset throttle state so new track gets immediate presence update
            _lastPresencePositionUpdate = DateTime.MinValue;

            _logger.LogDebug("Track changed to '{TrackTitle}'. Broadcasting to active services.",
                _currentTrack?.Title ?? "None");

            if (_currentTrack is not null && _playbackService.CurrentListenHistoryId.HasValue)
                await BroadcastAsync(s =>
                    s.OnTrackChangedAsync(_currentTrack, _playbackService.CurrentListenHistoryId.Value)).ConfigureAwait(false);
            else
                await BroadcastAsync(s => s.OnPlaybackStoppedAsync()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle track change event.");
        }
    }

    private async void OnPlaybackStateChanged()
    {
        try
        {
            _logger.LogDebug("Playback state changed. IsPlaying: {IsPlaying}. Broadcasting to active services.",
                _playbackService.IsPlaying);
            await BroadcastAsync(s => s.OnPlaybackStateChangedAsync(_playbackService.IsPlaying)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle playback state change event.");
        }
    }

    private async void OnPositionChanged()
    {
        try
        {
            if (_currentTrack is null || _playbackService.Duration <= TimeSpan.Zero) return;

            // Throttle presence updates to every 5 seconds
            var now = DateTime.UtcNow;
            if (now - _lastPresencePositionUpdate < PresenceThrottleInterval)
                return;
            _lastPresencePositionUpdate = now;

            await BroadcastAsync(s => s.OnTrackProgressAsync(_playbackService.CurrentPosition, _playbackService.Duration)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle position change event.");
        }
    }

    private async Task BroadcastAsync(Func<IPresenceService, Task> action)
    {
        List<IPresenceService> services;
        await _servicesLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_activeServices.Any()) return;
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
            // Log errors from individual services without stopping others.
            _logger.LogError(ex, "An error occurred while broadcasting an event to the '{ServiceName}' service.",
                service.Name);
        }
    }
}