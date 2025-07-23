using Nagi.Models;
using Nagi.Services.Abstractions;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Nagi.Services.Presence;

/// <summary>
/// Manages Last.fm integration, including "Now Playing" updates and scrobbling logic.
/// This service determines when a track is eligible for scrobbling based on playback
/// progress and attempts a real-time submission.
/// </summary>
public class LastFmPresenceService : IPresenceService {
    private readonly ILastFmScrobblerService _scrobblerService;
    private readonly ILibraryWriter _libraryWriter;
    private readonly ISettingsService _settingsService;

    private Song? _currentSong;
    private long? _currentListenHistoryId;
    private DateTime _playbackStartTime;
    private bool _isEligibilityMarked;
    private bool _isNowPlayingEnabled;
    private bool _isScrobblingEnabled;

    public string Name => "Last.fm";

    public LastFmPresenceService(
        ILastFmScrobblerService scrobblerService,
        ILibraryWriter libraryWriter,
        ISettingsService settingsService) {
        _scrobblerService = scrobblerService;
        _libraryWriter = libraryWriter;
        _settingsService = settingsService;
    }

    public async Task InitializeAsync() {
        await OnSettingsChanged();
        _settingsService.LastFmSettingsChanged += async () => await OnSettingsChanged();
    }

    public async Task OnTrackChangedAsync(Song song, long listenHistoryId) {
        _currentSong = song;
        _currentListenHistoryId = listenHistoryId;
        _playbackStartTime = DateTime.UtcNow;
        _isEligibilityMarked = false;

        if (_isNowPlayingEnabled) {
            try {
                await _scrobblerService.UpdateNowPlayingAsync(song);
            }
            catch (Exception ex) {
                Debug.WriteLine($"[LastFmPresenceService] Failed to update Now Playing: {ex.Message}");
            }
        }
    }

    public Task OnPlaybackStateChangedAsync(bool isPlaying) => Task.CompletedTask;

    public Task OnPlaybackStoppedAsync() {
        _currentSong = null;
        _currentListenHistoryId = null;
        return Task.CompletedTask;
    }

    public async Task OnTrackProgressAsync(TimeSpan progress, TimeSpan duration) {
        if (_currentSong is null || !_isScrobblingEnabled || _isEligibilityMarked || !_currentListenHistoryId.HasValue || duration <= TimeSpan.Zero) {
            return;
        }

        // Standard Last.fm scrobble criteria.
        bool isLongEnough = duration.TotalSeconds > 30;
        bool hasPlayedEnough = progress.TotalSeconds >= duration.TotalSeconds / 2 || progress.TotalMinutes >= 4;

        if (isLongEnough && hasPlayedEnough) {
            // Prevent multiple scrobble attempts for the same listening session.
            _isEligibilityMarked = true;

            // Mark the track as eligible in the database. This allows the offline service to pick it up if real-time scrobbling fails.
            await _libraryWriter.MarkListenAsEligibleForScrobblingAsync(_currentListenHistoryId.Value);
            Debug.WriteLine($"[LastFmPresenceService] Track '{_currentSong.Title}' is now eligible for scrobbling.");

            // Attempt to scrobble immediately for a real-time experience.
            try {
                if (await _scrobblerService.ScrobbleAsync(_currentSong, _playbackStartTime)) {
                    Debug.WriteLine($"[LastFmPresenceService] Successfully scrobbled track in real-time: {_currentSong.Title}");
                    await _libraryWriter.MarkListenAsScrobbledAsync(_currentListenHistoryId.Value);
                }
            }
            catch (Exception ex) {
                // If the real-time scrobble fails, the track remains eligible but not scrobbled,
                // allowing the background service to handle it later.
                Debug.WriteLine($"[LastFmPresenceService] Real-time scrobble failed for '{_currentSong.Title}'. It will be handled by the background service. Error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Updates local state when Last.fm settings change in the application.
    /// </summary>
    private async Task OnSettingsChanged() {
        _isNowPlayingEnabled = await _settingsService.GetLastFmNowPlayingEnabledAsync();
        _isScrobblingEnabled = await _settingsService.GetLastFmScrobblingEnabledAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}