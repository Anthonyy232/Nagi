using ATL;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

public class ReplayGainService : IReplayGainService
{
    private const double ReplayGainReference = -18.0;
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly ILogger<ReplayGainService> _logger;
    private readonly ILoudnessAnalyzer _loudnessAnalyzer;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private readonly object _passLock = new();
    private bool _isPassRunning;
    private bool _isAnotherRoundRequested;
    private bool _disposed;

    public ReplayGainService(
        IDbContextFactory<MusicDbContext> contextFactory,
        ILoudnessAnalyzer loudnessAnalyzer,
        ILogger<ReplayGainService> logger)
    {
        _contextFactory = contextFactory;
        _loudnessAnalyzer = loudnessAnalyzer;
        _logger = logger;
    }

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

    public async Task ScanLibraryAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // Passes write tags into the audio files, so two must never run at once. A caller arriving during a pass
        // returns at once and has that pass run one more round, which only picks up songs still missing gain.
        lock (_passLock)
        {
            if (_isPassRunning)
            {
                _logger.LogDebug("ReplayGain scan already running. Queuing one more round.");
                _isAnotherRoundRequested = true;
                return;
            }

            _isPassRunning = true;
        }

        try
        {
            do await ScanSongsWithoutReplayGainAsync(progress, cancellationToken).ConfigureAwait(false);
            while (TakeAnotherRoundRequest());
        }
        catch
        {
            // A cancelled or failed pass drops queued rounds; the next library scan picks those songs up.
            lock (_passLock) _isPassRunning = _isAnotherRoundRequested = false;
            throw;
        }
    }

    // Ends the pass in the same lock that callers check, so a request cannot slip in between.
    private bool TakeAnotherRoundRequest()
    {
        lock (_passLock)
        {
            if (_isAnotherRoundRequested)
            {
                _isAnotherRoundRequested = false;
                return true;
            }

            _isPassRunning = false;
            return false;
        }
    }

    private async Task ScanSongsWithoutReplayGainAsync(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
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
                if (result is { } values)
                {
                    // The player normalizes from the database, so a file whose tags can't be written (read-only, say)
                    // still gets its gain, and isn't analyzed again on every scan.
                    TagFileSafely(song.FilePath, values.GainDb, values.Peak);
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

    private void TagFileSafely(string filePath, double gainDb, double peak)
    {
        try
        {
            var track = new Track(filePath);
            track.AdditionalFields["REPLAYGAIN_TRACK_GAIN"] = FormattableString.Invariant($"{gainDb:F2} dB");
            track.AdditionalFields["REPLAYGAIN_TRACK_PEAK"] = FormattableString.Invariant($"{peak:F6}");
            if (!track.Save()) _logger.LogError("Failed to save ReplayGain tags to file: {FilePath}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing ReplayGain tags to file: {FilePath}", filePath);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _dbLock.Dispose();
        _disposed = true;
    }
}
