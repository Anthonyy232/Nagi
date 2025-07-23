using Microsoft.Extensions.DependencyInjection;
using Nagi.Models;
using Nagi.Services.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Nagi.Services.Presence;

/// <summary>
/// Listens to the MusicPlaybackService and broadcasts events to all enabled IPresenceService implementations.
/// </summary>
public class PresenceManager : IPresenceManager, IDisposable {
    private readonly IMusicPlaybackService _playbackService;
    private readonly IEnumerable<IPresenceService> _presenceServices;
    private readonly ISettingsService _settingsService;
    private readonly List<IPresenceService> _activeServices = new();
    private Song? _currentTrack;
    private bool _isInitialized;

    public PresenceManager(
        IMusicPlaybackService playbackService,
        IEnumerable<IPresenceService> presenceServices,
        ISettingsService settingsService) {
        _playbackService = playbackService;
        _presenceServices = presenceServices;
        _settingsService = settingsService;
    }

    public async Task InitializeAsync() {
        if (_isInitialized) return;

        // Activate services based on current settings.
        foreach (var service in _presenceServices) {
            await UpdateServiceActivationAsync(service);
        }

        SubscribeToPlaybackEvents();
        SubscribeToSettingsEvents();
        _isInitialized = true;
    }

    private void SubscribeToPlaybackEvents() {
        _playbackService.TrackChanged += OnTrackChanged;
        _playbackService.PlaybackStateChanged += OnPlaybackStateChanged;
        _playbackService.PositionChanged += OnPositionChanged;
    }

    private void UnsubscribeFromPlaybackEvents() {
        _playbackService.TrackChanged -= OnTrackChanged;
        _playbackService.PlaybackStateChanged -= OnPlaybackStateChanged;
        _playbackService.PositionChanged -= OnPositionChanged;
    }

    private void SubscribeToSettingsEvents() {
        _settingsService.LastFmSettingsChanged += OnLastFmSettingsChanged;
        _settingsService.DiscordRichPresenceSettingChanged += OnDiscordRichPresenceSettingChanged;
    }

    private void UnsubscribeFromSettingsEvents() {
        _settingsService.LastFmSettingsChanged -= OnLastFmSettingsChanged;
        _settingsService.DiscordRichPresenceSettingChanged -= OnDiscordRichPresenceSettingChanged;
    }

    private async void OnDiscordRichPresenceSettingChanged(bool isEnabled) {
        var service = _presenceServices.FirstOrDefault(s => s.Name == "Discord");
        if (service != null) {
            await SetServiceActive(service, isEnabled);
        }
    }

    private async void OnLastFmSettingsChanged() {
        var service = _presenceServices.FirstOrDefault(s => s.Name == "Last.fm");
        if (service != null) {
            await UpdateServiceActivationAsync(service);
        }
    }

    private async Task UpdateServiceActivationAsync(IPresenceService service) {
        bool shouldActivate = false;
        if (service.Name == "Discord") {
            shouldActivate = await _settingsService.GetDiscordRichPresenceEnabledAsync();
        }
        else if (service.Name == "Last.fm") {
            bool hasLastFmCreds = (await _settingsService.GetLastFmCredentialsAsync()) != null;
            shouldActivate = hasLastFmCreds &&
                             (await _settingsService.GetLastFmScrobblingEnabledAsync() || await _settingsService.GetLastFmNowPlayingEnabledAsync());
        }

        await SetServiceActive(service, shouldActivate);
    }

    private async Task SetServiceActive(IPresenceService service, bool shouldBeActive) {
        bool isActive = _activeServices.Contains(service);
        if (shouldBeActive == isActive) return;

        if (shouldBeActive) {
            try {
                await service.InitializeAsync();
                _activeServices.Add(service);
                Debug.WriteLine($"[PresenceManager] Activated '{service.Name}' presence service.");

                // If a track is playing, update its presence immediately.
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
            await BroadcastAsync(service => service.OnTrackChangedAsync(_currentTrack, _playbackService.CurrentListenHistoryId.Value));
        }
        else {
            await BroadcastAsync(service => service.OnPlaybackStoppedAsync());
        }
    }

    private async void OnPlaybackStateChanged() {
        if (!_activeServices.Any()) return;
        await BroadcastAsync(service => service.OnPlaybackStateChangedAsync(_playbackService.IsPlaying));
    }

    private async void OnPositionChanged() {
        if (_currentTrack is null || _playbackService.Duration <= TimeSpan.Zero) return;
        await BroadcastAsync(service => service.OnTrackProgressAsync(_playbackService.CurrentPosition, _playbackService.Duration));
    }

    private async Task BroadcastAsync(Func<IPresenceService, Task> action) {
        if (!_activeServices.Any()) return;
        var tasks = _activeServices.Select(service => SafeExecuteAsync(service, action));
        await Task.WhenAll(tasks);
    }

    private async Task SafeExecuteAsync(IPresenceService service, Func<IPresenceService, Task> action) {
        try {
            await action(service);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[PresenceManager] Error executing action for '{service.Name}': {ex.Message}");
        }
    }

    public async Task ShutdownAsync() {
        UnsubscribeFromPlaybackEvents();
        UnsubscribeFromSettingsEvents();

        await BroadcastAsync(service => service.OnPlaybackStoppedAsync());
        foreach (var service in _activeServices) {
            if (service is IAsyncDisposable asyncDisposable) {
                await asyncDisposable.DisposeAsync();
            }
        }
        _activeServices.Clear();
        _isInitialized = false;
    }

    public void Dispose() {
        ShutdownAsync().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}