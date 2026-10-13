using System;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Nagi.Core.Helpers;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Popups;
using Nagi.WinUI.Services.Abstractions;
using Nagi.WinUI.Services.Implementations;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Services;

/// <summary>
///     Registers the WinUI host's services: HTTP clients, platform implementations of Core
///     abstractions, window and UI services, and view models.
/// </summary>
public static class AppServiceRegistration
{
    public static IServiceCollection AddNagiHttpClients(this IServiceCollection services)
    {
        services.ConfigureHttpClientDefaults(builder => builder.ConfigureHttpClient(client =>
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Nagi/1.0 (+https://github.com/Anthonyy232/Nagi)")));
        services.AddHttpClient("")
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(10),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(5)
            });

        // Image downloads are larger payloads and tolerate slower CDNs; use a longer timeout.
        services.AddHttpClient("ImageDownloader")
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(10),
            });

        return services;
    }

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
    ///     Adds the Windows implementations of Core abstractions plus the window, theme, navigation,
    ///     and dispatcher services. They need the window registered by <see cref="AddNagiMainWindow" />.
    /// </summary>
    public static IServiceCollection AddNagiPlatformServices(this IServiceCollection services)
    {
        services.AddSingleton<IPathConfiguration, PathConfiguration>();
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<ICredentialLockerService, CredentialLockerService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<IUISettingsService>(sp => sp.GetRequiredService<SettingsService>());
        services.AddSingleton<ISettingsService>(sp => sp.GetRequiredService<SettingsService>());

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
        services.AddSingleton<IMusicNavigationService, MusicNavigationService>();
        services.AddSingleton<ITrayPopupService, TrayPopupService>();
        services.AddSingleton<IViewModelFactory, ViewModelFactory>();
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

    public static IServiceCollection AddNagiViewModels(this IServiceCollection services)
    {
        // Global singletons - persist for app lifetime
        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<GlobalOperationViewModel>();
        services.AddSingleton<TrayIconViewModel>();

        // List/Grid ViewModels - Singleton for fast navigation, refresh on data changes
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<PlaylistViewModel>();
        services.AddSingleton<FolderViewModel>();
        services.AddSingleton<ArtistViewModel>();
        services.AddSingleton<AlbumViewModel>();
        services.AddSingleton<GenreViewModel>();
        services.AddSingleton<InsightsViewModel>();

        // Detail/Context ViewModels
        services.AddSingleton<AppearanceSettingsViewModel>();
        services.AddSingleton<AudioSettingsViewModel>();
        services.AddSingleton<LibrarySettingsViewModel>();
        services.AddSingleton<ScrobblingSettingsViewModel>();
        services.AddSingleton<RomanizationPacksSettingsViewModel>();
        services.AddSingleton<DataManagementSettingsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<OnboardingViewModel>();

        return services;
    }
}
