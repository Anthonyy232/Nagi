using Microsoft.UI.Xaml;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics;
using Nagi.WinUI.Services.Abstractions;
using WinRT.Interop;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
/// Implements IWin32InteropService by calling native Win32 APIs.
/// This service acts as a wrapper to provide a safe and managed interface
/// to the underlying Windows platform functions.
/// </summary>
public class Win32InteropService : IWin32InteropService {
    /// <summary>
    /// Sets the icon for a given window handle.
    /// </summary>
    /// <param name="window">The window to set the icon for.</param>
    /// <param name="iconPath">The relative path to the icon file.</param>
    public void SetWindowIcon(Window window, string iconPath) {
        IntPtr hwnd = WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero) return;

        string fullIconPath = Path.Combine(AppContext.BaseDirectory, iconPath);
        if (!File.Exists(fullIconPath)) {
            Debug.WriteLine($"[Win32InteropService] Icon file not found: {fullIconPath}");
            return;
        }

        // Set the large icon (e.g., for Alt+Tab).
        IntPtr hIconBig = NativeMethods.LoadImage(IntPtr.Zero, fullIconPath, NativeMethods.IMAGE_ICON, 0, 0, NativeMethods.LR_LOADFROMFILE);
        if (hIconBig != IntPtr.Zero) {
            NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETICON, (IntPtr)NativeMethods.ICON_BIG, hIconBig);
        }

        // Set the small icon (e.g., for the title bar).
        IntPtr hIconSmall = NativeMethods.LoadImage(IntPtr.Zero, fullIconPath, NativeMethods.IMAGE_ICON, 16, 16, NativeMethods.LR_LOADFROMFILE);
        if (hIconSmall != IntPtr.Zero) {
            NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETICON, (IntPtr)NativeMethods.ICON_SMALL, hIconSmall);
        }
    }

    /// <summary>
    /// Gets the work area of the primary display monitor.
    /// The work area is the portion of the screen not obscured by the system taskbar.
    /// </summary>
    /// <returns>A Rect representing the primary work area.</returns>
    public Rect GetPrimaryWorkArea() {
        IntPtr rectPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.RECT>());
        try {
            if (NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, rectPtr, 0)) {
                var rect = Marshal.PtrToStructure<NativeMethods.RECT>(rectPtr);
                return new Rect(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
            }
            // Fallback to a default resolution if the API call fails.
            return new Rect(0, 0, 1920, 1080);
        }
        finally {
            Marshal.FreeHGlobal(rectPtr);
        }
    }

    /// <summary>
    /// Retrieves the current position of the cursor on the screen.
    /// </summary>
    /// <returns>A PointInt32 representing the cursor's coordinates.</returns>
    public PointInt32 GetCursorPos() {
        if (NativeMethods.GetCursorPos(out NativeMethods.POINT point)) {
            return new PointInt32(point.X, point.Y);
        }
        return new PointInt32(0, 0);
    }

    /// <summary>
    /// Gets the work area of the monitor that contains the specified point.
    /// </summary>
    /// <param name="point">The point to check.</param>
    /// <returns>A Rect representing the work area of the relevant monitor.</returns>
    public Rect GetWorkAreaForPoint(PointInt32 point) {
        var monitorHandle = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = point.X, Y = point.Y }, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new NativeMethods.MONITORINFO();
        if (NativeMethods.GetMonitorInfo(monitorHandle, monitorInfo)) {
            var workRect = monitorInfo.rcWork;
            return new Rect(workRect.left, workRect.top, workRect.right - workRect.left, workRect.bottom - workRect.top);
        }

        // Fallback to the primary monitor's work area if the specific monitor can't be determined.
        return GetPrimaryWorkArea();
    }

    public int GetDpiForWindow(IntPtr hwnd) => NativeMethods.GetDpiForWindow(hwnd);
    public uint GetTickCount() => NativeMethods.GetTickCount();
    public IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();
    public uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId) => NativeMethods.GetWindowThreadProcessId(hWnd, ProcessId);
    public bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach) => NativeMethods.AttachThreadInput(idAttach, idAttachTo, fAttach);
    public bool BringWindowToTop(IntPtr hWnd) => NativeMethods.BringWindowToTop(hWnd);
    public uint GetCurrentThreadId() => NativeMethods.GetCurrentThreadId();

    /// <summary>
    /// Contains P/Invoke definitions for native Win32 API calls.
    /// </summary>
    private static class NativeMethods {
        public const uint SPI_GETWORKAREA = 0x0030;
        public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        public const uint IMAGE_ICON = 1;
        public const uint LR_LOADFROMFILE = 0x0010;
        public const uint WM_SETICON = 0x0080;
        public const int ICON_SMALL = 0;
        public const int ICON_BIG = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left; public int top; public int right; public int bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public class MONITORINFO {
            public int cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            public RECT rcMonitor = new();
            public RECT rcWork = new();
            public int dwFlags = 0;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll")]
        public static extern uint GetTickCount();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, MONITORINFO lpmi);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BringWindowToTop(IntPtr hWnd);
    }
}