using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.Helpers;

/// <summary>
///     Runs something the user asked for, such as starting playback, and tells them if it failed.
/// </summary>
internal static class UserAction
{
    /// <param name="isClosed">Whether the view went away, so no dialog should appear for it.</param>
    public static async Task RunAsync(Func<Task> action, ILogger logger, IUIService uiService, Func<bool>? isClosed = null)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Action failed");
            if (isClosed?.Invoke() != true) await uiService.ShowMessageDialogAsync(Strings.Generic_Error, Strings.Listening_ActionError);
        }
    }
}
