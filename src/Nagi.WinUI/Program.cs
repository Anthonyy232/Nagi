using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Activation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Nagi.WinUI.Services.Abstractions;
using WinRT;
#if !MSIX_PACKAGE
using Velopack;
#endif

namespace Nagi.WinUI;

/// <summary>
/// Contains the application's entry point and single-instancing logic.
/// </summary>
public static class Program {
    private const string AppInstanceKey = "NagiMusicPlayerInstance-9A8B7C6D";

    [STAThread]
    private static int Main(string[] args) {
#if !MSIX_PACKAGE
        VelopackApp.Build().Run();
#endif

        ComWrappersSupport.InitializeComWrappers();

        // Enforce a single-instance model by redirecting activation
        // of subsequent instances to the primary one.
        if (TryRedirectActivation()) {
            return 0;
        }

        Debug.WriteLine("[Program] Primary instance starting.");

        Application.Start(p => {
            // The SynchronizationContext is essential for the UI thread to correctly
            // manage async operations and callbacks.
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }

    /// <summary>
    /// Checks for an existing application instance. If found, redirects activation and returns true.
    /// If not found, it registers the current process as the primary instance and returns false.
    /// </summary>
    /// <returns>True if activation was redirected; otherwise, false.</returns>
    private static bool TryRedirectActivation() {
        var mainInstance = AppInstance.FindOrRegisterForKey(AppInstanceKey);

        if (mainInstance.IsCurrent) {
            // This is the primary instance, so it must listen for activations from subsequent instances.
            mainInstance.Activated += OnActivated;
            return false;
        }

        Debug.WriteLine("[Program] Secondary instance detected. Redirecting activation...");
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        RedirectActivationTo(args, mainInstance);
        return true;
    }

    /// <summary>
    /// Handles activation requests that have been redirected to the main instance.
    /// This method runs in the primary instance when a user launches the app a second time.
    /// </summary>
    private static void OnActivated(object? sender, AppActivationArguments args) {
        var filePath = TryGetFilePathFromArgs(args);
        Debug.WriteLine($"[Program] Primary instance activated. File path found: {filePath ?? "None"}");

        if (!string.IsNullOrEmpty(filePath)) {
            App.CurrentApp?.EnqueueFileActivation(filePath);
        }

        // Ensure window activation logic runs on the main UI thread.
        App.MainDispatcherQueue?.TryEnqueue(() => {
            if (App.RootWindow is null) return;

            try {
                var windowService = App.Services?.GetService<IWindowService>();
                var isWindowVisible = App.RootWindow.AppWindow.IsVisible;
                var isMiniPlayerActive = windowService?.IsMiniPlayerActive ?? false;

                // Bring window to front if opening a file while the main window is visible and not in mini-player mode.
                // Always bring to front if not opening a file (i.e., just launching the app again).
                var shouldActivate = string.IsNullOrEmpty(filePath) || (isWindowVisible && !isMiniPlayerActive);

                if (shouldActivate) {
                    App.RootWindow.AppWindow.Show();
                    App.RootWindow.Activate();
                }
            }
            catch (Exception ex) {
                Debug.WriteLine($"[ERROR] Program.OnActivated: Exception during activation handling. {ex.Message}");
                // A minimal fallback to ensure the window appears on a simple re-launch if an error occurred.
                if (string.IsNullOrEmpty(filePath)) {
                    App.RootWindow?.AppWindow.Show();
                    App.RootWindow?.Activate();
                }
            }
        });
    }

    /// <summary>
    /// Extracts a file path from activation arguments, supporting both file associations and command-line launches.
    /// </summary>
    private static string? TryGetFilePathFromArgs(AppActivationArguments args) {
        if (args.Kind == ExtendedActivationKind.File && args.Data is IFileActivatedEventArgs fileArgs) {
            if (fileArgs.Files is { Count: > 0 }) {
                return fileArgs.Files[0].Path;
            }
        }
        else if (args.Kind == ExtendedActivationKind.Launch && args.Data is ILaunchActivatedEventArgs launchArgs) {
            if (string.IsNullOrWhiteSpace(launchArgs.Arguments)) return null;
            var argv = CommandLineToArgvW(launchArgs.Arguments, out var argc);
            if (argv == IntPtr.Zero) return null;

            try {
                if (argc > 0) {
                    // Assume the file path is the last argument.
                    var lastArgPtr = Marshal.ReadIntPtr(argv, (argc - 1) * IntPtr.Size);
                    var potentialPath = Marshal.PtrToStringUni(lastArgPtr);

                    if (!string.IsNullOrEmpty(potentialPath) &&
                        (File.Exists(potentialPath) || Directory.Exists(potentialPath))) {
                        return potentialPath;
                    }
                }
            }
            finally {
                LocalFree(argv);
            }
        }

        return null;
    }

    /// <summary>
    /// Redirects activation to the primary instance and waits for the operation to complete.
    /// </summary>
    private static void RedirectActivationTo(AppActivationArguments args, AppInstance keyInstance) {
        var redirectEventHandle = CreateEvent(IntPtr.Zero, true, false, null);

        // The redirection must run on a background thread to avoid blocking the STA thread.
        _ = Task.Run(() => {
            keyInstance.RedirectActivationToAsync(args).AsTask().Wait();
            SetEvent(redirectEventHandle);
        });

        // CoWaitForMultipleObjects processes the message pump, which is required for
        // activation redirection in unpackaged WinUI apps to prevent deadlocks.
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
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState,
        string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint dwFlags, uint dwMilliseconds, uint nHandles,
        IntPtr[] pHandles, out uint dwIndex);

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    #endregion
}