using Microsoft.Extensions.DependencyInjection;
using Nagi.Core;
using Nagi.Presentation.Insights;
using Nagi.Presentation.Lists;
using Nagi.Presentation.MusicServers;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Player;
using Nagi.Presentation.Settings;
using Nagi.Presentation.Shell;
using Nagi.Presentation.Theme;

namespace Nagi.Presentation;

/// <summary>
///     Registers the view models and the app services every UI head shares.
/// </summary>
public static class PresentationServiceRegistration
{
    /// <summary>
    ///     Adds settings, navigation and view-model services plus the view models. The UI head must register
    ///     its implementations of the abstractions it provides, such as <see cref="IUIService" />,
    ///     <see cref="IWindowService" />, <see cref="IThemeService" /> and <see cref="INavigationService" />.
    /// </summary>
    public static IServiceCollection AddNagiPresentation(this IServiceCollection services)
    {
        services.AddSettings(typeof(AppSettings));
        services.AddSingleton<IMusicNavigationService, MusicNavigationService>();
        services.AddSingleton<ViewModelFactory>();
        services.AddSingleton<MusicServerPrompts>();
        services.AddSingleton<ThemeCoordinator>();
        services.AddSingleton<WindowBehavior>();
        services.AddSingleton<LaunchCoordinator>();

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
