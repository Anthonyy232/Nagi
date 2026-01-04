namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Represents a chunk of PCM audio samples for streaming extraction.
/// </summary>
/// <param name="Samples">Interleaved float samples normalized to [-1, 1].</param>
/// <param name="SampleRate">Sample rate in Hz.</param>
/// <param name="Channels">Number of audio channels.</param>
public readonly record struct AudioChunk(float[] Samples, int SampleRate, int Channels);

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

    /// <summary>
    ///     Extracts PCM samples from an audio file in a streaming fashion.
    ///     This method yields chunks of audio data as they are decoded, reducing memory usage.
    /// </summary>
    /// <param name="filePath">Path to the audio file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of audio chunks.</returns>
    IAsyncEnumerable<AudioChunk> ExtractStreamingAsync(
        string filePath,
        CancellationToken cancellationToken = default);
}
