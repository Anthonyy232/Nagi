using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations.Presence;

/// <summary>
///     Manages ListenBrainz integration, including "Playing Now" updates and real-time listen
///     submission. Scrobble eligibility is determined centrally by <see cref="MusicPlaybackService" />
///     which raises <c>ScrobbleEligibilityReached</c>; this service is only responsible for
///     submitting the listen to ListenBrainz and marking the session as submitted in the database.
///     Mirrors the shape of <see cref="LastFmPresenceService" />.
/// </summary>
public class ListenBrainzPresenceService : IPresenceService, IAsyncDisposable
{
    private readonly ILibraryWriter _libraryWriter;
    private readonly ILogger<ListenBrainzPresenceService> _logger;
    private readonly IListenBrainzScrobblerService _scrobblerService;
    private readonly ISettingsService _settingsService;

    private bool _isNowPlayingEnabled;
    private bool _isScrobblingEnabled;

    public ListenBrainzPresenceService(
        IListenBrainzScrobblerService scrobblerService,
        ILibraryWriter libraryWriter,
        ISettingsService settingsService,
        ILogger<ListenBrainzPresenceService> logger)
    {
        _scrobblerService = scrobblerService;
        _libraryWriter = libraryWriter;
        _settingsService = settingsService;
        _logger = logger;
    }

    public string Name => "ListenBrainz";

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
        _logger.LogDebug("Initializing ListenBrainz Presence Service.");
        _settingsService.ListenBrainzSettingsChanged += OnSettingsChanged;
        await UpdateLocalSettingsAsync().ConfigureAwait(false);
    }

    public async Task OnTrackChangedAsync(Song song, long listenHistoryId)
    {
        if (_isNowPlayingEnabled)
            try
            {
                await _scrobblerService.UpdateNowPlayingAsync(song).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to update ListenBrainz 'Playing Now' for track {TrackTitle}.", song.Title);
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
    ///     Attempts to submit the listen to ListenBrainz immediately (real-time submission).
    ///     On success, marks the session as submitted in the database. On failure, the session
    ///     remains eligible so a background service can retry later.
    /// </summary>
    public async Task OnTrackEligibleForScrobblingAsync(
        Song song,
        long listenHistoryId,
        DateTime listenStartedUtc)
    {
        if (!_isScrobblingEnabled) return;

        _logger.LogDebug(
            "Track '{TrackTitle}' is eligible for ListenBrainz. Attempting real-time submission.", song.Title);

        try
        {
            if (await _scrobblerService.SubmitListenAsync(song, listenStartedUtc).ConfigureAwait(false))
            {
                _logger.LogDebug(
                    "Successfully submitted '{TrackTitle}' to ListenBrainz in real-time.", song.Title);
                await _libraryWriter.MarkListenAsSubmittedToListenBrainzAsync(listenHistoryId).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Real-time ListenBrainz submission for '{TrackTitle}' failed. It will be handled by the background service.",
                song.Title);
        }
    }

    public ValueTask DisposeAsync()
    {
        _settingsService.ListenBrainzSettingsChanged -= OnSettingsChanged;
        return ValueTask.CompletedTask;
    }

    private void OnSettingsChanged()
    {
        FireAndForgetSafe(
            async () => await UpdateLocalSettingsAsync().ConfigureAwait(false),
            "ListenBrainz settings update");
    }

    private async Task UpdateLocalSettingsAsync()
    {
        _isNowPlayingEnabled = await _settingsService.GetListenBrainzNowPlayingEnabledAsync().ConfigureAwait(false);
        _isScrobblingEnabled = await _settingsService.GetListenBrainzScrobblingEnabledAsync().ConfigureAwait(false);
        _logger.LogDebug(
            "Updated ListenBrainz settings. Playing Now: {IsNowPlayingEnabled}, Scrobbling: {IsScrobblingEnabled}",
            _isNowPlayingEnabled, _isScrobblingEnabled);
    }
}
