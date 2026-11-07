using Nagi.Core.Library;

namespace Nagi.Core.Playback;

public interface IReplayGainService : IDisposable
{
    /// <summary>
    ///     Scans the library for songs missing ReplayGain data and calculates/writes it. Only one scan runs at a
    ///     time: a call made during a scan returns immediately, and the running scan does one more round for it.
    /// </summary>
    Task ScanLibraryAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default);
}
