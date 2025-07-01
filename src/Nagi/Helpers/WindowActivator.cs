using Microsoft.UI.Xaml;
using Nagi.Services.Abstractions;
using System;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace Nagi.Helpers;

/// <summary>
/// Provides utility methods for managing window activation and state.
/// </summary>
internal static class WindowActivator {
    // Win32 SW_ command to show a window minimized.
    private const int SW_SHOWMINIMIZED = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>
    /// Brings a window to the foreground and activates it, ensuring it receives focus.
    /// </summary>
    /// <remarks>
    /// This method handles the complex Win32 logic required to steal focus from another application,
    /// which involves attaching the input threads of the foreground window and the target window.
    /// </remarks>
    /// <param name="window">The window to show and activate.</param>
    /// <param name="win32">A service providing Win32 interoperability functions.</param>
    public static void ShowAndActivate(Window window, IWin32InteropService win32) {
        // Retrieve the window handle.
        IntPtr windowHandle = WindowNative.GetWindowHandle(window);
        if (windowHandle == IntPtr.Zero) {
            // Cannot activate a window without a handle.
            return;
        }

        IntPtr foregroundWindowHandle = win32.GetForegroundWindow();

        // To bring a window to the foreground, we may need to attach our thread's input
        // to the foreground window's thread. This is a common technique to steal focus.
        uint currentThreadId = win32.GetCurrentThreadId();
        uint foregroundThreadId = win32.GetWindowThreadProcessId(foregroundWindowHandle, IntPtr.Zero);

        if (foregroundThreadId != currentThreadId) {
            win32.AttachThreadInput(foregroundThreadId, currentThreadId, true);
        }

        // Place the window on top of the Z-order.
        win32.BringWindowToTop(windowHandle);

        // Show the window if it's not already visible.
        window.AppWindow.Show();

        // Detach the threads to restore normal input processing.
        if (foregroundThreadId != currentThreadId) {
            win32.AttachThreadInput(foregroundThreadId, currentThreadId, false);
        }
    }

    /// <summary>
    /// Shows a window in a minimized state on the taskbar.
    /// </summary>
    /// <param name="window">The window to minimize.</param>
    public static void ShowMinimized(Window window) {
        IntPtr windowHandle = WindowNative.GetWindowHandle(window);
        if (windowHandle != IntPtr.Zero) {
            ShowWindow(windowHandle, SW_SHOWMINIMIZED);
        }
    }
}