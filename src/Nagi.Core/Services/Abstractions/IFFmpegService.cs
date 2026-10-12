using System.Threading.Tasks;

namespace Nagi.Core.Services.Abstractions;

public interface IFFmpegService
{
    /// <summary>
    ///     Checks if FFmpeg is installed and available in the system PATH.
    /// </summary>
    /// <param name="forceRecheck">If true, bypasses any cached result and checks again.</param>
    Task<bool> IsFFmpegInstalledAsync(bool forceRecheck = false);

    string GetFFmpegSetupInstructions();
}
