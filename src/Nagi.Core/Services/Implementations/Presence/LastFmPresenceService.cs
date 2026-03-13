using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations.Presence;

/// <summary>
///     Manages Last.fm integration, including "Now Playing" updates and scrobbling.
///     Scrobble eligibility is determined centrally by <see cref="MusicPlaybackService" /> which
///     raises <c>ScrobbleEligibilityReached</c>; this service is only responsible for submitting
///     the scrobble to Last.fm and marking the session as scrobbled in the database.
/// </summary>
public class LastFmPresenceService : IPresenceService, IAsyncDisposable
{
    private readonly ILibraryWriter _libraryWriter;
    private readonly ILogger<LastFmPresenceService> _logger;
    private readonly ILastFmScrobblerService _scrobblerService;
    private readonly ISettingsService _settingsService;

    private bool _isNowPlayingEnabled;
    private bool _isScrobblingEnabled;
    private DateTime _playbackStartTime;

    public LastFmPresenceService(
        ILastFmScrobblerService scrobblerService,
        ILibraryWriter libraryWriter,
        ISettingsService settingsService,
        ILogger<LastFmPresenceService> logger)
    {
        _scrobblerService = scrobblerService;
        _libraryWriter = libraryWriter;
        _settingsService = settingsService;
        _logger = logger;
    }

    public string Name => "Last.fm";

    /// <summary>
    ///     Executes an async action with proper error handling for event handlers.
    /// </summary>
    private void FireAndForgetSafe(Func<Task> asyncAction, string operationName)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await asyncAction().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in fire-and-forget operation: {Operation}", operationName);
            }
        });
    }

    public async Task InitializeAsync()
    {
        _logger.LogDebug("Initializing Last.fm Presence Service.");
        _settingsService.LastFmSettingsChanged += OnSettingsChanged;
        await UpdateLocalSettingsAsync().ConfigureAwait(false);
    }

    public async Task OnTrackChangedAsync(Song song, long listenHistoryId)
    {
        _playbackStartTime = DateTime.UtcNow;

        if (_isNowPlayingEnabled)
            try
            {
                await _scrobblerService.UpdateNowPlayingAsync(song).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update Last.fm 'Now Playing' for track {TrackTitle}.", song.Title);
            }
    }

    public Task OnPlaybackStateChangedAsync(bool isPlaying) => Task.CompletedTask;

    public Task OnPlaybackStoppedAsync() => Task.CompletedTask;

    /// <summary>
    ///     No-op: eligibility tracking is handled by <see cref="MusicPlaybackService" />, which
    ///     raises <c>ScrobbleEligibilityReached</c> and routes it to
    ///     <see cref="OnTrackEligibleForScrobblingAsync" /> via <see cref="PresenceManager" />.
    /// </summary>
    public Task OnTrackProgressAsync(TimeSpan progress, TimeSpan duration) => Task.CompletedTask;

    /// <summary>
    ///     Attempts to scrobble the track to Last.fm immediately (real-time scrobble).
    ///     On success, marks the session as scrobbled in the database. On failure, the session
    ///     remains eligible so a background service can retry later.
    /// </summary>
    public async Task OnTrackEligibleForScrobblingAsync(Song song, long listenHistoryId)
    {
        if (!_isScrobblingEnabled) return;

        _logger.LogDebug("Track '{TrackTitle}' is eligible for scrobbling. Attempting real-time submission.", song.Title);

        try
        {
            if (await _scrobblerService.ScrobbleAsync(song, _playbackStartTime).ConfigureAwait(false))
            {
                _logger.LogDebug("Successfully scrobbled track '{TrackTitle}' in real-time.", song.Title);
                await _libraryWriter.MarkListenAsScrobbledAsync(listenHistoryId).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Real-time scrobble for '{TrackTitle}' failed. It will be handled by the background service.",
                song.Title);
        }
    }

    public ValueTask DisposeAsync()
    {
        _settingsService.LastFmSettingsChanged -= OnSettingsChanged;
        return ValueTask.CompletedTask;
    }

    private void OnSettingsChanged()
    {
        FireAndForgetSafe(
            async () => await UpdateLocalSettingsAsync().ConfigureAwait(false),
            "Last.fm settings update");
    }

    private async Task UpdateLocalSettingsAsync()
    {
        _isNowPlayingEnabled = await _settingsService.GetLastFmNowPlayingEnabledAsync().ConfigureAwait(false);
        _isScrobblingEnabled = await _settingsService.GetLastFmScrobblingEnabledAsync().ConfigureAwait(false);
        _logger.LogDebug(
            "Updated Last.fm settings. Now Playing: {IsNowPlayingEnabled}, Scrobbling: {IsScrobblingEnabled}",
            _isNowPlayingEnabled, _isScrobblingEnabled);
    }
}