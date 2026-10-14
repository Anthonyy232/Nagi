using System;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics;
using Microsoft.UI.Windowing;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

public class Win32InteropService : IWin32InteropService
{
    public Rect GetWorkAreaForPoint(PointInt32 point)
    {
        var area = DisplayArea.GetFromPoint(point, DisplayAreaFallback.Nearest).WorkArea;
        return new Rect(area.X, area.Y, area.Width, area.Height);
    }

    public PointInt32 GetCursorPos()
    {
        return NativeMethods.GetCursorPos(out var point) ? new PointInt32(point.X, point.Y) : default;
    }

    public int GetDpiForWindow(IntPtr hwnd)
    {
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        return dpi == 0 ? 96 : dpi;
    }

    public IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId) =>
        NativeMethods.GetWindowThreadProcessId(hWnd, ProcessId);

    public bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach) =>
        NativeMethods.AttachThreadInput(idAttach, idAttachTo, fAttach);

    public bool BringWindowToTop(IntPtr hWnd) => NativeMethods.BringWindowToTop(hWnd);

    public uint GetCurrentThreadId() => NativeMethods.GetCurrentThreadId();

    public bool IsWindows11OrNewer => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public int ShowMessageBox(IntPtr hWnd, string text, string caption, uint type) =>
        NativeMethods.MessageBox(hWnd, text, caption, type);

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern int GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }
    }
}
