using Windows.Foundation;
using Windows.Graphics;

namespace Nagi.WinUI.Services.Abstractions;

public interface IWin32InteropService
{
    Rect GetWorkAreaForPoint(PointInt32 point);

    PointInt32 GetCursorPos();

    int GetDpiForWindow(IntPtr hwnd);

    IntPtr GetForegroundWindow();

    uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);

    bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    bool BringWindowToTop(IntPtr hWnd);

    uint GetCurrentThreadId();

    bool IsWindows11OrNewer { get; }

    int ShowMessageBox(IntPtr hWnd, string text, string caption, uint type);
}
