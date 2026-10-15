namespace Nagi.Core.Services.Abstractions;

using Nagi.Core.Services.Data;

public interface IReplayGainService : IDisposable
{
    /// <summary>
    ///     Calculates the ReplayGain track gain and peak for an audio file.
    /// </summary>
    /// <returns>A tuple of (GainDb, Peak), or null if calculation failed.</returns>
    Task<(double GainDb, double Peak)?> CalculateAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Scans the library for songs missing ReplayGain data and calculates/writes it. Only one scan runs at a
    ///     time: a call made during a scan returns immediately, and the running scan does one more round for it.
    /// </summary>
    /// <param name="progress">Optional progress reporter with status text and percentage.</param>
    Task ScanLibraryAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);
}
