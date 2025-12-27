using System.Threading.Tasks;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines a service for checking FFmpeg availability and providing setup instructions.
/// </summary>
public interface IFFmpegService
{
    /// <summary>
    ///     Checks if FFmpeg is installed and available in the system PATH.
    /// </summary>
    /// <param name="forceRecheck">If true, bypasses any cached result and checks again.</param>
    /// <returns>True if FFmpeg is available; otherwise, false.</returns>
    Task<bool> IsFFmpegInstalledAsync(bool forceRecheck = false);

    /// <summary>
    ///     Returns instructions for the user on how to install and set up FFmpeg.
    /// </summary>
    string GetFFmpegSetupInstructions();
}
