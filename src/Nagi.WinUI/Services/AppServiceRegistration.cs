using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Nagi.Audio.LibVlc;
using Nagi.Core;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Presentation.Common;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Shell;
using Nagi.Presentation.Theme;
using Nagi.Presentation;
using Nagi.WinUI.Controls;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Popups;
using Nagi.WinUI.Services.Implementations;
using Serilog;

namespace Nagi.WinUI.Services;

/// <summary>
///     Composes the whole app: Core, Presentation and the WinUI host's services, windows and pages.
/// </summary>
public static class AppServiceRegistration
{
    /// <summary>
    ///     Resolve the <see cref="MainWindow" /> first, on the UI thread: the dispatcher queue that the window, theme,
    ///     dialog and dispatcher services work with is the window's.
    /// </summary>
    public static IServiceCollection AddNagiApp(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddLogging(loggingBuilder => loggingBuilder.AddSerilog());
        services.AddSingleton(configuration);
        services.AddNagiCore();
        services.AddNagiPresentation();
        AddPlatformServices(services);
        AddShell(services);
        return services;
    }

    private static void AddPlatformServices(IServiceCollection services)
    {
        services.AddSingleton<IPathConfiguration>(_ => PackagedAppData.CreatePathConfiguration());
        services.AddSingleton<ISettingsStore, ApplicationDataSettingsStore>();
        services.AddSingleton<ICredentialLockerService, CredentialLockerService>();
        services.AddSingleton<IAutoLaunchService, StartupTaskAutoLaunchService>();

        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<UIService>();
        services.AddSingleton<IUIService>(sp => sp.GetRequiredService<UIService>());
        services.AddSingleton<IDispatcherService, DispatcherService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<ApplicationLifecycle>();
        services.AddSingleton<IApplicationLifecycle>(sp => sp.GetRequiredService<ApplicationLifecycle>());
        services.AddSingleton<IAppInfoService, AppInfoService>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());
        services.AddSingleton<ITrayPopupService, TrayPopupService>();
        services.AddSingleton<ISystemMediaControls, SmtcMediaControls>();
        services.AddSingleton<IAudioPlayer, LibVlcAudioPlayerService>();
        services.AddSingleton<TaskbarService>();
        services.AddSingleton<JumpListService>();
    }

    /// <summary>
    ///     The main window, plus factories for the pages and windows that services create at runtime: the main shell,
    ///     onboarding, the mini player and the tray popup. Each factory call builds a new instance, which the container
    ///     does not keep, so closing it releases it.
    /// </summary>
    private static void AddShell(IServiceCollection services)
    {
        services.AddSingleton<MainWindow>();
        services.AddSingleton<DispatcherQueue>(sp => sp.GetRequiredService<MainWindow>().DispatcherQueue);
        services.AddSingleton<Func<MainPage>>(sp => () => ActivatorUtilities.CreateInstance<MainPage>(sp));
        services.AddSingleton<Func<OnboardingPage>>(sp => () => ActivatorUtilities.CreateInstance<OnboardingPage>(sp));
        services.AddSingleton<Func<MiniPlayerWindow>>(sp => () => ActivatorUtilities.CreateInstance<MiniPlayerWindow>(sp));
        services.AddSingleton<Func<TrayPopup>>(sp => () => ActivatorUtilities.CreateInstance<TrayPopup>(sp));
    }
}
