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
    ///     Scans the library for songs missing ReplayGain data and calculates/writes it.
    /// </summary>
    /// <param name="progress">Optional progress reporter with status text and percentage.</param>
    Task ScanLibraryAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);
}
