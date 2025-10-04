using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Nagi.WinUI.Services.Abstractions;
using WinRT.Interop;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Provides utility methods for managing window activation and state using Win32 APIs.
/// </summary>
internal static class WindowActivator
{
    private const int SW_SHOWMINIMIZED = 2;
    private const int SW_RESTORE = 9;

    private static ILogger? _logger;

    private static ILogger Logger =>
        _logger ??= App.Services!.GetRequiredService<ILoggerFactory>().CreateLogger("WindowActivator");

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    ///     Brings a window to the foreground and activates it, ensuring it receives focus.
    /// </summary>
    /// <remarks>
    ///     This method handles the Win32 logic required to steal focus from another application
    ///     by temporarily attaching the input threads of the foreground and target windows.
    /// </remarks>
    /// <param name="window">The window to show and activate.</param>
    /// <param name="win32">A service providing Win32 interoperability functions.</param>
    public static void ShowAndActivate(Window window, IWin32InteropService win32)
    {
        var windowHandle = WindowNative.GetWindowHandle(window);
        if (windowHandle == IntPtr.Zero)
        {
            Logger.LogWarning("Could not get window handle for ShowAndActivate.");
            return;
        }

        Logger.LogDebug("Attempting to show and activate window with handle {WindowHandle}.", windowHandle);

        var foregroundWindowHandle = win32.GetForegroundWindow();
        var currentThreadId = win32.GetCurrentThreadId();
        var foregroundThreadId = win32.GetWindowThreadProcessId(foregroundWindowHandle, IntPtr.Zero);

        // Attach our thread's input to the foreground window's thread, which allows us to bypass focus restrictions.
        if (foregroundThreadId != currentThreadId)
        {
            Logger.LogDebug(
                "Attaching thread input from foreground thread {ForegroundThreadId} to current thread {CurrentThreadId}.",
                foregroundThreadId, currentThreadId);
            win32.AttachThreadInput(foregroundThreadId, currentThreadId, true);
        }

        win32.BringWindowToTop(windowHandle);
        // Restore the window if it was minimized and ensure it's visible.
        ShowWindow(windowHandle, SW_RESTORE);
        window.AppWindow.Show();
        SetForegroundWindow(windowHandle);

        // Detach the threads to restore normal input processing.
        if (foregroundThreadId != currentThreadId)
        {
            Logger.LogDebug(
                "Detaching thread input from foreground thread {ForegroundThreadId} to current thread {CurrentThreadId}.",
                foregroundThreadId, currentThreadId);
            win32.AttachThreadInput(foregroundThreadId, currentThreadId, false);
        }
    }

    /// <summary>
    ///     Activates a popup window, bringing it to the foreground.
    /// </summary>
    /// <param name="window">The window to show and activate.</param>
    public static void ActivatePopupWindow(Window window)
    {
        var windowHandle = WindowNative.GetWindowHandle(window);
        if (windowHandle == IntPtr.Zero)
        {
            Logger.LogWarning("Could not get window handle for ActivatePopupWindow.");
            return;
        }

        Logger.LogDebug("Activating popup window with handle {WindowHandle}.", windowHandle);
        window.AppWindow.Show();
        SetForegroundWindow(windowHandle);
    }

    /// <summary>
    ///     Shows a window in a minimized state on the taskbar.
    /// </summary>
    /// <param name="window">The window to minimize.</param>
    public static void ShowMinimized(Window window)
    {
        var windowHandle = WindowNative.GetWindowHandle(window);
        if (windowHandle == IntPtr.Zero)
        {
            Logger.LogWarning("Could not get window handle for ShowMinimized.");
            return;
        }

        Logger.LogDebug("Showing window with handle {WindowHandle} as minimized.", windowHandle);
        ShowWindow(windowHandle, SW_SHOWMINIMIZED);
    }
}