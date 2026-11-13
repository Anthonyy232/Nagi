using System.Runtime.InteropServices;
using Windows.Graphics;

namespace Nagi.WinUI.Helpers;

internal static partial class NativeMethods
{
    internal const int SW_RESTORE = 9;
    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_LAYERED = 0x00080000;
    internal const uint LWA_ALPHA = 0x00000002;
    internal const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const uint DWMWCP_ROUND = 2;
    internal const uint MB_OK = 0x00000000;
    internal const uint MB_ICONERROR = 0x00000010;

    private const double DefaultDpi = 96;

    // Segoe Fluent Icons, rounded window corners and the Windows 11 acrylic look need build 22000.
    internal static bool IsWindows11OrNewer => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>
    ///     The window's scale from device-independent to physical pixels. Read it when needed, since it changes
    ///     when the window moves to a monitor with a different DPI.
    /// </summary>
    internal static double GetDpiScale(nint hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1 : dpi / DefaultDpi;
    }

    internal static PointInt32 GetCursorPosition() =>
        GetCursorPos(out var point) ? new PointInt32(point.X, point.Y) : default;

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, nint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int MessageBox(nint hWnd, string text, string caption, uint type);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("dwmapi.dll", PreserveSig = false)]
    internal static extern void DwmSetWindowAttribute(nint hwnd, uint attribute, ref uint pvAttribute,
        uint cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
