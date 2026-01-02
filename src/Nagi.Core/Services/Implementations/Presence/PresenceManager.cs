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

    private Song? _currentTrack;
    private bool _isInitialized;

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
        // This provides a synchronous way to dispose, but calling ShutdownAsync is preferred.
        ShutdownAsync().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized) return;
        _logger.LogDebug("Initializing Presence Manager.");

        // Activate services based on their initial settings.
        foreach (var service in _presenceServices.Values) await UpdateServiceActivationAsync(service);

        SubscribeToEvents();
        _isInitialized = true;
    }

    public async Task ShutdownAsync()
    {
        if (!_isInitialized) return;
        _logger.LogDebug("Shutting down Presence Manager.");

        UnsubscribeFromEvents();

        await BroadcastAsync(service => service.OnPlaybackStoppedAsync());

        var disposalTasks = _activeServices
            .OfType<IAsyncDisposable>()
            .Select(service => service.DisposeAsync().AsTask());

        await Task.WhenAll(disposalTasks);

        _activeServices.Clear();
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
            if (_presenceServices.TryGetValue("Discord", out var service)) await SetServiceActiveAsync(service, isEnabled);
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
            if (_presenceServices.TryGetValue("Last.fm", out var service)) await UpdateServiceActivationAsync(service);
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
            "Discord" => await _settingsService.GetDiscordRichPresenceEnabledAsync(),
            "Last.fm" => await IsLastFmServiceEnabledAsync(),
            _ => false
        };

        await SetServiceActiveAsync(service, shouldActivate);
    }

    private async Task<bool> IsLastFmServiceEnabledAsync()
    {
        var hasCredentials = await _settingsService.GetLastFmCredentialsAsync() != null;
        if (!hasCredentials) return false;

        var isScrobblingEnabled = await _settingsService.GetLastFmScrobblingEnabledAsync();
        var isNowPlayingEnabled = await _settingsService.GetLastFmNowPlayingEnabledAsync();

        return isScrobblingEnabled || isNowPlayingEnabled;
    }

    private async Task SetServiceActiveAsync(IPresenceService service, bool shouldBeActive)
    {
        var isActive = _activeServices.Contains(service);
        if (shouldBeActive == isActive) return;

        if (shouldBeActive)
            try
            {
                await service.InitializeAsync();
                _activeServices.Add(service);
                _logger.LogDebug("Activated '{ServiceName}' presence service.", service.Name);

                // If a track is already playing, immediately update the newly activated service.
                if (_currentTrack is not null && _playbackService.CurrentListenHistoryId.HasValue)
                {
                    await service.OnTrackChangedAsync(_currentTrack, _playbackService.CurrentListenHistoryId.Value);
                    await service.OnPlaybackStateChangedAsync(_playbackService.IsPlaying);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize '{ServiceName}' presence service.", service.Name);
            }
        else
            try
            {
                await service.OnPlaybackStoppedAsync();
                if (service is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
                _activeServices.Remove(service);
                _logger.LogDebug("Deactivated '{ServiceName}' presence service.", service.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deactivate '{ServiceName}' presence service.", service.Name);
            }
    }

    private async void OnTrackChanged()
    {
        try
        {
            var newTrack = _playbackService.CurrentTrack;
            if (_currentTrack?.Id == newTrack?.Id) return;

            _currentTrack = newTrack;
            _logger.LogDebug("Track changed to '{TrackTitle}'. Broadcasting to active services.",
                _currentTrack?.Title ?? "None");

            if (_currentTrack is not null && _playbackService.CurrentListenHistoryId.HasValue)
                await BroadcastAsync(s =>
                    s.OnTrackChangedAsync(_currentTrack, _playbackService.CurrentListenHistoryId.Value));
            else
                await BroadcastAsync(s => s.OnPlaybackStoppedAsync());
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
            await BroadcastAsync(s => s.OnPlaybackStateChangedAsync(_playbackService.IsPlaying));
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

            await BroadcastAsync(s => s.OnTrackProgressAsync(_playbackService.CurrentPosition, _playbackService.Duration));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle position change event.");
        }
    }

    private async Task BroadcastAsync(Func<IPresenceService, Task> action)
    {
        if (!_activeServices.Any()) return;

        var broadcastTasks = _activeServices.Select(service => SafeExecuteAsync(service, action));
        await Task.WhenAll(broadcastTasks);
    }

    private async Task SafeExecuteAsync(IPresenceService service, Func<IPresenceService, Task> action)
    {
        try
        {
            await action(service);
        }
        catch (Exception ex)
        {
            // Log errors from individual services without stopping others.
            _logger.LogError(ex, "An error occurred while broadcasting an event to the '{ServiceName}' service.",
                service.Name);
        }
    }
}