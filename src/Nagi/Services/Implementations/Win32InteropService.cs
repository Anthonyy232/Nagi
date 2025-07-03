using Nagi.Services.Abstractions;
using System;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics;

namespace Nagi.Services.Implementations;

/// <summary>
/// Implements IWin32InteropService by calling native Win32 APIs.
/// </summary>
public class Win32InteropService : IWin32InteropService {
    public Rect GetPrimaryWorkArea() {
        IntPtr rectPtr = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());
        try {
            if (SystemParametersInfo(SPI_GETWORKAREA, 0, rectPtr, 0)) {
                var rect = Marshal.PtrToStructure<RECT>(rectPtr);
                return new Rect(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
            }
            // Fallback to a common resolution if the API call fails.
            return new Rect(0, 0, 1920, 1080);
        }
        finally {
            Marshal.FreeHGlobal(rectPtr);
        }
    }

    public PointInt32 GetCursorPos() {
        if (GetCursorPos_Private(out POINT point)) {
            return new PointInt32(point.X, point.Y);
        }
        return new PointInt32(0, 0);
    }

    public Rect GetWorkAreaForPoint(PointInt32 point) {
        var monitorHandle = MonitorFromPoint(new POINT { X = point.X, Y = point.Y }, MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new MONITORINFO();
        if (GetMonitorInfo(monitorHandle, monitorInfo)) {
            var workRect = monitorInfo.rcWork;
            return new Rect(workRect.left, workRect.top, workRect.right - workRect.left, workRect.bottom - workRect.top);
        }

        // Fallback to primary work area if monitor info fails.
        return GetPrimaryWorkArea();
    }

    public int GetDpiForWindow(IntPtr hwnd) => GetDpiForWindow_Private(hwnd);
    public uint GetTickCount() => GetTickCount_Private();
    public IntPtr GetForegroundWindow() => GetForegroundWindow_Private();
    public uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId) => GetWindowThreadProcessId_Private(hWnd, ProcessId);
    public bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach) => AttachThreadInput_Private(idAttach, idAttachTo, fAttach);
    public bool BringWindowToTop(IntPtr hWnd) => BringWindowToTop_Private(hWnd);
    public uint GetCurrentThreadId() => GetCurrentThreadId_Private();

    #region P/Invoke Definitions

    private const int SPI_GETWORKAREA = 0x0030;
    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MONITORINFO {
        public int cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        public RECT rcMonitor = new();
        public RECT rcWork = new();
        public int dwFlags = 0;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static extern uint GetCurrentThreadId_Private();

    [DllImport("kernel32.dll", EntryPoint = "GetTickCount")]
    private static extern uint GetTickCount_Private();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetDpiForWindow")]
    private static extern int GetDpiForWindow_Private(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos_Private(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, MONITORINFO lpmi);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindow_Private();

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GetWindowThreadProcessId_Private(IntPtr hWnd, IntPtr ProcessId);

    [DllImport("user32.dll", EntryPoint = "AttachThreadInput")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput_Private(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll", EntryPoint = "BringWindowToTop")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop_Private(IntPtr hWnd);

    #endregion
}