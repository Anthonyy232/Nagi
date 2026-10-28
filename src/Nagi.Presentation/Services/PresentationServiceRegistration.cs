using Microsoft.Extensions.DependencyInjection;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Services.Implementations;
using Nagi.Presentation.ViewModels;

namespace Nagi.Presentation.Services;

/// <summary>
///     Registers the view models and the app services every UI head shares.
/// </summary>
public static class PresentationServiceRegistration
{
    /// <summary>
    ///     Adds settings, navigation and view-model services plus the view models. The UI head must register
    ///     its implementations of the other abstractions in <c>Nagi.Presentation.Services.Abstractions</c>.
    /// </summary>
    public static IServiceCollection AddNagiPresentation(this IServiceCollection services)
    {
        services.AddSingleton<SettingsService>();
        services.AddSingleton<IUISettingsService>(sp => sp.GetRequiredService<SettingsService>());
        services.AddSingleton<ISettingsService>(sp => sp.GetRequiredService<SettingsService>());
        services.AddSingleton<IMusicNavigationService, MusicNavigationService>();
        services.AddSingleton<ViewModelFactory>();

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
