using System.Runtime.InteropServices;
using Windows.System;

namespace Nagi.Helpers;

/// <summary>
///     Provides a helper method to ensure a dispatcher queue exists on the current thread.
///     This is a prerequisite for using Mica or Acrylic system backdrops in a WinUI 3 application.
/// </summary>
internal class WindowsSystemDispatcherQueueHelper
{
    private object? _dispatcherQueueController;

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController([In] DispatcherQueueOptions options,
        [In] [Out] [MarshalAs(UnmanagedType.IUnknown)]
        ref object? dispatcherQueueController);

    public void EnsureWindowsSystemDispatcherQueueController()
    {
        if (DispatcherQueue.GetForCurrentThread() != null)
            // A dispatcher queue already exists for this thread.
            return;

        if (_dispatcherQueueController == null)
        {
            DispatcherQueueOptions options;
            options.dwSize = Marshal.SizeOf(typeof(DispatcherQueueOptions));
            options.threadType = 2; // DQTYPE_THREAD_CURRENT
            options.apartmentType = 2; // DQTAT_COM_STA

            CreateDispatcherQueueController(options, ref _dispatcherQueueController);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        internal int dwSize;
        internal int threadType;
        internal int apartmentType;
    }
}