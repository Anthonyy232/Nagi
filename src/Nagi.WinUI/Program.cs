using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Activation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinRT;
#if !MSIX_PACKAGE
using Velopack;
#endif

namespace Nagi.WinUI;

/// <summary>
///     Contains the application's entry point and single-instancing logic.
/// </summary>
public static class Program
{
    private const string AppInstanceKey = "NagiMusicPlayerInstance-9A8B7C6D";

    [STAThread]
    private static int Main(string[] args)
    {
#if !MSIX_PACKAGE
        VelopackApp.Build().Run();
#endif

        ComWrappersSupport.InitializeComWrappers();

        // If this is a secondary instance, redirect its activation arguments to the primary
        // instance and exit immediately. This enforces a single-instance model.
        if (TryRedirectActivation()) return 0;

        Debug.WriteLine("[Program] Primary instance starting.");

        Application.Start(p =>
        {
            // The SynchronizationContext is essential for the UI thread to correctly
            // manage async operations and callbacks.
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }

    /// <summary>
    ///     Checks for an existing application instance. If found, redirects activation and returns true.
    ///     If not found, it registers the current process as the primary instance and returns false.
    /// </summary>
    /// <returns>True if activation was redirected; otherwise, false.</returns>
    private static bool TryRedirectActivation()
    {
        var mainInstance = AppInstance.FindOrRegisterForKey(AppInstanceKey);

        if (mainInstance.IsCurrent)
        {
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
    ///     Handles activation requests that have been redirected to the main instance.
    ///     This method runs in the primary instance when a user launches the app a second time.
    /// </summary>
    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        var filePath = TryGetFilePathFromArgs(args);
        Debug.WriteLine($"[Program] Primary instance activated. File path found: {filePath ?? "None"}");

        if (!string.IsNullOrEmpty(filePath)) App.CurrentApp?.EnqueueFileActivation(filePath);

        // Only bring the main window to the foreground if appropriate based on current state
        // This prevents interrupting users when the app is minimized to tray or in mini-player mode
        App.MainDispatcherQueue?.TryEnqueue(() =>
        {
            if (App.RootWindow is not null)
            {
                try
                {
                    // Check if services are available and initialized
                    if (App.Services is not null)
                    {
                        var windowService = App.Services.GetService<Nagi.WinUI.Services.Abstractions.IWindowService>();
                        
                        // Only proceed with service-based checks if the service is properly initialized
                        if (windowService is not null)
                        {
                            var windowVisible = App.RootWindow.AppWindow.IsVisible;
                            var miniPlayerActive = windowService.IsMiniPlayerActive;
                            
                            // Only bring to front if window is visible and mini-player is not active, OR if no file is being opened
                            var bringToFront = (windowVisible && !miniPlayerActive) || string.IsNullOrEmpty(filePath);

                            if (bringToFront)
                            {
                                App.RootWindow.AppWindow.Show();
                                App.RootWindow.Activate();
                            }
                            return; // Exit early since we handled it with full service info
                        }
                    }
                    
                    // Fallback: Services not available or not initialized yet
                    var isWindowVisible = App.RootWindow.AppWindow.IsVisible;

                    var shouldBringToFront = string.IsNullOrEmpty(filePath) || isWindowVisible;

                    if (shouldBringToFront)
                    {
                        App.RootWindow.AppWindow.Show();
                        App.RootWindow.Activate();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ERROR] Program.OnActivated: Exception during activation handling. {ex.Message}");
                    
                    // Final fallback: Basic behavior for non-file activations only
                    if (string.IsNullOrEmpty(filePath))
                    {
                        App.RootWindow.AppWindow.Show();
                        App.RootWindow.Activate();
                    }
                }
            }
        });
    }

    /// <summary>
    ///     Extracts a file path from activation arguments, supporting both file associations and command-line launches.
    /// </summary>
    private static string? TryGetFilePathFromArgs(AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.File && args.Data is IFileActivatedEventArgs fileArgs)
        {
            if (fileArgs.Files?.Count > 0) return fileArgs.Files[0].Path;
        }
        else if (args.Kind == ExtendedActivationKind.Launch && args.Data is ILaunchActivatedEventArgs launchArgs)
        {
            if (!string.IsNullOrWhiteSpace(launchArgs.Arguments))
            {
                var commandLineArgs = launchArgs.Arguments.Trim();
                // Handles file paths that are quoted (e.g., contain spaces) by removing the quotes.
                if (commandLineArgs.StartsWith("\"") && commandLineArgs.EndsWith("\""))
                    return commandLineArgs.Substring(1, commandLineArgs.Length - 2);
                return commandLineArgs;
            }
        }

        return null;
    }

    /// <summary>
    ///     Redirects activation to the primary instance and waits for the operation to complete.
    /// </summary>
    private static void RedirectActivationTo(AppActivationArguments args, AppInstance keyInstance)
    {
        var redirectEventHandle = CreateEvent(IntPtr.Zero, true, false, null);

        // The redirection must run on a background thread to avoid blocking the STA thread.
        Task.Run(() =>
        {
            keyInstance.RedirectActivationToAsync(args).AsTask().Wait();
            SetEvent(redirectEventHandle);
        });

        // A standard Task.Wait() or await would block the message pump (deadlocks)
        // CoWaitForMultipleObjects does message pump which is needed for activation redirection in unpackaged WinUI apps.
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

    #endregion
}