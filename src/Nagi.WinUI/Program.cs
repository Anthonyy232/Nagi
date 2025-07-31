using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Activation;
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

    /// <summary>
    /// The main entry point for the application.
    /// </summary>
    [STAThread]
    private static async Task<int> Main(string[] args) {
        Debug.WriteLine("Program.Main: Application starting.");

        ComWrappersSupport.InitializeComWrappers();

        // Check if another instance is running and redirect activation if so.
        bool isRedirect = await TryRedirectActivation();
        if (isRedirect) {
            Debug.WriteLine("Program.Main: This is a second instance. Redirection is complete. Exiting now.");
            return 0;
        }

        Debug.WriteLine("Program.Main: This is the first instance. Proceeding with normal startup.");

        // Set up Velopack for automatic updates in unpackaged deployments.
#if !MSIX_PACKAGE
        VelopackApp.Build().Run();
#endif

        // Register an activation handler to process requests from subsequent instances.
        AppInstance.GetCurrent().Activated += OnActivated;
        Debug.WriteLine("Program.Main: Registered activation handler for future launches.");

        // Start the WinUI application.
        Application.Start(p => {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }

    /// <summary>
    /// Attempts to find the main application instance and redirect activation to it.
    /// </summary>
    /// <returns>True if activation was redirected to another instance; otherwise, false.</returns>
    private static async Task<bool> TryRedirectActivation() {
        AppInstance mainInstance = AppInstance.FindOrRegisterForKey(AppInstanceKey);

        if (!mainInstance.IsCurrent) {
            Debug.WriteLine("Program.Main.TryRedirect: Another instance is running. Redirecting activation.");
            AppActivationArguments args = AppInstance.GetCurrent().GetActivatedEventArgs();
            await mainInstance.RedirectActivationToAsync(args);
            return true;
        }

        Debug.WriteLine("Program.Main.TryRedirect: No other instance found. This is the main instance.");
        return false;
    }

    /// <summary>
    /// Handles activation requests that have been redirected to the main instance.
    /// </summary>
    private static void OnActivated(object? sender, AppActivationArguments args) {
        Debug.WriteLine($"Program.OnActivated: Primary instance received an activation. Kind: {args.Kind}");

        if (App.CurrentApp is not null) {
            string? filePath = null;

            // Handle file activation for packaged apps.
            if (args.Kind == ExtendedActivationKind.File) {
                if (args.Data is IFileActivatedEventArgs fileArgs && (fileArgs.Files?.Count ?? 0) > 0) {
                    filePath = fileArgs.Files[0].Path;
                    Debug.WriteLine($"Program.OnActivated: Found file via redirected File Activation: {filePath}");
                }
            }
            // Handle command-line activation for unpackaged apps.
            else if (args.Kind == ExtendedActivationKind.Launch) {
                if (args.Data is ILaunchActivatedEventArgs launchArgs && !string.IsNullOrWhiteSpace(launchArgs.Arguments)) {
                    string commandLine = launchArgs.Arguments;
                    Debug.WriteLine($"Program.OnActivated: Received raw command-line arguments: {commandLine}");

                    // Use a regex to robustly find a file path, which may be quoted.
                    // This assumes the file path is the second quoted argument.
                    MatchCollection matches = Regex.Matches(commandLine, "\"([^\"]*)\"");
                    if (matches.Count > 1) {
                        filePath = matches[1].Groups[1].Value;
                        Debug.WriteLine($"Program.OnActivated: Parsed file path from arguments: {filePath}");
                    }
                    // Fallback for non-quoted paths.
                    else if (!commandLine.Contains("\"")) {
                        var commandArgs = commandLine.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                        if (commandArgs.Length > 1) {
                            filePath = commandArgs[1];
                            Debug.WriteLine($"Program.OnActivated: Parsed file path from non-quoted arguments: {filePath}");
                        }
                    }
                }
            }

            // If a file path was found, enqueue it for processing.
            if (!string.IsNullOrEmpty(filePath)) {
                App.CurrentApp.EnqueueFileActivation(filePath);
            }

            // Ensure the main window is brought to the foreground.
            App.MainDispatcherQueue?.TryEnqueue(() => {
                if (App.RootWindow is not null) {
                    Debug.WriteLine("Program.OnActivated: Bringing main window to foreground.");
                    App.RootWindow.AppWindow.Show();
                    App.RootWindow.Activate();
                }
            });
        }
    }
}