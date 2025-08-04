using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
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
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly ILastFmScrobblerService _scrobblerService;
    private readonly ISettingsService _settingsService;

    // A lock-free flag to ensure only one processing task runs at a time.
    // 0 = not processing, 1 = processing.
    private int _isProcessingQueue;

    public OfflineScrobbleService(
        IDbContextFactory<MusicDbContext> contextFactory,
        ILastFmScrobblerService scrobblerService,
        ISettingsService settingsService)
    {
        _contextFactory = contextFactory;
        _scrobblerService = scrobblerService;
        _settingsService = settingsService;
        _settingsService.LastFmSettingsChanged += OnLastFmSettingsChanged;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _settingsService.LastFmSettingsChanged -= OnLastFmSettingsChanged;
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public void Start()
    {
        // Fire-and-forget the long-running task to run in the background.
        _ = Task.Run(() => ScrobbleLoopAsync(_cancellationTokenSource.Token));
    }

    /// <inheritdoc />
    public async Task ProcessQueueAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return;

        if (!await _settingsService.GetLastFmScrobblingEnabledAsync())
        {
            Debug.WriteLine("[OfflineScrobbleService] Scrobbling is disabled; skipping queue processing.");
            return;
        }

        // Atomically check if processing is already running. If _isProcessingQueue is 0,
        // set it to 1 and proceed. If it's already 1, another thread is processing, so we exit.
        if (Interlocked.CompareExchange(ref _isProcessingQueue, 1, 0) != 0)
        {
            Debug.WriteLine("[OfflineScrobbleService] Queue processing is already in progress.");
            return;
        }

        try
        {
            Debug.WriteLine("[OfflineScrobbleService] Starting to process pending scrobbles...");
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
                Debug.WriteLine("[OfflineScrobbleService] No pending scrobbles found.");
                return;
            }

            Debug.WriteLine($"[OfflineScrobbleService] Found {pendingScrobbles.Count} pending scrobbles.");
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
                        Debug.WriteLine(
                            $"[OfflineScrobbleService] Scrobble failed for '{historyEntry.Song.Title}'. Will retry later.");
                        // Stop processing on the first failure to maintain chronological order.
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[OfflineScrobbleService] Exception while scrobbling '{historyEntry.Song.Title}'. Will retry later. Error: {ex.Message}");
                    break;
                }
            }

            if (successfulScrobbles > 0)
            {
                await context.SaveChangesAsync(cancellationToken);
                Debug.WriteLine($"[OfflineScrobbleService] Successfully submitted {successfulScrobbles} scrobbles.");
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
                    Debug.WriteLine(
                        $"[OfflineScrobbleService] CRITICAL: An unhandled error occurred in the processing loop. Error: {ex.Message}");
                }

                await Task.Delay(CheckInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // This is the expected, clean way to exit the loop when Dispose is called.
            Debug.WriteLine("[OfflineScrobbleService] Scrobble loop has been cancelled.");
        }
    }

    /// <summary>
    ///     Event handler that triggers an immediate queue processing when Last.fm settings change.
    /// </summary>
    private void OnLastFmSettingsChanged()
    {
        Debug.WriteLine("[OfflineScrobbleService] Last.fm settings changed. Triggering queue processing.");
        // Pass the token to be a good citizen during shutdown.
        _ = ProcessQueueAsync(_cancellationTokenSource.Token);
    }
}