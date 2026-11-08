using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.Activation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Nagi.Core.Platform;
using Nagi.Presentation.Logging;
using Nagi.Presentation.Shell;
using Nagi.Presentation;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services;
using Nagi.WinUI.Services.Implementations;
using Serilog;
using WinRT;
using LaunchActivatedEventArgs = Microsoft.UI.Xaml.LaunchActivatedEventArgs;
using UnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;

namespace Nagi.WinUI;

public partial class App : Application
{
    private readonly SingleInstanceManager _singleInstanceManager;
    private readonly string? _launchFilePath;
    private readonly LaunchCommand _launchCommand;
    private bool _isReportingCrash;
    private string? _logsDirectory;
    private MainWindow? _window;

    internal App(SingleInstanceManager singleInstanceManager, string? launchFilePath, LaunchCommand launchCommand)
    {
        _singleInstanceManager = singleInstanceManager;
        _launchFilePath = launchFilePath;
        _launchCommand = launchCommand;
        InitializeComponent();

        UnhandledException += OnAppUnhandledException;
    }

    private static IServiceProvider? Services { get; set; }

    private bool IsExiting => Services?.GetService<IWindowService>()?.IsExiting == true;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _logsDirectory = PackagedAppData.CreatePathConfiguration().LogsDirectory;
        AppLogging.Configure(_logsDirectory);

        try
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .Build();
            Services = new ServiceCollection().AddNagiApp(configuration).BuildServiceProvider();
            ViewModelPage.Initialize(Services.GetRequiredService<IServiceScopeFactory>());
            Services.GetRequiredService<ILogger<App>>().LogInformation("Application starting up.");

            _window = Services.GetRequiredService<MainWindow>();
            _window.Closed += OnWindowClosed;
            _window.SetContent(new SplashPage());
            Services.GetRequiredService<TaskbarService>().Start();

            var launch = Services.GetRequiredService<LaunchCoordinator>();
            var (filePath, command) = ReadActivation(args.UWPLaunchActivatedEventArgs);
            if (filePath is not null || command != LaunchCommand.None) launch.Activate(filePath, command);
            _singleInstanceManager.StartListening(launch.Activate);

            await launch.StartAsync();
            Services.GetRequiredService<JumpListService>().Start();
        }
        catch (Exception ex) when (IsExiting)
        {
            Log.Debug(ex, "Startup interrupted by application shutdown.");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly during startup.");
            await ShowCrashReportAndExitAsync(ex);
        }
    }

    private (string? FilePath, LaunchCommand Command) ReadActivation(IActivatedEventArgs args) => args.Kind switch
    {
        ActivationKind.File => (args.As<IFileActivatedEventArgs>().Files.FirstOrDefault()?.Path, LaunchCommand.None),
        ActivationKind.Launch => (_launchFilePath, _launchCommand),
        _ => (null, LaunchCommand.None)
    };

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // Keep the window until the services have stopped; the process exits afterwards.
        args.Handled = true;
        _window!.Closed -= OnWindowClosed;

        try
        {
            _window.SaveWindowState();
            await AppServices.StopAsync(Services!, Services!.GetRequiredService<ILogger<App>>());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during application shutdown.");
        }
        finally
        {
            try
            {
                if (Services is IAsyncDisposable asyncDisposableServices)
                    await asyncDisposableServices.DisposeAsync();
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
            Log.Fatal(ex, "A critical error occurred. Application will now terminate. Logs are in {LogsDirectory}",
                _logsDirectory);
            var fullCrashReport = AppLogging.BuildCrashReport(ex);
            if (Services?.GetService<UIService>() is not { } uiService) return;

            await Services.GetRequiredService<IDispatcherService>().EnqueueAsync(async () =>
            {
                if (await uiService.ShowCrashReportDialogAsync(fullCrashReport)) await ResetApplicationDataAsync();
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

    private static async Task ResetApplicationDataAsync()
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
}
