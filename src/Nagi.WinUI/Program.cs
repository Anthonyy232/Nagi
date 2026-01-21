using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Activation;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Nagi.WinUI.Helpers;
using WinRT;
#if !MSIX_PACKAGE
using Velopack;
#endif

namespace Nagi.WinUI;

/// <summary>
///     Application entry point with single-instance enforcement using Mutex and Named Pipes.
/// </summary>
public static class Program
{
    private static SingleInstanceManager? _singleInstanceManager;

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        #if !MSIX_PACKAGE
                VelopackApp.Build().Run();
                // Set the AppUserModelId for unpackaged runs to ensure Windows correctly identifies the app
                // for features like the Taskbar and System Media Transport Controls (SMTC).
                SetCurrentProcessExplicitAppUserModelID("Nagi.MusicPlayer");
        #endif

        ComWrappersSupport.InitializeComWrappers();

        // Ensure COM initialization in release builds
        #if !DEBUG
                Thread.Sleep(100);
        #endif

        // Check for single instance
        var logger = CreateBootstrapLogger();
        _singleInstanceManager = new SingleInstanceManager(logger);

        if (!_singleInstanceManager.TryAcquire())
        {
            // Another instance is running, send activation message and exit
            logger?.LogInformation("Secondary instance detected, sending activation to primary");
            var filePath = TryGetFilePathFromArgs(args);
            
            try
            {
                var sent = await _singleInstanceManager
                    .SendActivationAsync(filePath)
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);

                if (sent)
                {
                    logger?.LogInformation("Activation message sent successfully, exiting");
                }
                else
                {
                    logger?.LogWarning("Failed to send activation message, exiting anyway");
                }
            }
            catch (TimeoutException)
            {
                logger?.LogWarning("Timed out waiting to send activation message, exiting anyway");
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to send activation message, exiting anyway");
            }

            _singleInstanceManager.Dispose();
            return 0;
        }

        // Primary instance - subscribe to activation events
        _singleInstanceManager.ActivationReceived += OnActivationReceived;

        logger?.LogInformation("Primary instance starting");

        Application.Start(p =>
        {
            // Configure UI thread synchronization context
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        // Cleanup on exit
        _singleInstanceManager.ActivationReceived -= OnActivationReceived;
        _singleInstanceManager.Dispose();

        return 0;
    }

    /// <summary>
    ///     Creates a minimal logger for bootstrapping before the full DI container is available.
    /// </summary>
    private static ILogger<SingleInstanceManager>? CreateBootstrapLogger()
    {
        try
        {
            // We can't use the full logging infrastructure yet, so return null
            // The SingleInstanceManager will work without logging
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Handles activation messages from secondary instances.
    /// </summary>
    private static void OnActivationReceived(string? filePath)
    {
        App.CurrentApp?.HandleExternalActivation(filePath);
    }

    /// <summary>
    ///     Extracts file path from command line arguments.
    /// </summary>
    private static string? TryGetFilePathFromArgs(string[] args)
    {
        if (args == null || args.Length == 0) return null;

        // The last argument is typically the file path
        var potentialPath = args[^1];

        if (!string.IsNullOrEmpty(potentialPath) &&
            (File.Exists(potentialPath) || Directory.Exists(potentialPath)))
        {
            return potentialPath;
        }

        return null;
    }

    #region P/Invoke Declarations

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

    #endregion
}
