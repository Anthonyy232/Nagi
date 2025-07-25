using System.Diagnostics;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations.Presence;

/// <summary>
/// Orchestrates presence services by listening to music playback and settings events.
/// This manager activates, deactivates, and broadcasts updates to all relevant
/// IPresenceService implementations like Discord and Last.fm.
/// </summary>
public class PresenceManager : IPresenceManager, IDisposable {
    private readonly IMusicPlaybackService _playbackService;
    private readonly ISettingsService _settingsService;
    private readonly IReadOnlyDictionary<string, IPresenceService> _presenceServices;
    private readonly List<IPresenceService> _activeServices = new();

    private Song? _currentTrack;
    private bool _isInitialized;

    public PresenceManager(
        IMusicPlaybackService playbackService,
        IEnumerable<IPresenceService> presenceServices,
        ISettingsService settingsService) {
        _playbackService = playbackService;
        _settingsService = settingsService;

        // Store services in a dictionary for efficient lookups by name.
        _presenceServices = presenceServices.ToDictionary(s => s.Name, s => s);
    }

    public async Task InitializeAsync() {
        if (_isInitialized) return;

        // Activate services based on their initial settings.
        foreach (var service in _presenceServices.Values) {
            await UpdateServiceActivationAsync(service);
        }

        SubscribeToEvents();
        _isInitialized = true;
    }

    private void SubscribeToEvents() {
        _playbackService.TrackChanged += OnTrackChanged;
        _playbackService.PlaybackStateChanged += OnPlaybackStateChanged;
        _playbackService.PositionChanged += OnPositionChanged;
        _settingsService.LastFmSettingsChanged += OnLastFmSettingsChanged;
        _settingsService.DiscordRichPresenceSettingChanged += OnDiscordRichPresenceSettingChanged;
    }

    private void UnsubscribeFromEvents() {
        _playbackService.TrackChanged -= OnTrackChanged;
        _playbackService.PlaybackStateChanged -= OnPlaybackStateChanged;
        _playbackService.PositionChanged -= OnPositionChanged;
        _settingsService.LastFmSettingsChanged -= OnLastFmSettingsChanged;
        _settingsService.DiscordRichPresenceSettingChanged -= OnDiscordRichPresenceSettingChanged;
    }

    private async void OnDiscordRichPresenceSettingChanged(bool isEnabled) {
        if (_presenceServices.TryGetValue("Discord", out var service)) {
            await SetServiceActiveAsync(service, isEnabled);
        }
    }

    private async void OnLastFmSettingsChanged() {
        if (_presenceServices.TryGetValue("Last.fm", out var service)) {
            await UpdateServiceActivationAsync(service);
        }
    }

    private async Task UpdateServiceActivationAsync(IPresenceService service) {
        bool shouldActivate = service.Name switch {
            "Discord" => await _settingsService.GetDiscordRichPresenceEnabledAsync(),
            "Last.fm" => await IsLastFmServiceEnabledAsync(),
            _ => false
        };

        await SetServiceActiveAsync(service, shouldActivate);
    }



    private async Task<bool> IsLastFmServiceEnabledAsync() {
        bool hasCredentials = (await _settingsService.GetLastFmCredentialsAsync()) != null;
        if (!hasCredentials) return false;

        bool isScrobblingEnabled = await _settingsService.GetLastFmScrobblingEnabledAsync();
        bool isNowPlayingEnabled = await _settingsService.GetLastFmNowPlayingEnabledAsync();

        return isScrobblingEnabled || isNowPlayingEnabled;
    }

    private async Task SetServiceActiveAsync(IPresenceService service, bool shouldBeActive) {
        bool isActive = _activeServices.Contains(service);
        if (shouldBeActive == isActive) return;

        if (shouldBeActive) {
            try {
                await service.InitializeAsync();
                _activeServices.Add(service);
                Debug.WriteLine($"[PresenceManager] Activated '{service.Name}' presence service.");

                // If a track is already playing, immediately update the newly activated service.
                if (_currentTrack is not null && _playbackService.CurrentListenHistoryId.HasValue) {
                    await service.OnTrackChangedAsync(_currentTrack, _playbackService.CurrentListenHistoryId.Value);
                    await service.OnPlaybackStateChangedAsync(_playbackService.IsPlaying);
                }
            }
            catch (Exception ex) {
                Debug.WriteLine($"[PresenceManager] Failed to initialize '{service.Name}' presence service: {ex.Message}");
            }
        }
        else {
            try {
                await service.OnPlaybackStoppedAsync();
                if (service is IAsyncDisposable asyncDisposable) {
                    await asyncDisposable.DisposeAsync();
                }
                _activeServices.Remove(service);
                Debug.WriteLine($"[PresenceManager] Deactivated '{service.Name}' presence service.");
            }
            catch (Exception ex) {
                Debug.WriteLine($"[PresenceManager] Failed to deactivate '{service.Name}' presence service: {ex.Message}");
            }
        }
    }

    private async void OnTrackChanged() {
        var newTrack = _playbackService.CurrentTrack;
        if (_currentTrack?.Id == newTrack?.Id) return;

        _currentTrack = newTrack;

        if (_currentTrack is not null && _playbackService.CurrentListenHistoryId.HasValue) {
            await BroadcastAsync(s => s.OnTrackChangedAsync(_currentTrack, _playbackService.CurrentListenHistoryId.Value));
        }
        else {
            await BroadcastAsync(s => s.OnPlaybackStoppedAsync());
        }
    }

    private async void OnPlaybackStateChanged() {
        await BroadcastAsync(s => s.OnPlaybackStateChangedAsync(_playbackService.IsPlaying));
    }

    private async void OnPositionChanged() {
        if (_currentTrack is null || _playbackService.Duration <= TimeSpan.Zero) return;

        await BroadcastAsync(s => s.OnTrackProgressAsync(_playbackService.CurrentPosition, _playbackService.Duration));
    }

    private async Task BroadcastAsync(Func<IPresenceService, Task> action) {
        if (!_activeServices.Any()) return;

        var broadcastTasks = _activeServices.Select(service => SafeExecuteAsync(service, action));
        await Task.WhenAll(broadcastTasks);
    }

    private async Task SafeExecuteAsync(IPresenceService service, Func<IPresenceService, Task> action) {
        try {
            await action(service);
        }
        catch (Exception ex) {
            // Log errors from individual services without stopping others.
            Debug.WriteLine($"[PresenceManager] Error in '{service.Name}' service: {ex.Message}");
        }
    }

    public async Task ShutdownAsync() {
        if (!_isInitialized) return;

        UnsubscribeFromEvents();

        await BroadcastAsync(service => service.OnPlaybackStoppedAsync());

        var disposalTasks = _activeServices
            .OfType<IAsyncDisposable>()
            .Select(service => service.DisposeAsync().AsTask());

        await Task.WhenAll(disposalTasks);

        _activeServices.Clear();
        _isInitialized = false;
    }

    public void Dispose() {
        // This provides a synchronous way to dispose, but calling ShutdownAsync is preferred.
        ShutdownAsync().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}