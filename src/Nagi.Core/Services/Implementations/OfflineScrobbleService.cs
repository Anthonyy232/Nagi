using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Services.Abstractions;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     A background service that periodically submits pending scrobbles that were not
///     successfully submitted in real-time. This service is designed to be resilient,
///     handling intermittent network failures and ensuring no scrobbles are lost.
/// </summary>
public class OfflineScrobbleService : IOfflineScrobbleService, IDisposable
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BaseCheckInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaxCheckInterval = TimeSpan.FromHours(4);
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly ILogger<OfflineScrobbleService> _logger;
    private readonly ILastFmScrobblerService _scrobblerService;
    private readonly ISettingsService _settingsService;

    // A lock-free flag to ensure only one processing task runs at a time.
    // 0 = not processing, 1 = processing.
    private int _isProcessingQueue;
    
    // Tracks consecutive failures for exponential backoff.
    private int _consecutiveFailures;

    public OfflineScrobbleService(
        IDbContextFactory<MusicDbContext> contextFactory,
        ILastFmScrobblerService scrobblerService,
        ISettingsService settingsService,
        ILogger<OfflineScrobbleService> logger)
    {
        _contextFactory = contextFactory;
        _scrobblerService = scrobblerService;
        _settingsService = settingsService;
        _logger = logger;
        _settingsService.LastFmSettingsChanged += OnLastFmSettingsChanged;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _logger.LogDebug("Disposing OfflineScrobbleService.");
        _settingsService.LastFmSettingsChanged -= OnLastFmSettingsChanged;
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

        if (!await _settingsService.GetLastFmScrobblingEnabledAsync())
        {
            _logger.LogDebug("Scrobbling is disabled; skipping queue processing.");
            return;
        }

        // Atomically check if processing is already running. If _isProcessingQueue is 0,
        // set it to 1 and proceed. If it's already 1, another thread is processing, so we exit.
        if (Interlocked.CompareExchange(ref _isProcessingQueue, 1, 0) != 0)
        {
            _logger.LogDebug("Queue processing is already in progress; skipping.");
            return;
        }

        try
        {
            _logger.LogDebug("Starting to process pending scrobbles...");
            // Pass the token to all async EF Core operations for clean cancellation.
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

            var pendingScrobbles = await context.ListenHistory
                .AsTracking()
                .Where(lh => lh.IsEligibleForScrobbling && !lh.IsScrobbled)
                .Include(lh => lh.Song).ThenInclude(s => s!.Artist)
                .Include(lh => lh.Song).ThenInclude(s => s!.Album)
                .OrderBy(lh => lh.ListenTimestampUtc)
                .ToListAsync(cancellationToken);

            if (cancellationToken.IsCancellationRequested) return;

            if (pendingScrobbles.Count == 0)
            {
                _logger.LogDebug("No pending scrobbles found.");
                return;
            }

            _logger.LogDebug("Found {ScrobbleCount} pending scrobbles.", pendingScrobbles.Count);
            var successfulScrobbles = 0;

            foreach (var historyEntry in pendingScrobbles)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (historyEntry.Song is null) continue;

                try
                {
                    var success =
                        await _scrobblerService.ScrobbleAsync(historyEntry.Song, historyEntry.ListenTimestampUtc);
                    if (success)
                    {
                        historyEntry.IsScrobbled = true;
                        successfulScrobbles++;
                    }
                    else
                    {
                        _logger.LogWarning("Scrobble failed for song '{SongTitle}'. Will retry later.",
                            historyEntry.Song.Title);
                        // Stop processing on the first failure to maintain chronological order.
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception while scrobbling song '{SongTitle}'. Will retry later.",
                        historyEntry.Song.Title);
                    break;
                }
            }

            if (successfulScrobbles > 0)
            {
                await context.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Successfully submitted {ScrobbleCount} scrobbles.", successfulScrobbles);
                // Reset backoff counter on success.
                _consecutiveFailures = 0;
            }
            else if (pendingScrobbles.Count > 0)
            {
                // We had pending scrobbles but submitted none - track as failure for backoff.
                _consecutiveFailures++;
            }
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
            await Task.Delay(InitialDelay, cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                // Wrap the core work in a try/catch to make the loop resilient to unexpected errors.
                // A single failed run will not terminate the background service.
                try
                {
                    await ProcessQueueAsync(cancellationToken);
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
                
                await Task.Delay(waitTime, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // This is the expected, clean way to exit the loop when Dispose is called.
            _logger.LogDebug("Offline scrobble service loop has been cancelled.");
        }
    }

    /// <summary>
    ///     Event handler that triggers an immediate queue processing when Last.fm settings change.
    /// </summary>
    private void OnLastFmSettingsChanged()
    {
        _logger.LogDebug("Last.fm settings changed. Triggering immediate queue processing.");
        // Pass the token to be a good citizen during shutdown.
        _ = ProcessQueueAsync(_cancellationTokenSource.Token);
    }
}