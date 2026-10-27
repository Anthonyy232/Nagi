using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Nagi.Audio.LibVlc;
using Nagi.Core.Helpers;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Popups;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.Services.Implementations;

namespace Nagi.WinUI.Services;

/// <summary>
///     Registers the WinUI host's services: the Windows implementations of the Core and
///     Presentation abstractions, and the window and page factories.
/// </summary>
public static class AppServiceRegistration
{
    /// <summary>
    ///     Registers <paramref name="mainWindow" /> and its dispatcher queue, which the window, theme,
    ///     dialog and dispatcher services work with.
    /// </summary>
    public static IServiceCollection AddNagiMainWindow(this IServiceCollection services, MainWindow mainWindow)
    {
        services.AddSingleton(mainWindow);
        services.AddSingleton(mainWindow.DispatcherQueue);
        return services;
    }

    /// <summary>
    ///     Adds the Windows implementations of the Core and Presentation abstractions: storage, window,
    ///     theme, navigation, dialogs, dispatcher, tray and audio. They need the window registered by
    ///     <see cref="AddNagiMainWindow" />.
    /// </summary>
    public static IServiceCollection AddNagiPlatformServices(this IServiceCollection services)
    {
        services.AddSingleton<IPathConfiguration>(_ => PackagedAppData.CreatePathConfiguration());
        services.AddSingleton<ISettingsStore, ApplicationDataSettingsStore>();
        services.AddSingleton<ICredentialLockerService, CredentialLockerService>();
        services.AddSingleton<IAutoLaunchService, StartupTaskAutoLaunchService>();

        services.AddSingleton<IWin32InteropService, Win32InteropService>();
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<IUIService, UIService>();
        services.AddSingleton<IDispatcherService, DispatcherService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IApplicationLifecycle, ApplicationLifecycle>();
        services.AddSingleton<IAppInfoService, AppInfoService>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<TransparencyEffectsMonitor>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());
        services.AddSingleton<ITrayPopupService, TrayPopupService>();
        services.AddSingleton<ISystemMediaControls, SmtcMediaControls>();
        services.AddSingleton<IAudioPlayer, LibVlcAudioPlayerService>();
        services.AddTransient<ITaskbarService, TaskbarService>();

        return services;
    }

    /// <summary>
    ///     Registers factories for the windows and pages that services create at runtime: the main shell,
    ///     onboarding, the mini player and the tray popup. Each call builds a new instance with its
    ///     dependencies from the container, which does not keep it, so closing it releases it.
    /// </summary>
    public static IServiceCollection AddNagiShell(this IServiceCollection services)
    {
        services.AddSingleton<Func<MainPage>>(sp => () => ActivatorUtilities.CreateInstance<MainPage>(sp));
        services.AddSingleton<Func<OnboardingPage>>(sp => () => ActivatorUtilities.CreateInstance<OnboardingPage>(sp));
        services.AddSingleton<Func<MiniPlayerWindow>>(sp => () => ActivatorUtilities.CreateInstance<MiniPlayerWindow>(sp));
        services.AddSingleton<Func<ElementTheme, TrayPopup>>(sp =>
            theme => ActivatorUtilities.CreateInstance<TrayPopup>(sp, theme));
        return services;
    }
}
