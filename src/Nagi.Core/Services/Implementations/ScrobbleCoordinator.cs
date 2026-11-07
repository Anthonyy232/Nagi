using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Settings;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Tells every <see cref="IListenSubmitter" /> what is playing and sends them the listens that count: as soon as a
///     listen reaches the scrobble threshold, when a scrobbling setting or account changes, and every quarter of an hour
///     for listens that couldn't be sent before, such as while offline.
/// </summary>
public sealed class ScrobbleCoordinator : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);
    private const int MaxRetryDoublings = 4;

    private readonly IMusicPlaybackService _playback;
    private readonly IReadOnlyList<IListenSubmitter> _submitters;
    private readonly ISettingsService _settingsService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ScrobbleCoordinator> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private long? _nowPlayingListenId;
    private bool _isStarted;

    public ScrobbleCoordinator(
        IMusicPlaybackService playback,
        IEnumerable<IListenSubmitter> submitters,
        ISettingsService settingsService,
        ILogger<ScrobbleCoordinator> logger,
        TimeProvider? timeProvider = null)
    {
        _playback = playback;
        _submitters = submitters.ToList();
        _settingsService = settingsService;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void Start()
    {
        if (_isStarted) return;
        _isStarted = true;
        _playback.TrackChanged += OnTrackChanged;
        _playback.ScrobbleEligibilityReached += OnScrobbleEligibilityReached;
        _settingsService.SettingChanged += OnSettingChanged;
        _settingsService.CredentialsChanged += OnCredentialsChanged;
        _ = RetryPeriodicallyAsync(_shutdownCts.Token);
    }

    public void Dispose()
    {
        if (_shutdownCts.IsCancellationRequested) return;
        _playback.TrackChanged -= OnTrackChanged;
        _playback.ScrobbleEligibilityReached -= OnScrobbleEligibilityReached;
        _settingsService.SettingChanged -= OnSettingChanged;
        _settingsService.CredentialsChanged -= OnCredentialsChanged;
        _shutdownCts.Cancel();
        _shutdownCts.Dispose();
    }

    /// <summary>
    ///     Sends every destination its pending listens. One destination failing doesn't keep the others from theirs.
    /// </summary>
    /// <returns>False if a destination failed.</returns>
    public async Task<bool> SubmitPendingAsync(CancellationToken cancellationToken = default)
    {
        if (_shutdownCts.IsCancellationRequested) return true;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        var token = linkedCts.Token;

        var succeeded = true;
        foreach (var submitter in _submitters)
        {
            if (token.IsCancellationRequested) break;
            try
            {
                await submitter.SubmitPendingAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Couldn't send pending listens to {Destination}.", submitter.Name);
                succeeded = false;
            }
        }

        return succeeded;
    }

    private async Task RetryPeriodicallyAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RetryInterval, _timeProvider);
        var failures = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                failures = await SubmitPendingAsync(cancellationToken).ConfigureAwait(false) ? 0 : failures + 1;
                // A destination that keeps failing is asked less often, up to every four hours.
                var period = RetryInterval * Math.Pow(2, Math.Min(failures, MaxRetryDoublings));
                if (timer.Period != period) timer.Period = period;
            }
        }
        catch (OperationCanceledException)
        {
            // The app closed.
        }
    }

    private void OnTrackChanged()
    {
        // A track restored at startup isn't being listened to yet, so only a track with a listen counts as playing now.
        if (_playback.CurrentTrack is not { } song || _playback.CurrentListenHistoryId is not { } listenId ||
            listenId == _nowPlayingListenId) return;
        _nowPlayingListenId = listenId;
        _ = UpdateNowPlayingAsync(song);
    }

    // The listen is already marked eligible, so each destination's queue picks it up with any that failed earlier.
    private void OnScrobbleEligibilityReached(Song song, long listenHistoryId, DateTime listenStartedUtc) =>
        _ = SubmitPendingAsync();

    private void OnSettingChanged(Setting setting)
    {
        if (setting == CoreSettings.LastFmNowPlaying || setting == CoreSettings.ListenBrainzNowPlaying)
            OnCredentialsChanged();
        else if (setting == CoreSettings.LastFmScrobbling || setting == CoreSettings.ListenBrainzScrobbling ||
                 setting == CoreSettings.ListenBrainzServerUrl || setting == CoreSettings.ListenBrainzEnabledSinceUtc)
            _ = SubmitPendingAsync();
    }

    // A destination just signed in to, or with "now playing" just turned on, hears about the current track at once.
    private void OnCredentialsChanged()
    {
        if (_playback.CurrentTrack is { } song && _playback.CurrentListenHistoryId is not null) _ = UpdateNowPlayingAsync(song);
        _ = SubmitPendingAsync();
    }

    private Task UpdateNowPlayingAsync(Song song) =>
        Task.WhenAll(_submitters.Select(submitter => submitter.UpdateNowPlayingAsync(song)));
}
