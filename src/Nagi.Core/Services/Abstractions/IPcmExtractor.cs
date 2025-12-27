namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines a service for extracting raw PCM audio samples from audio files.
///     Used for ReplayGain calculation.
/// </summary>
public interface IPcmExtractor
{
    /// <summary>
    ///     Extracts PCM samples from an audio file.
    /// </summary>
    /// <param name="filePath">Path to the audio file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    ///     Tuple containing interleaved float samples (normalized to [-1, 1]), sample rate, and channel count.
    ///     Returns null if extraction failed.
    /// </returns>
    Task<(float[] Samples, int SampleRate, int Channels)?> ExtractAsync(
        string filePath,
        CancellationToken cancellationToken = default);
}
