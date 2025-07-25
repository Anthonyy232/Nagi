using Microsoft.EntityFrameworkCore;
using Nagi.Core.Data;
using Nagi.Core.Services.Abstractions;
using System.Diagnostics;

namespace Nagi.Core.Services.Implementations;

/// <summary>
/// A background service that periodically submits pending scrobbles that were not
/// successfully submitted in real-time.
/// </summary>
public class OfflineScrobbleService : IOfflineScrobbleService, IDisposable {
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly ILastFmScrobblerService _scrobblerService;
    private readonly ISettingsService _settingsService;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly SemaphoreSlim _queueLock = new(1, 1);

    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    public OfflineScrobbleService(
        IDbContextFactory<MusicDbContext> contextFactory,
        ILastFmScrobblerService scrobblerService,
        ISettingsService settingsService) {
        _contextFactory = contextFactory;
        _scrobblerService = scrobblerService;
        _settingsService = settingsService;
        _settingsService.LastFmSettingsChanged += OnLastFmSettingsChanged;
    }

    /// <inheritdoc />
    public void Start() {
        Task.Run(() => ScrobbleLoopAsync(_cancellationTokenSource.Token));
    }

    /// <inheritdoc />
    public async Task ProcessQueueAsync() {
        if (!await _settingsService.GetLastFmScrobblingEnabledAsync()) {
            Debug.WriteLine("[OfflineScrobbleService] Scrobbling is disabled; skipping queue processing.");
            return;
        }

        // Use a non-blocking wait to prevent multiple threads from processing the queue simultaneously.
        if (!await _queueLock.WaitAsync(TimeSpan.Zero)) {
            Debug.WriteLine("[OfflineScrobbleService] Queue processing is already in progress.");
            return;
        }

        try {
            Debug.WriteLine("[OfflineScrobbleService] Starting to process pending scrobbles...");
            await using var context = await _contextFactory.CreateDbContextAsync();

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
                        // Stop processing on the first failure to maintain chronological order for the next attempt.
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

    private async Task ScrobbleLoopAsync(CancellationToken cancellationToken) {
        await Task.Delay(InitialDelay, cancellationToken);

        while (!cancellationToken.IsCancellationRequested) {
            await ProcessQueueAsync();
            await Task.Delay(CheckInterval, cancellationToken);
        }
    }

    private void OnLastFmSettingsChanged() {
        Debug.WriteLine("[OfflineScrobbleService] Last.fm settings changed. Triggering queue processing.");
        // Fire-and-forget the processing task as this is an event handler.
        _ = ProcessQueueAsync();
    }

    /// <inheritdoc />
    public void Dispose() {
        _settingsService.LastFmSettingsChanged -= OnLastFmSettingsChanged;
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
        _queueLock.Dispose();
        GC.SuppressFinalize(this);
    }
}