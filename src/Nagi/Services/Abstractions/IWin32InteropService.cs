using System;
using Windows.Foundation;
using Windows.Graphics;

namespace Nagi.Services.Abstractions;

/// <summary>
/// Defines a contract for a service that provides access to Win32 API functions.
/// This abstraction allows for easier testing and isolates platform-specific code.
/// </summary>
public interface IWin32InteropService {
    /// <summary>
    /// Gets the work area of the primary display monitor.
    /// </summary>
    Rect GetPrimaryWorkArea();

    /// <summary>
    /// Gets the work area of the display monitor that contains a specified point.
    /// </summary>
    /// <param name="point">The point to check.</param>
    Rect GetWorkAreaForPoint(PointInt32 point);

    /// <summary>
    /// Gets the current position of the cursor in screen coordinates.
    /// </summary>
    PointInt32 GetCursorPos();

    /// <summary>
    /// Gets the dots per inch (DPI) for a given window.
    /// </summary>
    int GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// Retrieves the number of milliseconds that have elapsed since the system was started.
    /// </summary>
    uint GetTickCount();

    /// <summary>
    /// Retrieves a handle to the foreground window (the window with which the user is currently working).
    /// </summary>
    IntPtr GetForegroundWindow();

    /// <summary>
    /// Retrieves the identifier of the thread that created the specified window.
    /// </summary>
    uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);

    /// <summary>
    /// Attaches or detaches the input processing mechanism of one thread to that of another thread.
    /// </summary>
    bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    /// <summary>
    /// Brings the specified window to the top of the Z order.
    /// </summary>
    bool BringWindowToTop(IntPtr hWnd);

    /// <summary>
    /// Retrieves the thread identifier of the calling thread.
    /// </summary>
    uint GetCurrentThreadId();
}