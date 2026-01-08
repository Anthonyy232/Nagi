using System;
using Windows.Foundation;
using Windows.Graphics;
using Microsoft.UI.Xaml;

namespace Nagi.WinUI.Services.Abstractions;

/// <summary>
///     Defines a contract for a service that provides access to Win32 API functions.
///     This abstraction isolates platform-specific code and allows for easier testing.
/// </summary>
public interface IWin32InteropService
{
    /// <summary>
    ///     Sets the icon for a given WinUI 3 window, used in the title bar and Alt+Tab switcher.
    /// </summary>
    /// <param name="window">The window to set the icon for.</param>
    /// <param name="iconPath">The relative path to the .ico file.</param>
    void SetWindowIcon(Window window, string iconPath);

    /// <summary>
    ///     Gets the work area of the primary display monitor, excluding the taskbar.
    /// </summary>
    /// <returns>A Rect representing the work area.</returns>
    Rect GetPrimaryWorkArea();

    /// <summary>
    ///     Gets the work area of the display monitor that contains a specified point.
    /// </summary>
    /// <param name="point">The point to check, in screen coordinates.</param>
    /// <returns>A Rect representing the work area of the containing monitor.</returns>
    Rect GetWorkAreaForPoint(PointInt32 point);

    /// <summary>
    ///     Gets the current position of the cursor in screen coordinates.
    /// </summary>
    /// <returns>The cursor's screen coordinates.</returns>
    PointInt32 GetCursorPos();

    /// <summary>
    ///     Gets the dots per inch (DPI) for a given window.
    /// </summary>
    /// <param name="hwnd">The handle of the window.</param>
    /// <returns>The DPI value of the window.</returns>
    int GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    ///     Retrieves the number of milliseconds that have elapsed since the system was started.
    /// </summary>
    /// <returns>The system uptime in milliseconds.</returns>
    uint GetTickCount();

    /// <summary>
    ///     Retrieves a handle to the foreground window (the window with which the user is currently working).
    /// </summary>
    /// <returns>A handle to the foreground window.</returns>
    IntPtr GetForegroundWindow();

    /// <summary>
    ///     Retrieves the identifier of the thread that created the specified window.
    /// </summary>
    /// <param name="hWnd">A handle to the window.</param>
    /// <param name="ProcessId">A pointer to a variable that receives the process identifier (not used here).</param>
    /// <returns>The thread identifier.</returns>
    uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);

    /// <summary>
    ///     Attaches or detaches the input processing mechanism of one thread to that of another thread.
    /// </summary>
    /// <param name="idAttach">The identifier of the thread to be attached.</param>
    /// <param name="idAttachTo">The identifier of the thread to which idAttach will be attached.</param>
    /// <param name="fAttach">If true, the threads are attached. If false, they are detached.</param>
    /// <returns>True if the function succeeds; otherwise, false.</returns>
    bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    /// <summary>
    ///     Brings the specified window to the top of the Z order.
    /// </summary>
    /// <param name="hWnd">A handle to the window to bring to the top.</param>
    /// <returns>True if the function succeeds; otherwise, false.</returns>
    bool BringWindowToTop(IntPtr hWnd);

    /// <summary>
    ///     Retrieves the thread identifier of the calling thread.
    /// </summary>
    /// <returns>The thread identifier.</returns>
    uint GetCurrentThreadId();

    /// <summary>
    ///     Gets a value indicating whether the current operating system is Windows 11 or newer.
    /// </summary>
    bool IsWindows11OrNewer { get; }

    /// <summary>
    ///    Shows a standard Win32 message box.
    /// </summary>
    /// <param name="hWnd">The handle to the owner window.</param>
    /// <param name="text">The text to display.</param>
    /// <param name="caption">The caption of the message box.</param>
    /// <param name="type">The type of the message box.</param>
    /// <returns>the result of the message box call.</returns>
    int ShowMessageBox(IntPtr hWnd, string text, string caption, uint type);
}
