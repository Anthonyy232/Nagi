using System.Diagnostics;

namespace Nagi.WinUI.Helpers;

public static class ElevationHelper
{
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
