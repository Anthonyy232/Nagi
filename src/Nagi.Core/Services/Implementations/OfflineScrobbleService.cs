using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Periodically retries pending listens across registered scrobbling destinations.
/// </summary>
public class OfflineScrobbleService : IOfflineScrobbleService, IDisposable
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BaseCheckInterval = TimeSpan.FromMinutes(15);
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly CancellationToken _shutdownToken;

    private readonly ILogger<OfflineScrobbleService> _logger;
    private readonly ISettingsService _settingsService;
    private readonly IReadOnlyList<IListenSubmitter> _submitters;

    private int _isProcessingQueue;
    private int _consecutiveFailures;

    public OfflineScrobbleService(
        IEnumerable<IListenSubmitter> submitters,
        ISettingsService settingsService,
        ILogger<OfflineScrobbleService> logger)
    {
        _submitters = submitters.ToList();
        _settingsService = settingsService;
        _logger = logger;
        _shutdownToken = _cancellationTokenSource.Token;
        _settingsService.LastFmSettingsChanged += OnSettingsChanged;
        _settingsService.ListenBrainzSettingsChanged += OnSettingsChanged;
    }

    public void Dispose()
    {
        if (_shutdownToken.IsCancellationRequested) return;
        _logger.LogDebug("Disposing OfflineScrobbleService.");
        _settingsService.LastFmSettingsChanged -= OnSettingsChanged;
        _settingsService.ListenBrainzSettingsChanged -= OnSettingsChanged;
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
    }

    public void Start()
    {
        _logger.LogDebug("Starting offline scrobble service background loop.");
        _ = ScrobbleLoopAsync(_shutdownToken);
    }

    public async Task ProcessQueueAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        cancellationToken = linkedCancellation.Token;
        if (cancellationToken.IsCancellationRequested) return;

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

                try
                {
                    if (!await submitter.IsEnabledAsync().ConfigureAwait(false))
                    {
                        _logger.LogDebug("Submitter '{Id}' is disabled; skipping.", submitter.Id);
                        continue;
                    }
                    await submitter.ProcessPendingListensAsync(cancellationToken).ConfigureAwait(false);
                    anyProcessed = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
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
            Interlocked.Exchange(ref _isProcessingQueue, 0);
        }
    }

    private async Task ScrobbleLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(InitialDelay, cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessQueueAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogCritical(ex, "An unhandled exception occurred in the scrobble processing loop.");
                    _consecutiveFailures++;
                }

                var waitTime = BaseCheckInterval * Math.Pow(2, Math.Min(_consecutiveFailures, 4));

                if (_consecutiveFailures > 0)
                    _logger.LogDebug("Consecutive failures: {FailureCount}. Next check in {WaitTime}.",
                        _consecutiveFailures, waitTime);

                await Task.Delay(waitTime, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Offline scrobble service loop has been cancelled.");
        }
    }

    private async void OnSettingsChanged()
    {
        _logger.LogDebug("Scrobbling settings changed. Triggering immediate queue processing.");
        try
        {
            await ProcessQueueAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
        {
        }
    }
}
