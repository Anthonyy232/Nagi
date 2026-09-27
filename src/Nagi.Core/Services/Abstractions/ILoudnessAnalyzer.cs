namespace Nagi.Core.Services.Abstractions;

public interface ILoudnessAnalyzer
{
    /// <returns>Integrated LUFS and linear sample peak, or null for audio below the loudness gate.</returns>
    Task<(double LoudnessLufs, double Peak)?> AnalyzeAsync(string filePath,
        CancellationToken cancellationToken = default);
}
