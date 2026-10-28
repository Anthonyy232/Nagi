using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Presentation.Services.Abstractions;
using Windows.ApplicationModel;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Toggles the MSIX startup task declared in Package.appxmanifest.
/// </summary>
public class StartupTaskAutoLaunchService(ILogger<StartupTaskAutoLaunchService> logger) : IAutoLaunchService
{
    private const string StartupTaskId = "NagiAutolaunchStartup";
    private const int NoMutableDirectoryHResult = unchecked((int)0x80073D5B);

    public async Task<bool> IsEnabledAsync()
    {
        try
        {
            var startupTask = await StartupTask.GetAsync(StartupTaskId).AsTask().ConfigureAwait(false);
            return startupTask.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        catch (Exception ex)
        {
            if (ex.HResult == NoMutableDirectoryHResult)
            {
                logger.LogDebug("Auto-launch task check skipped: The package does not have a mutable directory.");
            }
            else
            {
                logger.LogWarning(ex, "Failed to get startup task state.");
            }
            return false;
        }
    }

    public async Task SetEnabledAsync(bool isEnabled)
    {
        var startupTask = await StartupTask.GetAsync(StartupTaskId).AsTask().ConfigureAwait(false);
        if (isEnabled)
        {
            var state = await startupTask.RequestEnableAsync().AsTask().ConfigureAwait(false);
            if (state is not StartupTaskState.Enabled and not StartupTaskState.EnabledByPolicy)
                logger.LogWarning(
                    "StartupTask enable request did not result in 'Enabled' state. Current state: {State}", state);
        }
        else
        {
            startupTask.Disable();
        }
    }
}
