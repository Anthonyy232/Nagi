using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     A background service that periodically submits pending scrobbles that were not
///     successfully submitted in real-time. This service is a thin orchestrator that fans
///     queue processing out to every registered <see cref="IListenSubmitter" />. Each
///     submitter owns its own DB-query + HTTP-submit flow; failures in one submitter do
///     not block the others.
/// </summary>
public class OfflineScrobbleService : IOfflineScrobbleService, IDisposable
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BaseCheckInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxCheckInterval = TimeSpan.FromHours(4);
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private readonly ILogger<OfflineScrobbleService> _logger;
    private readonly ISettingsService _settingsService;
    private readonly IReadOnlyList<IListenSubmitter> _submitters;

    // A lock-free flag to ensure only one processing task runs at a time.
    // 0 = not processing, 1 = processing.
    private int _isProcessingQueue;

    // Tracks consecutive failures for exponential backoff.
    private int _consecutiveFailures;

    public OfflineScrobbleService(
        IEnumerable<IListenSubmitter> submitters,
        ISettingsService settingsService,
        ILogger<OfflineScrobbleService> logger)
    {
        _submitters = submitters.ToList();
        _settingsService = settingsService;
        _logger = logger;
        _settingsService.LastFmSettingsChanged += OnSettingsChanged;
        _settingsService.ListenBrainzSettingsChanged += OnSettingsChanged;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _logger.LogDebug("Disposing OfflineScrobbleService.");
        _settingsService.LastFmSettingsChanged -= OnSettingsChanged;
        _settingsService.ListenBrainzSettingsChanged -= OnSettingsChanged;
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public void Start()
    {
        _logger.LogDebug("Starting offline scrobble service background loop.");
        // Fire-and-forget the long-running task to run in the background.
        _ = Task.Run(() => ScrobbleLoopAsync(_cancellationTokenSource.Token));
    }

    /// <inheritdoc />
    public async Task ProcessQueueAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return;

        // Atomically check if processing is already running. If _isProcessingQueue is 0,
        // set it to 1 and proceed. If it's already 1, another thread is processing, so we exit.
        if (Interlocked.CompareExchange(ref _isProcessingQueue, 1, 0) != 0)
        {
            _logger.LogDebug("Queue processing is already in progress; skipping.");
            return;
        }

        try
        {
            _logger.LogDebug("Starting to process pending scrobbles across {SubmitterCount} submitter(s).",
                _submitters.Count);

            var anyProcessed = false;
            var anyFailure = false;

            foreach (var submitter in _submitters)
            {
                if (cancellationToken.IsCancellationRequested) break;

                bool enabled;
                try
                {
                    enabled = await submitter.IsEnabledAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Submitter '{Id}' failed IsEnabledAsync check.", submitter.Id);
                    anyFailure = true;
                    continue;
                }

                if (!enabled)
                {
                    _logger.LogDebug("Submitter '{Id}' is disabled; skipping.", submitter.Id);
                    continue;
                }

                try
                {
                    await submitter.ProcessPendingListensAsync(cancellationToken).ConfigureAwait(false);
                    anyProcessed = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Submitter '{Id}' raised during queue processing.", submitter.Id);
                    anyFailure = true;
                }
            }

            if (anyFailure) _consecutiveFailures++;
            else if (anyProcessed) _consecutiveFailures = 0;
        }
        finally
        {
            // Unconditionally release the lock, allowing the next run.
            Interlocked.Exchange(ref _isProcessingQueue, 0);
        }
    }

    /// <summary>
    ///     The main background loop that periodically triggers queue processing.
    /// </summary>
    private async Task ScrobbleLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(InitialDelay, cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                // Wrap the core work in a try/catch to make the loop resilient to unexpected errors.
                // A single failed run will not terminate the background service.
                try
                {
                    await ProcessQueueAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogCritical(ex, "An unhandled exception occurred in the scrobble processing loop.");
                    _consecutiveFailures++;
                }

                // Calculate wait time with exponential backoff based on consecutive failures.
                var backoffMultiplier = Math.Pow(2, Math.Min(_consecutiveFailures, 4)); // Cap at 16x
                var waitTime = TimeSpan.FromTicks((long)(BaseCheckInterval.Ticks * backoffMultiplier));
                if (waitTime > MaxCheckInterval) waitTime = MaxCheckInterval;

                if (_consecutiveFailures > 0)
                    _logger.LogDebug("Consecutive failures: {FailureCount}. Next check in {WaitTime}.",
                        _consecutiveFailures, waitTime);

                await Task.Delay(waitTime, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // This is the expected, clean way to exit the loop when Dispose is called.
            _logger.LogDebug("Offline scrobble service loop has been cancelled.");
        }
    }

    /// <summary>
    ///     Event handler that triggers an immediate queue processing when scrobbling-related
    ///     settings change (Last.fm or ListenBrainz).
    /// </summary>
    private void OnSettingsChanged()
    {
        _logger.LogDebug("Scrobbling settings changed. Triggering immediate queue processing.");
        // Pass the token to be a good citizen during shutdown.
        _ = ProcessQueueAsync(_cancellationTokenSource.Token);
    }
}
