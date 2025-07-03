using Microsoft.UI.Xaml;
using Nagi.Services.Abstractions;
using System;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace Nagi.Helpers;

/// <summary>
/// Provides utility methods for managing window activation and state using Win32 APIs.
/// </summary>
internal static class WindowActivator {
    private const int SW_SHOWMINIMIZED = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// Brings a window to the foreground and activates it, ensuring it receives focus.
    /// </summary>
    /// <remarks>
    /// This method handles the Win32 logic required to steal focus from another application
    /// by temporarily attaching the input threads of the foreground and target windows.
    /// </remarks>
    /// <param name="window">The window to show and activate.</param>
    /// <param name="win32">A service providing Win32 interoperability functions.</param>
    public static void ShowAndActivate(Window window, IWin32InteropService win32) {
        IntPtr windowHandle = WindowNative.GetWindowHandle(window);
        if (windowHandle == IntPtr.Zero) return;

        IntPtr foregroundWindowHandle = win32.GetForegroundWindow();
        uint currentThreadId = win32.GetCurrentThreadId();
        uint foregroundThreadId = win32.GetWindowThreadProcessId(foregroundWindowHandle, IntPtr.Zero);

        // To reliably bring a window to the foreground, we attach our thread's input
        // to the foreground window's thread, which allows us to bypass certain focus restrictions.
        if (foregroundThreadId != currentThreadId) {
            win32.AttachThreadInput(foregroundThreadId, currentThreadId, true);
        }

        win32.BringWindowToTop(windowHandle);
        window.AppWindow.Show();

        // Detach the threads to restore normal input processing.
        if (foregroundThreadId != currentThreadId) {
            win32.AttachThreadInput(foregroundThreadId, currentThreadId, false);
        }
    }

    /// <summary>
    /// Activates a popup window, bringing it to the foreground.
    /// </summary>
    /// <param name="window">The window to show and activate.</param>
    public static void ActivatePopupWindow(Window window) {
        IntPtr windowHandle = WindowNative.GetWindowHandle(window);
        if (windowHandle == IntPtr.Zero) return;

        window.AppWindow.Show();
        SetForegroundWindow(windowHandle);
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