using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinRT;

namespace Nagi;

/// <summary>
/// The entry point for the application.
/// </summary>
public static class Program {
    /// <summary>
    /// The main entry point for the application.
    /// Initializes COM wrappers, enforces single-instancing, and starts the XAML application.
    /// </summary>
    /// <param name="args">Command-line arguments passed to the application.</param>
    /// <returns>An integer representing the application's exit code.</returns>
    [STAThread]
    private static int Main(string[] args) {
        // Initialize COM wrappers for WinRT interop.
        ComWrappersSupport.InitializeComWrappers();

        // Enforce single-instancing. If another instance is running,
        // redirect activation to it and then exit this instance.
        if (TryRedirectActivation()) {
            return 0;
        }

        // If this is the primary instance, start the XAML application.
        Application.Start(p => {
            // Set up a synchronization context for the UI thread's DispatcherQueue.
            // This is crucial for async operations to correctly resume on the UI thread.
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);

            // Create and initialize the main App instance.
            _ = new App();
        });

        return 0;
    }

    /// <summary>
    /// Determines if this is the first instance of the application.
    /// If not, it redirects activation to the main instance and returns true.
    /// </summary>
    /// <returns><c>true</c> if activation was redirected to an existing instance; otherwise, <c>false</c>.</returns>
    private static bool TryRedirectActivation() {
        // A unique key for identifying the application instance across runs.
        const string instanceKey = "NagiMusicPlayerInstance-9A8B7C6D";
        var keyInstance = AppInstance.FindOrRegisterForKey(instanceKey);

        if (keyInstance.IsCurrent) {
            // This is the first instance, so register a handler for subsequent activations.
            keyInstance.Activated += OnActivated;
            Debug.WriteLine("Application started as the primary instance.");
            return false;
        }

        // This is a subsequent instance, so redirect activation to the primary instance.
        Debug.WriteLine("Redirecting activation to the primary instance.");
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        RedirectActivationTo(args, keyInstance);
        return true;
    }

    /// <summary>
    /// Handles activation requests for the primary application instance.
    /// Ensures the main window is visible and brought to the foreground.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="args">The activation arguments.</param>
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

    /// <summary>
    /// Asynchronously redirects activation to the primary instance and waits for
    /// the operation to complete without blocking the STA thread.
    /// This uses a standard pattern for unpackaged WinUI 3 apps to wait for
    /// an async COM call in a synchronous main method.
    /// </summary>
    /// <param name="args">The activation arguments to redirect.</param>
    /// <param name="keyInstance">The <see cref="AppInstance"/> representing the primary instance.</param>
    private static void RedirectActivationTo(AppActivationArguments args, AppInstance keyInstance) {
        // Use a manual reset event to signal completion from the background thread.
        var redirectEventHandle = CreateEvent(IntPtr.Zero, true, false, null);

        // Run the async redirection on a background thread.
        Task.Run(() => {
            keyInstance.RedirectActivationToAsync(args).AsTask().Wait();
            SetEvent(redirectEventHandle); // Signal that redirection is complete.
        });

        // Wait for the redirection to complete on the STA thread while allowing message pump to run.
        const uint CWMO_DEFAULT = 0;
        const uint INFINITE = 0xFFFFFFFF;
        _ = CoWaitForMultipleObjects(
            CWMO_DEFAULT, // Default flags.
            INFINITE,     // Wait indefinitely.
            1,            // Number of handles to wait for.
            [redirectEventHandle], // The event handle.
            out _);       // Unused output parameter.
    }

    #region P/Invoke Declarations

    /// <summary>
    /// Creates or opens a named or unnamed event object.
    /// </summary>
    /// <param name="lpEventAttributes">A pointer to a SECURITY_ATTRIBUTES structure.</param>
    /// <param name="bManualReset">If TRUE, the event is a manual-reset event.</param>
    /// <param name="bInitialState">If TRUE, the initial state of the event object is signaled.</param>
    /// <param name="lpName">The name of the event object.</param>
    /// <returns>If the function succeeds, the return value is a handle to the event object.</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState,
        string? lpName);

    /// <summary>
    /// Sets the specified event object to the signaled state.
    /// </summary>
    /// <param name="hEvent">A handle to the event object.</param>
    /// <returns>If the function succeeds, the return value is nonzero.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr hEvent);

    /// <summary>
    /// Waits for one or more synchronization objects to become signaled,
    /// while continuing to dispatch COM and Send/Post messages.
    /// </summary>
    /// <param name="dwFlags">The wait flags.</param>
    /// <param name="dwMilliseconds">The time-out interval in milliseconds.</param>
    /// <param name="nHandles">The number of handles in the pHandles array.</param>
    /// <param name="pHandles">An array of handles to synchronization objects.</param>
    /// <param name="dwIndex">Receives the index of the object that satisfied the wait.</param>
    /// <returns>S_OK if successful.</returns>
    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint dwFlags, uint dwMilliseconds, uint nHandles,
        IntPtr[] pHandles, out uint dwIndex);

    #endregion
}