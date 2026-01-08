using System;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Nagi.WinUI.Services.Abstractions;
using WinRT.Interop;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Provides a managed wrapper for native Win32 API functions, offering a safe
///     and convenient interface to the underlying Windows platform.
/// </summary>
public class Win32InteropService : IWin32InteropService, IDisposable
{
    private readonly ILogger<Win32InteropService> _logger;
    private IntPtr _hIconBig = IntPtr.Zero;
    private IntPtr _hIconSmall = IntPtr.Zero;
    private bool _isDisposed;

    public Win32InteropService(ILogger<Win32InteropService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     Sets the large and small icons for a window from a specified icon file.
    /// </summary>
    /// <param name="window">The window whose icon is to be set.</param>
    /// <param name="iconPath">The application-relative path to the .ico file.</param>
    public void SetWindowIcon(Window window, string iconPath)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero) return;

        var fullIconPath = Path.Combine(AppContext.BaseDirectory, iconPath);
        if (!File.Exists(fullIconPath))
        {
            _logger.LogWarning("Icon file not found: {IconPath}", fullIconPath);
            return;
        }

        // Clean up any previously loaded icons to prevent leaks.
        CleanupIcons();

        // Set the large icon (e.g., for Alt+Tab).
        _hIconBig = NativeMethods.LoadImage(IntPtr.Zero, fullIconPath, NativeMethods.IMAGE_ICON, 0, 0,
            NativeMethods.LR_LOADFROMFILE);
        if (_hIconBig != IntPtr.Zero)
            NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETICON, NativeMethods.ICON_BIG, _hIconBig);

        // Set the small icon (e.g., for the title bar).
        _hIconSmall = NativeMethods.LoadImage(IntPtr.Zero, fullIconPath, NativeMethods.IMAGE_ICON, 16, 16,
            NativeMethods.LR_LOADFROMFILE);
        if (_hIconSmall != IntPtr.Zero)
            NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETICON, NativeMethods.ICON_SMALL, _hIconSmall);
    }

    private void CleanupIcons()
    {
        if (_hIconBig != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_hIconBig);
            _hIconBig = IntPtr.Zero;
        }

        if (_hIconSmall != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_hIconSmall);
            _hIconSmall = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        CleanupIcons();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Retrieves the work area of the primary display monitor.
    /// </summary>
    /// <returns>A Rect representing the primary work area.</returns>
    public Rect GetPrimaryWorkArea()
    {
        var workAreaRect = new NativeMethods.RECT();
        if (NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, ref workAreaRect, 0))
            return new Rect(workAreaRect.left, workAreaRect.top, workAreaRect.right - workAreaRect.left,
                workAreaRect.bottom - workAreaRect.top);

        // Fallback to a default resolution if the API call fails.
        return new Rect(0, 0, 1920, 1080);
    }

    /// <summary>
    ///     Retrieves the current position of the cursor in screen coordinates.
    /// </summary>
    /// <returns>A PointInt32 with the cursor's X and Y coordinates.</returns>
    public PointInt32 GetCursorPos()
    {
        if (NativeMethods.GetCursorPos(out var point)) return new PointInt32(point.X, point.Y);
        return new PointInt32(0, 0);
    }

    /// <summary>
    ///     Retrieves the work area of the display monitor that contains the specified point.
    /// </summary>
    /// <param name="point">The point in screen coordinates to check.</param>
    /// <returns>A Rect representing the work area of the containing monitor.</returns>
    public Rect GetWorkAreaForPoint(PointInt32 point)
    {
        var nativePoint = new NativeMethods.POINT { X = point.X, Y = point.Y };
        var monitorHandle = NativeMethods.MonitorFromPoint(nativePoint, NativeMethods.MONITOR_DEFAULTTONEAREST);

        var monitorInfo = new NativeMethods.MONITORINFO();
        monitorInfo.cbSize = Marshal.SizeOf(monitorInfo);

        if (NativeMethods.GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            var workRect = monitorInfo.rcWork;
            return new Rect(workRect.left, workRect.top, workRect.right - workRect.left,
                workRect.bottom - workRect.top);
        }

        // Fallback to the primary monitor's work area if the specific monitor can't be determined.
        return GetPrimaryWorkArea();
    }

    /// <summary>
    ///     Gets the Dots Per Inch (DPI) for the specified window.
    ///     This method includes a fallback for older versions of Windows that do not support GetDpiForWindow.
    /// </summary>
    /// <param name="hwnd">The handle to the window.</param>
    /// <returns>The DPI value of the window, or 96 if it cannot be determined.</returns>
    public int GetDpiForWindow(IntPtr hwnd)
    {
        try
        {
            // This is the modern API, available on Windows 10 (1607) and newer.
            return NativeMethods.GetDpiForWindow(hwnd);
        }
        catch (EntryPointNotFoundException)
        {
            // Fallback for older OS versions.
            _logger.LogDebug("GetDpiForWindow not found. Falling back to GetDeviceCaps for older OS.");
            var hdc = NativeMethods.GetDC(hwnd);
            if (hdc != IntPtr.Zero)
                try
                {
                    // Get the horizontal DPI.
                    return NativeMethods.GetDeviceCaps(hdc, NativeMethods.LOGPIXELSX);
                }
                finally
                {
                    // Important: Always release the device context.
                    NativeMethods.ReleaseDC(hwnd, hdc);
                }
        }

        // If all else fails, return the standard default DPI.
        return 96;
    }

    /// <inheritdoc />
    public uint GetTickCount()
    {
        return NativeMethods.GetTickCount();
    }

    /// <inheritdoc />
    public IntPtr GetForegroundWindow()
    {
        return NativeMethods.GetForegroundWindow();
    }

    /// <inheritdoc />
    public uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId)
    {
        return NativeMethods.GetWindowThreadProcessId(hWnd, ProcessId);
    }

    /// <inheritdoc />
    public bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach)
    {
        return NativeMethods.AttachThreadInput(idAttach, idAttachTo, fAttach);
    }

    /// <inheritdoc />
    public bool BringWindowToTop(IntPtr hWnd)
    {
        return NativeMethods.BringWindowToTop(hWnd);
    }

    /// <inheritdoc />
    public uint GetCurrentThreadId()
    {
        return NativeMethods.GetCurrentThreadId();
    }

    /// <inheritdoc />
    public int ShowMessageBox(IntPtr hWnd, string text, string caption, uint type)
    {
        return NativeMethods.MessageBox(hWnd, text, caption, type);
    }

    /// <summary>
    ///     Contains P/Invoke definitions for native Win32 API calls used by this service.
    /// </summary>
    private static class NativeMethods
    {
        // Constants for SystemParametersInfo
        public const uint SPI_GETWORKAREA = 0x0030;

        // Constants for MonitorFromPoint
        public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        // Constants for LoadImage
        public const uint IMAGE_ICON = 1;
        public const uint LR_LOADFROMFILE = 0x0010;

        // Constants for SendMessage (WM_SETICON)
        public const uint WM_SETICON = 0x0080;
        public const int ICON_SMALL = 0;
        public const int ICON_BIG = 1;

        // Constants for GetDeviceCaps
        public const int LOGPIXELSX = 88; // Logical pixels/inch in X

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired,
            uint fuLoad);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll")]
        public static extern uint GetTickCount();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo,
            [MarshalAs(UnmanagedType.Bool)] bool fAttach);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        [DllImport("gdi32.dll")]
        public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }
    }
}