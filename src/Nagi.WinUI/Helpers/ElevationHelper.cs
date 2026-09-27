using System;
using System.Diagnostics;
using System.Security.Principal;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Provides helper methods for detecting and handling Windows process elevation (administrator mode).
/// </summary>
public static class ElevationHelper
{
    /// <summary>
    ///     Checks if the current process is running with elevated (administrator) privileges.
    /// </summary>
    /// <returns>True if running as administrator, false otherwise.</returns>
    public static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    ///     Restarts the application without administrator elevation by using explorer.exe as a shell bridge.
    ///     This works because explorer.exe runs at medium integrity level (non-elevated),
    ///     so any process it launches will also be non-elevated.
    /// </summary>
    public static void RestartWithoutElevation()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return;

        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            ArgumentList = { exePath },
            UseShellExecute = false
        };

        Process.Start(startInfo);
        Environment.Exit(0);
    }
}
