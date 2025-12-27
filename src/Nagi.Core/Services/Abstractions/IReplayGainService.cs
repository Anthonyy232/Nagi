namespace Nagi.Core.Services.Abstractions;

using Nagi.Core.Services.Data;

/// <summary>
///     Defines a service for calculating and managing ReplayGain values for audio normalization.
/// </summary>
public interface IReplayGainService
{
    /// <summary>
    ///     Calculates the ReplayGain track gain and peak for an audio file.
    /// </summary>
    /// <param name="filePath">The path to the audio file.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A tuple of (GainDb, Peak), or null if calculation failed.</returns>
    Task<(double GainDb, double Peak)?> CalculateAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Calculates ReplayGain for a song, writes the tags to the file, and updates the database.
    /// </summary>
    /// <param name="songId">The ID of the song to process.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>True if successful; otherwise, false.</returns>
    Task<bool> CalculateAndWriteAsync(Guid songId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Scans the library for songs missing ReplayGain data and calculates/writes it.
    /// </summary>
    /// <param name="progress">Optional progress reporter with status text and percentage.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task ScanLibraryAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);
}

