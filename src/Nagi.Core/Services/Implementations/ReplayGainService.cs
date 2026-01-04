using ATL;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Service for calculating, writing, and managing ReplayGain metadata.
///     Uses streaming PCM extraction for memory-efficient processing.
/// </summary>
public class ReplayGainService : IReplayGainService
{
    private const double ReplayGain2Reference = -18.0; // ReplayGain 2.0 uses -18 LUFS reference
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly ILogger<ReplayGainService> _logger;
    private readonly LoudnessMeter _loudnessMeter;
    private readonly IPcmExtractor _pcmExtractor;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private bool _disposed;

    public ReplayGainService(
        IDbContextFactory<MusicDbContext> contextFactory,
        IPcmExtractor pcmExtractor,
        ILogger<ReplayGainService> logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _pcmExtractor = pcmExtractor ?? throw new ArgumentNullException(nameof(pcmExtractor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loudnessMeter = new LoudnessMeter();
    }

    /// <inheritdoc />
    public async Task<(double GainDb, double Peak)?> CalculateAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            // Use streaming extraction for memory efficiency
            var hasData = false;
            LoudnessMeter.Session? session = null;

            await foreach (var chunk in _pcmExtractor.ExtractStreamingAsync(filePath, cancellationToken).ConfigureAwait(false))
            {
                // Initialize session on first chunk
                session ??= _loudnessMeter.CreateSession(chunk.SampleRate, chunk.Channels);
                
                session.ProcessChunk(chunk);
                hasData = true;
            }

            if (!hasData || session == null)
            {
                _logger.LogWarning("Failed to extract PCM samples from: {FilePath}", filePath);
                return null;
            }

            // Calculate integrated loudness using EBU R128
            var integratedLoudness = session.GetIntegratedLoudness();
            var peak = session.Peak;
            session.Dispose();
            
            if (double.IsNegativeInfinity(integratedLoudness))
            {
                _logger.LogWarning("Could not calculate loudness for: {FilePath} (possibly silent)", filePath);
                return null;
            }

            // Calculate ReplayGain: difference from reference level
            var gainDb = ReplayGain2Reference - integratedLoudness;

            _logger.LogDebug("Calculated ReplayGain for {FilePath}: LUFS={Lufs:F2}, Gain={Gain:F2} dB, Peak={Peak:F6}",
                filePath, integratedLoudness, gainDb, peak);

            return (gainDb, peak);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("ReplayGain calculation cancelled for: {FilePath}", filePath);
            return null;
        }
        catch (Exception ex)
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

        // Write tags to file using ATL.NET
        try
        {
            var track = new Track(song.FilePath);
            track.AdditionalFields["REPLAYGAIN_TRACK_GAIN"] = $"{gainDb:F2} dB";
            track.AdditionalFields["REPLAYGAIN_TRACK_PEAK"] = $"{peak:F6}";
            
            if (!track.Save())
            {
                _logger.LogError("Failed to save ReplayGain tags to file: {FilePath}", song.FilePath);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing ReplayGain tags to file: {FilePath}", song.FilePath);
            return false;
        }

        // Update database - handle concurrency and SQLite filing safely with a semaphore
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
            progress?.Report(new ScanProgress { StatusText = "All songs already have volume data.", Percentage = 100 });
            return;
        }

        _logger.LogInformation("Found {Count} songs without ReplayGain data.", totalCount);
        progress?.Report(new ScanProgress { StatusText = $"Calculating volume for {totalCount} songs...", IsIndeterminate = true });

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            CancellationToken = cancellationToken
        };

        var processed = 0;
        await Parallel.ForEachAsync(songsWithoutReplayGain, parallelOptions, async (songId, ct) =>
        {
            try
            {
                await CalculateAndWriteAsync(songId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to calculate ReplayGain for song {SongId}", songId);
            }

            var currentProcessed = Interlocked.Increment(ref processed);
            var percentage = totalCount > 0 ? (double)currentProcessed / totalCount * 100 : 100;
            progress?.Report(new ScanProgress
            {
                StatusText = $"Calculating volume normalization ({currentProcessed}/{totalCount})...",
                Percentage = percentage
            });
        }).ConfigureAwait(false);

        progress?.Report(new ScanProgress { StatusText = $"Volume normalization complete. Processed {processed} songs.", Percentage = 100 });
        _logger.LogInformation("ReplayGain scan complete. Processed {Count} songs.", processed);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _dbLock.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
