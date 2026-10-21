using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Activation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services;
using Nagi.Presentation.Logging;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Services;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.Services.Implementations;
using Nagi.WinUI.Services;
using Serilog;
using WinRT;
using LaunchActivatedEventArgs = Microsoft.UI.Xaml.LaunchActivatedEventArgs;
using UnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;

namespace Nagi.WinUI;

public partial class App : Application
{
    private static string? _currentLogFilePath;

    private readonly ConcurrentQueue<string> _fileActivationQueue = new();
    private bool _isProcessingFileQueue;
    private bool _coreServicesInitialized;
    private volatile bool _hasExternalActivation;
    private JumpListTask _pendingJumpListTask;
    private bool _isReportingCrash;
    private ILogger<App>? _logger;
    private Window? _window;

    public App()
    {
        CurrentApp = this;
        InitializeComponent();

        UnhandledException += OnAppUnhandledException;
    }

    public static App? CurrentApp { get; private set; }

    public static Window? RootWindow => CurrentApp?._window;

    public static IServiceProvider? Services { get; private set; }

    public static DispatcherQueue? MainDispatcherQueue => CurrentApp?._window?.DispatcherQueue;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();
        _currentLogFilePath = AppLogging.Configure(PackagedAppData.CreatePathConfiguration().LogsDirectory);

