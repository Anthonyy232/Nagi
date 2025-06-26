using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nagi;

public static class Program {
    [STAThread]
    static int Main(string[] args) {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Enforce single-instancing. If another instance is running, redirect
        // activation to it and then exit.
        if (TryRedirectActivation()) {
            return 0;
        }

        // If this is the primary instance, start the XAML application.
        Application.Start((p) => {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }

    // Determines if this is the first instance of the application.
    // If not, it redirects activation to the main instance and returns true.
    private static bool TryRedirectActivation() {
        // A unique key for identifying the application instance.
        const string instanceKey = "NagiMusicPlayerInstance-9A8B7C6D";
        AppInstance keyInstance = AppInstance.FindOrRegisterForKey(instanceKey);

        if (keyInstance.IsCurrent) {
            // This is the first instance, so register a handler for subsequent activations.
            keyInstance.Activated += OnActivated;
            Debug.WriteLine("Application started as the primary instance.");
            return false;
        }
        else {
            // This is a subsequent instance, so redirect activation to the primary instance.
            Debug.WriteLine("Redirecting activation to the primary instance.");
            AppActivationArguments args = AppInstance.GetCurrent().GetActivatedEventArgs();
            RedirectActivationTo(args, keyInstance);
            return true;
        }
    }

    // Handles activation requests for the primary application instance.
    private static void OnActivated(object? sender, AppActivationArguments args) {
        Debug.WriteLine("Primary instance activated by a subsequent instance.");
        // Dispatch to the UI thread to safely interact with the main window.
        App.MainDispatcherQueue?.TryEnqueue(() => {
            var mainWindow = App.RootWindow;
            if (mainWindow != null) {
                // Ensure the window is visible, as it might be minimized or hidden.
                mainWindow.AppWindow.Show();
                // Bring the window to the foreground and give it focus.
                mainWindow.Activate();
            }
        });
    }

    // Asynchronously redirects activation to the primary instance and waits for
    // the operation to complete without blocking the STA thread.
    // This uses a standard pattern for unpackaged WinUI 3 apps. The redirection
    // is an async COM call. To wait for it in a synchronous main method on an
    // STA thread, we run the call on a background thread and use CoWaitForMultipleObjects
    // to wait in a way that keeps the message pump running, avoiding deadlocks.
    private static void RedirectActivationTo(AppActivationArguments args, AppInstance keyInstance) {
        // Use an event to signal completion from the background thread.
        var redirectEventHandle = CreateEvent(IntPtr.Zero, true, false, null);

        Task.Run(() => {
            keyInstance.RedirectActivationToAsync(args).AsTask().Wait();
            SetEvent(redirectEventHandle);
        });

        // Wait for the redirection to complete without blocking the STA thread.
        const uint CWMO_DEFAULT = 0;
        const uint INFINITE = 0xFFFFFFFF;
        _ = CoWaitForMultipleObjects(
           CWMO_DEFAULT,
           INFINITE,
           1,
           [redirectEventHandle],
           out _);
    }

    #region P/Invoke Declarations

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint dwFlags, uint dwMilliseconds, ulong nHandles, IntPtr[] pHandles, out uint dwIndex);

    #endregion
}