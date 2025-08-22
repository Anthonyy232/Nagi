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
/// Application entry point with single-instance enforcement.
/// </summary>
public static class Program {
    private const string AppInstanceKey = "NagiMusicPlayerInstance-9A8B7C6D";
    private const int MaxRetryAttempts = 3;
    private const int RetryDelayMs = 100;
    private const int RedirectionTimeoutMs = 5000;

    [STAThread]
    private static int Main(string[] args) {
        #if !MSIX_PACKAGE
                VelopackApp.Build().Run();
        #endif

        ComWrappersSupport.InitializeComWrappers();

        // Ensure COM initialization in release builds
        #if !DEBUG
                Thread.Sleep(50);
        #endif

        // Redirect secondary instances to primary
        if (TryRedirectActivation()) {
            return 0;
        }

        Debug.WriteLine("[Program] Primary instance starting.");

        Application.Start(p => {
            // Configure UI thread synchronization context
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }

    /// <summary>
    /// Enforces single-instance by redirecting secondary launches to primary.
    /// </summary>
    /// <returns>True if redirected to existing instance; false if this becomes primary.</returns>
    private static bool TryRedirectActivation() {
        AppInstance? mainInstance = null;

        // Retry AppInstance operations for stability
        for (int attempt = 0; attempt < MaxRetryAttempts; attempt++) {
            try {
                mainInstance = AppInstance.FindOrRegisterForKey(AppInstanceKey);
                break;
            }
            catch (Exception ex) {
                Debug.WriteLine($"[Program] Attempt {attempt + 1} failed to find/register app instance: {ex.Message}");
                if (attempt == MaxRetryAttempts - 1) {
                    Debug.WriteLine("[Program] All attempts failed, continuing as primary instance.");
                    return false;
                }
                Thread.Sleep(RetryDelayMs);
            }
        }

        if (mainInstance == null) {
            Debug.WriteLine("[Program] Failed to get app instance, continuing as primary.");
            return false;
        }

        if (mainInstance.IsCurrent) {
            // Register as primary instance
            mainInstance.Activated += OnActivated;
            return false;
        }

        Debug.WriteLine("[Program] Secondary instance detected. Redirecting activation...");

        // Verify primary instance is still running
        if (!IsProcessAlive(mainInstance.ProcessId)) {
            Debug.WriteLine("[Program] Main instance process is no longer running, becoming primary.");
            return false;
        }

        try {
            var args = AppInstance.GetCurrent().GetActivatedEventArgs();
            return RedirectActivationTo(args, mainInstance);
        }
        catch (Exception ex) {
            Debug.WriteLine($"[Program] Failed to redirect activation: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Validates if process is still running.
    /// </summary>
    private static bool IsProcessAlive(uint processId) {
        try {
            using var process = Process.GetProcessById((int)processId);
            return !process.HasExited;
        }
        catch {
            return false;
        }
    }

    /// <summary>
    /// Handles activation from secondary instances.
    /// </summary>
    private static void OnActivated(object? sender, AppActivationArguments args) {
        var filePath = TryGetFilePathFromArgs(args);
        Debug.WriteLine($"[Program] Primary instance activated. File path found: {filePath ?? "None"}");

        if (!string.IsNullOrEmpty(filePath)) {
            App.CurrentApp?.EnqueueFileActivation(filePath);
        }

        // Execute window activation on UI thread
        App.MainDispatcherQueue?.TryEnqueue(() => {
            if (App.RootWindow is null) return;

            try {
                var windowService = App.Services?.GetService<IWindowService>();
                var isWindowVisible = App.RootWindow.AppWindow.IsVisible;
                var isMiniPlayerActive = windowService?.IsMiniPlayerActive ?? false;

                // Show window unless opening file in mini-player mode
                var shouldActivate = string.IsNullOrEmpty(filePath) || (isWindowVisible && !isMiniPlayerActive);

                if (shouldActivate) {
                    App.RootWindow.AppWindow.Show();
                    App.RootWindow.Activate();
                }
            }
            catch (Exception ex) {
                Debug.WriteLine($"[ERROR] Program.OnActivated: Exception during activation handling. {ex.Message}");
                // Fallback for simple re-launch
                if (string.IsNullOrEmpty(filePath)) {
                    App.RootWindow?.AppWindow.Show();
                    App.RootWindow?.Activate();
                }
            }
        });
    }

    /// <summary>
    /// Extracts file path from file association or command line activation.
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
                    // Extract last argument as potential file path
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
    /// Redirects activation to primary instance with timeout protection.
    /// </summary>
    /// <returns>True if redirection succeeded; false if timeout or failure.</returns>
    private static bool RedirectActivationTo(AppActivationArguments args, AppInstance keyInstance) {
        var redirectEventHandle = CreateEvent(IntPtr.Zero, true, false, null);
        if (redirectEventHandle == IntPtr.Zero) {
            Debug.WriteLine("[Program] Failed to create event handle for redirection.");
            return false;
        }

        var redirectionSucceeded = false;

        // Perform redirection on background thread
        _ = Task.Run(async () => {
            try {
                await keyInstance.RedirectActivationToAsync(args);
                redirectionSucceeded = true;
                Debug.WriteLine("[Program] Activation redirection completed successfully.");
            }
            catch (Exception ex) {
                Debug.WriteLine($"[Program] RedirectActivationToAsync failed: {ex.Message}");
            }
            finally {
                SetEvent(redirectEventHandle);
            }
        });

        // Wait with message pump processing to prevent deadlocks
        const uint CWMO_DEFAULT = 0;
        var waitResult = CoWaitForMultipleObjects(
            CWMO_DEFAULT,
            RedirectionTimeoutMs,
            1,
            new[] { redirectEventHandle },
            out _);

        CloseHandle(redirectEventHandle);

        // Handle timeout (WAIT_TIMEOUT = 0x00000102)
        if (waitResult == 0x00000102) {
            Debug.WriteLine("[Program] Redirection timed out, becoming primary instance.");
            return false;
        }

        return redirectionSucceeded;
    }

    #region P/Invoke Declarations

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState,
        string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint dwFlags, uint dwMilliseconds, uint nHandles,
        IntPtr[] pHandles, out uint dwIndex);

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    #endregion
}