using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Playback;
using Nagi.Core.Settings;

namespace Nagi.Core.Scrobbling;

/// <summary>
///     Tells every <see cref="IListenSubmitter" /> what is playing and sends them the listens that count: at startup, as
///     soon as a listen reaches the scrobble threshold, when one of a destination's settings or its sign-in changes, and
///     every quarter of an hour for listens that couldn't be sent before, such as while offline.
/// </summary>
public sealed class ScrobbleCoordinator : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);

    // Connecting an account saves its sign-in and then turns settings on one by one; waiting this long lets every one
    // of those changes share a single run.
    internal static readonly TimeSpan CoalesceDelay = TimeSpan.FromMilliseconds(250);

    private readonly IMusicPlaybackService _playback;
    private readonly IReadOnlyList<Destination> _destinations;
    private readonly ISettingsService _settingsService;
    private readonly IScrobblingAccounts _accounts;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ScrobbleCoordinator> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly CancellationToken _shutdown;
    private ITimer? _retryTimer;
    private long? _nowPlayingListenId;
    private bool _isStarted;

    public ScrobbleCoordinator(
        IMusicPlaybackService playback,
        IEnumerable<IListenSubmitter> submitters,
        ISettingsService settingsService,
        IScrobblingAccounts accounts,
        ILogger<ScrobbleCoordinator> logger,
        TimeProvider? timeProvider = null)
    {
        _playback = playback;
        _destinations = submitters.Select(submitter => new Destination(submitter)).ToList();
        _settingsService = settingsService;
        _accounts = accounts;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _shutdown = _shutdownCts.Token;
    }

    /// <summary>
    ///     Starts listening for changes and sends what is pending in the background.
    /// </summary>
    public void Start()
    {
        if (_isStarted) return;
        _isStarted = true;
        _playback.TrackChanged += OnTrackChanged;
        _playback.ScrobbleEligibilityReached += OnScrobbleEligibilityReached;
        _settingsService.SettingChanged += OnSettingChanged;
        _accounts.CredentialsChanged += OnCredentialsChanged;
        RequestRuns(_destinations, announce: false);
        _retryTimer = _timeProvider.CreateTimer(_ => RequestRuns(_destinations, announce: false), null, RetryInterval,
            RetryInterval);
    }

    public void Dispose()
    {
        if (_shutdownCts.IsCancellationRequested) return;
        _playback.TrackChanged -= OnTrackChanged;
        _playback.ScrobbleEligibilityReached -= OnScrobbleEligibilityReached;
        _settingsService.SettingChanged -= OnSettingChanged;
        _accounts.CredentialsChanged -= OnCredentialsChanged;
        _retryTimer?.Dispose();
        _shutdownCts.Cancel();
        _shutdownCts.Dispose();
    }

    private void OnTrackChanged()
    {
        // A track restored at startup isn't being listened to yet, so only a track with a listen counts as playing now.
        if (CurrentListen() is not { } song || _playback.CurrentListenHistoryId == _nowPlayingListenId) return;
        _nowPlayingListenId = _playback.CurrentListenHistoryId;
        _ = Task.WhenAll(_destinations.Select(destination => destination.Submitter.UpdateNowPlayingAsync(song)));
    }

    // The listen is already marked eligible, so each destination's queue picks it up with any that failed earlier.
    private void OnScrobbleEligibilityReached() =>
        RequestRuns(_destinations, announce: false);

    // A destination just signed in to, or with "now playing" just turned on, hears about the current track at once.
    private void OnSettingChanged(Setting setting) =>
        RequestRuns(_destinations.Where(destination => destination.Submitter.Settings.Contains(setting)), announce: true);

    private void OnCredentialsChanged(ScrobblingAccount account) =>
        RequestRuns(_destinations.Where(destination => destination.Submitter.Account == account), announce: true);

    private Song? CurrentListen() =>
        _playback.CurrentListenHistoryId is null ? null : _playback.CurrentTrack;

    private void RequestRuns(IEnumerable<Destination> destinations, bool announce)
    {
        foreach (var destination in destinations)
        {
            if (announce) destination.RequestAnnounce();
            _ = RunAsync(destination);
        }
    }

    private async Task RunAsync(Destination destination)
    {
        // A request made while a run waits to start is covered by that run.
        if (Interlocked.Exchange(ref destination.RunQueued, 1) == 1) return;
        try
        {
            await Task.Delay(CoalesceDelay, _timeProvider, _shutdown).ConfigureAwait(false);
            await destination.RunLock.WaitAsync(_shutdown).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Volatile.Write(ref destination.RunQueued, 0);
        try
        {
            if (destination.TakeAnnounceRequest() && CurrentListen() is { } song)
                await destination.Submitter.UpdateNowPlayingAsync(song).ConfigureAwait(false);
            await destination.Submitter.SubmitPendingAsync(_shutdown).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // The app closed; what wasn't sent waits for the next start.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't send pending listens to {Destination}.", destination.Submitter.Name);
        }
        finally
        {
            destination.RunLock.Release();
        }
    }

    private sealed class Destination(IListenSubmitter submitter)
    {
        // A destination's runs take turns, so two of them can't send the same listen.
        public readonly SemaphoreSlim RunLock = new(1, 1);
        public int RunQueued;
        private int _announceRequested;

        public IListenSubmitter Submitter { get; } = submitter;

        public void RequestAnnounce() => Volatile.Write(ref _announceRequested, 1);

        public bool TakeAnnounceRequest() => Interlocked.Exchange(ref _announceRequested, 0) == 1;
    }
}
