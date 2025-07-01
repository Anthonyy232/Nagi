using System;
using System.Runtime.InteropServices;
using Windows.System;

namespace Nagi.Helpers;

/// <summary>
/// Provides a helper method to ensure a dispatcher queue exists on the current thread.
/// This is a prerequisite for using Mica or Acrylic system backdrops in a WinUI 3 application.
/// </summary>
internal sealed class WindowsSystemDispatcherQueueHelper : IDisposable {
    private object? _dispatcherQueueController;
    private bool _disposed;

    // See: https://docs.microsoft.com/windows/win32/api/dispatcherqueue/ne-dispatcherqueue-dispatcherqueue_thread_type
    private enum DispatcherQueueThreadType {
        Dedicated = 1, // DQTYPE_THREAD_DEDICATED
        Current = 2,   // DQTYPE_THREAD_CURRENT
    }

    // See: https://docs.microsoft.com/windows/win32/api/dispatcherqueue/ne-dispatcherqueue-dispatcherqueue_thread_apartment_type
    private enum DispatcherQueueThreadApartmentType {
        None = 0,    // DQTAT_NONE
        ASTA = 1,    // DQTAT_COM_ASTA
        STA = 2,     // DQTAT_COM_STA
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions {
        internal uint dwSize;
        internal DispatcherQueueThreadType threadType;
        internal DispatcherQueueThreadApartmentType apartmentType;
    }

    [DllImport("CoreMessaging.dll", SetLastError = true)]
    private static extern int CreateDispatcherQueueController(
        [In] DispatcherQueueOptions options,
        [In, Out, MarshalAs(UnmanagedType.IUnknown)] ref object? dispatcherQueueController);

    /// <summary>
    /// Ensures a DispatcherQueue is available for the current thread.
    /// If one does not exist, it creates one.
    /// </summary>
    public void EnsureDispatcherQueue() {
        // If a DispatcherQueue already exists, no action is needed.
        if (DispatcherQueue.GetForCurrentThread() is not null) {
            return;
        }

        // Create a new DispatcherQueueController for the current thread.
        if (_dispatcherQueueController is null) {
            var options = new DispatcherQueueOptions {
                dwSize = (uint)Marshal.SizeOf<DispatcherQueueOptions>(),
                threadType = DispatcherQueueThreadType.Current,
                apartmentType = DispatcherQueueThreadApartmentType.STA
            };

            // P/Invoke to create the controller.
            int hresult = CreateDispatcherQueueController(options, ref _dispatcherQueueController);
            if (hresult != 0) // S_OK
            {
                Marshal.ThrowExceptionForHR(hresult);
            }
        }
    }

    /// <summary>
    /// Disposes of the created DispatcherQueueController.
    /// </summary>
    public void Dispose() {
        if (_disposed) {
            return;
        }

        // The controller object is IDisposable and needs to be explicitly released.
        if (_dispatcherQueueController is IDisposable controller) {
            controller.Dispose();
        }

        _dispatcherQueueController = null;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}