        try
        {
            InitializeWindowAndServices(configuration);
            _logger = Services!.GetRequiredService<ILogger<App>>();
            _logger.LogInformation("Application starting up.");

            InitializeSystemIntegration();

            if (_window is MainWindow mainWindow)
            {
                mainWindow.Content = new Pages.SplashPage();
                mainWindow.InitializeCustomTitleBar();
                await mainWindow.RestoreWindowStateAsync();
            }
            // Before doing heavy blocking initialization, show the window to the user
            var isStartupLaunch = Environment.GetCommandLineArgs()
                .Any(arg => arg.Equals("--startup", StringComparison.OrdinalIgnoreCase));
            var uiSettings = Services!.GetRequiredService<IUISettingsService>();
            var startMinimized = await uiSettings.GetStartMinimizedEnabledAsync();

            if (!isStartupLaunch && !startMinimized)
            {
                _window?.AppWindow?.Show();
                _window?.Activate();
                if (_window?.AppWindow is not null) _window.AppWindow.IsShownInSwitchers = true;
            }
            HandleInitialActivation(args.UWPLaunchActivatedEventArgs);

            var restoreSession = _fileActivationQueue.IsEmpty;
            await AppServices.StartAsync(Services!, restoreSession, _logger!);
            if (_isExiting) return;
            _coreServicesInitialized = true;
            await Services!.GetRequiredService<IApplicationLifecycle>().NavigateToMainContentAsync();

            _ = ProcessFileActivationQueueAsync();

            await HandleWindowActivationAsync(isStartupLaunch);
            await RunPendingJumpListTaskAsync();

            EnqueuePostLaunchTasks();
        }
        catch (Exception ex)
        {
            if (_isExiting)
            {
                Log.Debug(ex, "Startup interrupted by application shutdown.");
                return;
            }
            Log.Fatal(ex, "Application terminated unexpectedly during startup.");
            await ShowCrashReportAndExitAsync(ex);
        }
    }

    private void HandleInitialActivation(IActivatedEventArgs args)
    {
        string? filePath = null;

        if (args.Kind == ActivationKind.File)
        {
            var fileArgs = args.As<IFileActivatedEventArgs>();
            if (fileArgs.Files.Any()) filePath = fileArgs.Files[0].Path;
        }
        else if (args.Kind == ActivationKind.Launch)
        {
            var commandLineArgs = Environment.GetCommandLineArgs()[1..];
            filePath = Program.TryGetFilePathFromArgs(commandLineArgs);
            _pendingJumpListTask = JumpListTasks.Parse(commandLineArgs);
            if (_pendingJumpListTask != JumpListTask.None) _hasExternalActivation = true;
        }

        if (!string.IsNullOrEmpty(filePath))
        {
            _hasExternalActivation = true;
            _fileActivationQueue.Enqueue(filePath);
        }
    }

    /// <summary>
    ///     Handles an activation request from an external source (e.g., a secondary instance).
    ///     This method orchestrates file queuing and window activation logic on the UI thread.
    /// </summary>
    internal void HandleExternalActivation(string? filePath, JumpListTask jumpListTask)
    {
        _hasExternalActivation = true;
        if (!string.IsNullOrEmpty(filePath)) _fileActivationQueue.Enqueue(filePath);
        if (jumpListTask != JumpListTask.None) _pendingJumpListTask = jumpListTask;
        MainDispatcherQueue?.TryEnqueue(() =>
        {
            if (!_coreServicesInitialized || _isExiting) return;

            _ = ProcessFileActivationQueueAsync();

            try
            {
                var windowService = Services!.GetRequiredService<IWindowService>();

                // The mini-player task replaces the main window, and a file opened while the
                // mini-player is up plays there without disrupting it.
                var staysInMiniPlayer = jumpListTask == JumpListTask.MiniPlayer ||
                                        (!string.IsNullOrEmpty(filePath) && windowService.IsMiniPlayerActive);
                if (!staysInMiniPlayer) windowService.ShowAndActivate();
                _ = RunPendingJumpListTaskAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "HandleExternalActivation: Exception during window activation");
            }
        });
    }

    private async Task RegisterJumpListAsync(bool includeMiniPlayer)
    {
        try
        {
            if (!_isExiting) await JumpListTasks.RegisterAsync(includeMiniPlayer);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to register the taskbar jump list");
        }
    }

    private async Task RunPendingJumpListTaskAsync()
    {
        if (!_coreServicesInitialized || _isExiting) return;
        var task = Interlocked.Exchange(ref _pendingJumpListTask, JumpListTask.None);

        try
        {
            switch (task)
            {
                case JumpListTask.MiniPlayer:
                    // Minimizing swaps in the mini-player, the same as the window's own minimize button.
                    if (_window?.AppWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize();
                    break;
                case JumpListTask.ShuffleAll:
                    await Services!.GetRequiredService<IMusicPlaybackService>().ShuffleLibraryAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to run jump list task {JumpListTask}", task);
        }
    }

    private async Task ProcessFileActivationQueueAsync()
    {
        if (!_coreServicesInitialized || _isProcessingFileQueue || _isExiting) return;

        _isProcessingFileQueue = true;
        try
        {
            while (!_isExiting && _fileActivationQueue.TryDequeue(out var filePath))
            {
                try
                {
                    await Services!.GetRequiredService<IMusicPlaybackService>().PlayTransientFileAsync(filePath);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to process file '{FilePath}'", filePath);
                }
            }
        }
        finally
        {
            _isProcessingFileQueue = false;
        }
    }

    private static IServiceProvider ConfigureServices(MainWindow mainWindow, IConfiguration configuration)
    {
        var services = new ServiceCollection();

        services.AddLogging(loggingBuilder => loggingBuilder.AddSerilog());
        services.AddSingleton(configuration);
        services.AddNagiHttpClients();
        services.AddNagiMainWindow(mainWindow);
        services.AddNagiPlatformServices();
        services.AddNagiCore();
        services.AddNagiPresentation();
        services.AddNagiShell();

        return services.BuildServiceProvider();
    }

    private void InitializeWindowAndServices(IConfiguration configuration)
    {
        var mainWindow = new MainWindow();
        _window = mainWindow;
        _window.Closed += OnWindowClosed;

        Services = ConfigureServices(mainWindow, configuration);

        mainWindow.InitializeDependencies(Services.GetRequiredService<IUISettingsService>(),
            Services.GetRequiredService<ITaskbarService>(), Services.GetRequiredService<TransparencyEffectsMonitor>(),
            Services.GetRequiredService<ILogger<MainWindow>>());
    }

    private void InitializeSystemIntegration()
    {
        try
        {
            _window!.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/AppLogo.ico"));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to set window icon");
        }
    }

    private bool _isExiting;

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // We need to handle this manually to ensure async tasks complete before the app exits.
        args.Handled = true;
        if (_isExiting) return;
        _isExiting = true;

        try
        {
            if (_window is MainWindow mainWindow)
            {
                _logger?.LogDebug("Saving window state...");
                await mainWindow.SaveWindowStateAsync();
                mainWindow.Cleanup();
            }

            if (Services is not null)
            {
                await AppServices.StopAsync(Services, _logger ?? NullLogger<App>.Instance);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during application shutdown.");
        }
        finally
        {
            try
            {
                if (Services is IAsyncDisposable asyncDisposableServices)
                    await asyncDisposableServices.DisposeAsync();
                else if (Services is IDisposable disposableServices)
                    disposableServices.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to dispose application services.");
            }
            finally
            {
                await ExitAsync(0);
            }
        }
    }

    private static async Task ExitAsync(int exitCode)
    {
        try
        {
            await Log.CloseAndFlushAsync();
        }
        finally
        {
            Environment.Exit(exitCode);
        }
    }

    private void OnAppUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        _ = ShowCrashReportAndExitAsync(e.Exception);
    }

    private async Task ShowCrashReportAndExitAsync(Exception ex)
    {
        if (_isReportingCrash) return;
        _isReportingCrash = true;
        try
        {
            Log.Fatal(ex, "A critical error occurred. Application will now terminate. Log path: {LogPath}",
                _currentLogFilePath);
            var fullCrashReport = AppLogging.BuildCrashReport(ex);
            if (MainDispatcherQueue is not { } dispatcher) return;

            var dispatcherService = Services?.GetService<IDispatcherService>() ?? new DispatcherService(dispatcher);
            await dispatcherService.EnqueueAsync(async () =>
            {
                if (Services?.GetService<IUIService>() is not { } uiService) return;
                var result = await uiService.ShowCrashReportDialogAsync(
                    Strings.CrashReport_Title,
                    Strings.CrashReport_Message,
                    fullCrashReport,
                    "https://github.com/Anthonyy232/Nagi/issues");

                if (result == CrashReportResult.Reset) await ResetApplicationDataAsync();
            });
        }
        catch (Exception dialogEx)
        {
            Debug.WriteLine($"Failed to show the crash report dialog: {dialogEx}");
        }
        finally
        {
            await ExitAsync(1);
        }
    }

    private async Task ResetApplicationDataAsync()
    {
        try
        {
            Log.Information("User requested a full application data reset from the crash dialog.");
            await AppDataReset.ResetAsync(Services, PackagedAppData.CreatePathConfiguration(),
                new ApplicationDataSettingsStore());

            Log.Information("Reset complete. Restarting application.");
            await Log.CloseAndFlushAsync();

            ElevationHelper.RestartWithoutElevation();

        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Critical failure during app reset: {ex}");
            Log.Fatal(ex, "Critical failure during app reset.");
        }
        finally
        {
            await ExitAsync(0);
        }
    }

    private async Task HandleWindowActivationAsync(bool isStartupLaunch = false)
    {
        if (_isExiting || _window is null || Services is null) return;

        var settingsService = Services.GetRequiredService<IUISettingsService>();
        var startMinimized = await settingsService.GetStartMinimizedEnabledAsync();
        var minimizeToMiniPlayer = await settingsService.GetMinimizeToMiniPlayerEnabledAsync();
        var hideToTray = await settingsService.GetHideToTrayEnabledAsync();
        if (_isExiting) return;
        var windowService = Services.GetRequiredService<IWindowService>();

        // A launch received during initialization takes precedence over startup minimization.
        if (_hasExternalActivation || !(isStartupLaunch || startMinimized))
        {
            windowService.ShowAndActivate();
        }
        else if (minimizeToMiniPlayer)
        {
            windowService.ShowMiniPlayer();
            _window.AppWindow.IsShownInSwitchers = false;
        }
        else if (!hideToTray)
        {
            WindowActivator.ShowMinimized(_window);
        }
    }

    private void EnqueuePostLaunchTasks()
    {
        MainDispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            try
            {
                if (_isExiting || Services is null) return;

                Services.GetRequiredService<ISystemMediaControls>().Initialize();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to initialize System Media Transport Controls");
            }
        });

        MainDispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            if (_isExiting || Services is null) return;
            var uiSettings = Services.GetRequiredService<IUISettingsService>();
            // The mini-player only exists while "Minimize to Mini-Player" is on, so the task follows it.
            uiSettings.MinimizeToMiniPlayerSettingChanged += enabled =>
                MainDispatcherQueue?.TryEnqueue(() => _ = RegisterJumpListAsync(enabled));
            await RegisterJumpListAsync(await uiSettings.GetMinimizeToMiniPlayerEnabledAsync());
        });
    }

}
