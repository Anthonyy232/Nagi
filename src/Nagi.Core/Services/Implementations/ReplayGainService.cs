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

        // Batching strategy: Process in parallel but commit in chunks to minimize SQLite lock contention
        const int BatchSize = 50;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            CancellationToken = cancellationToken
        };

        var processed = 0;
        
        // Use Chunks to allow batching within each parallel worker
        var songGroups = songsWithoutReplayGain.Chunk(BatchSize);

        await Parallel.ForEachAsync(songGroups, parallelOptions, async (batch, ct) =>
        {
            // Each parallel worker gets its own DB context and recycled session
            await using var workerContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            LoudnessMeter.Session? recycledSession = null;

            try
            {
                foreach (var songId in batch)
                {
                    var song = await workerContext.Songs.FindAsync(new object[] { songId }, ct).ConfigureAwait(false);
                    if (song == null) continue;

                    try 
                    {
                        // Calculate - ensure we capture the session whether success or failure
                        var calcResult = await CalculateStreamingInternalAsync(song.FilePath, recycledSession, ct).ConfigureAwait(false);
                        recycledSession = calcResult.SessionBuffer; // Always update the reference

                        if (calcResult.GainDb.HasValue && calcResult.Peak.HasValue)
                        {
                            var gainDb = calcResult.GainDb.Value;
                            var peak = calcResult.Peak.Value;

                            // Tag file
                            if (TagFileSafely(song.FilePath, gainDb, peak))
                            {
                                // Stage for DB (No SaveChanges yet)
                                song.ReplayGainTrackGain = gainDb;
                                song.ReplayGainTrackPeak = peak;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to process song {SongId}", songId);
                    }

                    // Granular progress reporting
                    var currentProcessed = Interlocked.Increment(ref processed);
                    var percentage = totalCount > 0 ? (double)currentProcessed / totalCount * 100 : 100;
                    progress?.Report(new ScanProgress
                    {
                        StatusText = $"Calculating volume normalization ({currentProcessed}/{totalCount})...",
                        Percentage = percentage
                    });
                }

                // Batch Save to DB
                await _dbLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await workerContext.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    _dbLock.Release();
                }
            }
            finally
            {
                recycledSession?.Dispose();
            }
        }).ConfigureAwait(false);

        progress?.Report(new ScanProgress { StatusText = $"Volume normalization complete. Processed {processed} songs.", Percentage = 100 });
        _logger.LogInformation("ReplayGain scan complete. Processed {Count} songs.", processed);
    }

    private record InternalCalculationResult(double? GainDb, double? Peak, LoudnessMeter.Session? SessionBuffer);

    private async Task<InternalCalculationResult> CalculateStreamingInternalAsync(
        string filePath, 
        LoudnessMeter.Session? existingSession,
        CancellationToken ct)
    {
        var hasData = false;
        var session = existingSession;
        var initialized = false;

        try
        {
            await foreach (var chunk in _pcmExtractor.ExtractStreamingAsync(filePath, ct).ConfigureAwait(false))
            {
                if (!initialized)
                {
                    // Validate if existing session matches current audio properties
                    if (session == null || session.SampleRate != chunk.SampleRate || session.Channels != chunk.Channels)
                    {
                        session?.Dispose();
                        session = _loudnessMeter.CreateSession(chunk.SampleRate, chunk.Channels);
                    }
                    else
                    {
                        session.Reset();
                    }
                    initialized = true;
                }
                
                // Use ! because we know it's initialized after the block above
                session!.ProcessChunk(chunk);
                hasData = true;
            }

            if (!hasData || session == null) 
                return new InternalCalculationResult(null, null, session);

            var integratedLoudness = session.GetIntegratedLoudness();
            if (double.IsNegativeInfinity(integratedLoudness)) 
                return new InternalCalculationResult(null, null, session);

            var gainDb = ReplayGain2Reference - integratedLoudness;
            return new InternalCalculationResult(gainDb, session.Peak, session);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error during streaming calculation for {FilePath}", filePath);
            return new InternalCalculationResult(null, null, session);
        }
    }

    private bool TagFileSafely(string filePath, double gainDb, double peak)
    {
        try
        {
            var track = new Track(filePath);
            track.AdditionalFields["REPLAYGAIN_TRACK_GAIN"] = $"{gainDb:F2} dB";
            track.AdditionalFields["REPLAYGAIN_TRACK_PEAK"] = $"{peak:F6}";
            return track.Save();
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
        GC.SuppressFinalize(this);
    }
}
