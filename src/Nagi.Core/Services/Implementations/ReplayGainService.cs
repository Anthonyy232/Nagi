using ATL;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Service for calculating, writing, and managing ReplayGain metadata.
/// </summary>
public class ReplayGainService : IReplayGainService
{
    private const double ReplayGainReference = -14.0;
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly ILogger<ReplayGainService> _logger;
    private readonly ILoudnessAnalyzer _loudnessAnalyzer;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private bool _disposed;

    public ReplayGainService(
        IDbContextFactory<MusicDbContext> contextFactory,
        ILoudnessAnalyzer loudnessAnalyzer,
        ILogger<ReplayGainService> logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _loudnessAnalyzer = loudnessAnalyzer;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<(double GainDb, double Peak)?> CalculateAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _loudnessAnalyzer.AnalyzeAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (result is null) return null;
            var (integratedLoudness, peak) = result.Value;
            var gainDb = ReplayGainReference - integratedLoudness;

            _logger.LogDebug("Calculated ReplayGain for {FilePath}: LUFS={Lufs:F2}, Gain={Gain:F2} dB, Peak={Peak:F6}",
                filePath, integratedLoudness, gainDb, peak);

            return (gainDb, peak);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error calculating ReplayGain for: {FilePath}", filePath);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> CalculateAndWriteAsync(Guid songId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var song = await context.Songs.FindAsync(new object[] { songId }, cancellationToken).ConfigureAwait(false);
        if (song == null)
        {
            // This can happen during concurrent library rescans - song may have been deleted and recreated with a new ID
            _logger.LogDebug("Song with ID {SongId} not found for ReplayGain calculation (possibly deleted during rescan).", songId);
            return false;
        }

        var result = await CalculateAsync(song.FilePath, cancellationToken).ConfigureAwait(false);
        if (result == null) return false;

        var (gainDb, peak) = result.Value;
        cancellationToken.ThrowIfCancellationRequested();

        if (!TagFileSafely(song.FilePath, gainDb, peak)) return false;

        await _dbLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            song.ReplayGainTrackGain = gainDb;
            song.ReplayGainTrackPeak = peak;

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Song was modified or deleted by another process (e.g., library rescan)
            // The file tags are already updated, so just log and continue
            _logger.LogDebug("Concurrency conflict when saving ReplayGain for song {SongId}. File tags were updated but database update skipped.", songId);
            return true;
        }
        finally
        {
            _dbLock.Release();
        }

        _logger.LogInformation("ReplayGain calculated for '{Title}': Gain={Gain:F2} dB, Peak={Peak:F6}",
            song.Title, gainDb, peak);
        return true;
    }

    /// <inheritdoc />
    public async Task ScanLibraryAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var songsWithoutReplayGain = await context.Songs
            .Where(s => s.ReplayGainTrackGain == null)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var totalCount = songsWithoutReplayGain.Count;

        if (totalCount == 0)
        {
            _logger.LogInformation("All songs already have ReplayGain data.");
            progress?.Report(new ScanProgress { StatusText = Resources.Strings.ReplayGain_AllSongsScanned, Percentage = 100 });
            return;
        }

        _logger.LogInformation("Found {Count} songs without ReplayGain data.", totalCount);
        progress?.Report(new ScanProgress { StatusText = string.Format(Resources.Strings.ReplayGain_CalculatingCount, totalCount), IsIndeterminate = true });

        // Batching strategy: Process in parallel but commit in chunks to minimize SQLite lock contention
        const int BatchSize = 50;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            CancellationToken = cancellationToken
        };

        var processed = 0;

        var songGroups = songsWithoutReplayGain.Chunk(BatchSize);

        await Parallel.ForEachAsync(songGroups, parallelOptions, async (batch, ct) =>
        {
            await using var workerContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var songs = await workerContext.Songs.Where(song => batch.Contains(song.Id)).ToListAsync(ct).ConfigureAwait(false);
            foreach (var song in songs)
            {
                var result = await CalculateAsync(song.FilePath, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (result is { } values && TagFileSafely(song.FilePath, values.GainDb, values.Peak))
                {
                    song.ReplayGainTrackGain = values.GainDb;
                    song.ReplayGainTrackPeak = values.Peak;
                }

                var currentProcessed = Interlocked.Increment(ref processed);
                progress?.Report(new ScanProgress
                {
                    StatusText = string.Format(Resources.Strings.ReplayGain_CalculatingProgress, currentProcessed, totalCount),
                    Percentage = (double)currentProcessed / totalCount * 100
                });
            }

            await _dbLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await workerContext.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _dbLock.Release();
            }
        }).ConfigureAwait(false);

        progress?.Report(new ScanProgress { StatusText = string.Format(Resources.Strings.ReplayGain_Complete, processed), Percentage = 100 });
        _logger.LogInformation("ReplayGain scan complete. Processed {Count} songs.", processed);
    }

    private bool TagFileSafely(string filePath, double gainDb, double peak)
    {
        try
        {
            var track = new Track(filePath);
            track.AdditionalFields["REPLAYGAIN_TRACK_GAIN"] = FormattableString.Invariant($"{gainDb:F2} dB");
            track.AdditionalFields["REPLAYGAIN_TRACK_PEAK"] = FormattableString.Invariant($"{peak:F6}");
            var saved = track.Save();
            if (!saved) _logger.LogError("Failed to save ReplayGain tags to file: {FilePath}", filePath);
            return saved;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing ReplayGain tags to file: {FilePath}", filePath);
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _dbLock.Dispose();
        _disposed = true;
    }
}
