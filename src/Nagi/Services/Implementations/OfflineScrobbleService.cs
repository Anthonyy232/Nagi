using Microsoft.EntityFrameworkCore;
using Nagi.Data;
using Nagi.Services.Abstractions;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nagi.Services.Implementations;

/// <summary>
/// A background service that periodically checks for and submits pending scrobbles
/// that were eligible but not successfully submitted in real-time.
/// </summary>
public class OfflineScrobbleService : IOfflineScrobbleService, IDisposable {
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly ILastFmScrobblerService _scrobblerService;
    private readonly ISettingsService _settingsService;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(15);

    // A semaphore to prevent concurrent processing of the scrobble queue.
    private readonly SemaphoreSlim _queueLock = new(1, 1);

    public OfflineScrobbleService(
        IDbContextFactory<MusicDbContext> contextFactory,
        ILastFmScrobblerService scrobblerService,
        ISettingsService settingsService) {
        _contextFactory = contextFactory;
        _scrobblerService = scrobblerService;
        _settingsService = settingsService;

        _settingsService.LastFmSettingsChanged += OnLastFmSettingsChanged;
    }

    public void Start() {
        Task.Run(() => ScrobbleLoopAsync(_cancellationTokenSource.Token));
    }

    public async Task ProcessQueueAsync() {
        if (!await _settingsService.GetLastFmScrobblingEnabledAsync()) {
            Debug.WriteLine("[OfflineScrobbleService] Scrobbling is disabled; skipping queue processing.");
            return;
        }

        // Do not run if another processing task is already active.
        if (!await _queueLock.WaitAsync(0)) {
            Debug.WriteLine("[OfflineScrobbleService] Queue processing is already in progress; skipping this run.");
            return;
        }

        try {
            Debug.WriteLine("[OfflineScrobbleService] Starting to process pending scrobbles...");
            await using var context = await _contextFactory.CreateDbContextAsync();

            // Find all tracks that have been marked as eligible for scrobbling but have not yet been successfully scrobbled.
            var pendingScrobbles = await context.ListenHistory
                .AsTracking()
                .Where(lh => lh.IsEligibleForScrobbling && !lh.IsScrobbled)
                .Include(lh => lh.Song).ThenInclude(s => s!.Artist)
                .Include(lh => lh.Song).ThenInclude(s => s!.Album)
                .OrderBy(lh => lh.ListenTimestampUtc)
                .ToListAsync();

            if (pendingScrobbles.Count == 0) {
                Debug.WriteLine("[OfflineScrobbleService] No pending scrobbles found.");
                return;
            }

            Debug.WriteLine($"[OfflineScrobbleService] Found {pendingScrobbles.Count} pending scrobbles.");
            int successfulScrobbles = 0;

            foreach (var historyEntry in pendingScrobbles) {
                if (historyEntry.Song is null) continue;

                try {
                    bool success = await _scrobblerService.ScrobbleAsync(historyEntry.Song, historyEntry.ListenTimestampUtc);
                    if (success) {
                        historyEntry.IsScrobbled = true;
                        successfulScrobbles++;
                    }
                    else {
                        // Stop processing on the first failure to maintain chronological order.
                        Debug.WriteLine($"[OfflineScrobbleService] Scrobble failed for '{historyEntry.Song.Title}'. Will retry later.");
                        break;
                    }
                }
                catch (Exception ex) {
                    Debug.WriteLine($"[OfflineScrobbleService] Exception while scrobbling '{historyEntry.Song.Title}'. Will retry later. Error: {ex.Message}");
                    break;
                }
            }

            if (successfulScrobbles > 0) {
                await context.SaveChangesAsync();
                Debug.WriteLine($"[OfflineScrobbleService] Successfully submitted {successfulScrobbles} scrobbles.");
            }
        }
        finally {
            _queueLock.Release();
        }
    }

    /// <summary>
    /// The main loop for the background scrobbling task.
    /// </summary>
    private async Task ScrobbleLoopAsync(CancellationToken cancellationToken) {
        // Initial delay to allow the application to fully start up.
        await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);

        while (!cancellationToken.IsCancellationRequested) {
            await ProcessQueueAsync();
            await Task.Delay(_checkInterval, cancellationToken);
        }
    }

    /// <summary>
    /// Triggers a queue check when Last.fm settings are changed.
    /// </summary>
    private void OnLastFmSettingsChanged() {
        Debug.WriteLine("[OfflineScrobbleService] Last.fm settings changed. Checking for pending scrobbles.");
        _ = ProcessQueueAsync();
    }

    public void Dispose() {
        _settingsService.LastFmSettingsChanged -= OnLastFmSettingsChanged;
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
        _queueLock.Dispose();
        GC.SuppressFinalize(this);
    }
}