using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
/// Manages high-level application lifecycle events, such as navigation and application reset.
/// </summary>
public class ApplicationLifecycle : IApplicationLifecycle {
    private readonly App _app;
    private readonly IServiceProvider _serviceProvider;

    public ApplicationLifecycle(App app, IServiceProvider serviceProvider) {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <summary>
    /// Navigates to the main content of the application, performing initial setup checks if necessary.
    /// </summary>
    public async Task NavigateToMainContentAsync() {
        await _app.CheckAndNavigateToMainContent();
    }

    /// <summary>
    /// Resets the application to its default state by clearing all settings, library data, and the playback queue,
    /// then navigates to the main content (which may trigger the onboarding process).
    /// </summary>
    public async Task ResetAndNavigateToOnboardingAsync() {
        try {
            var settingsService = _serviceProvider.GetRequiredService<IUISettingsService>();
            await settingsService.ResetToDefaultsAsync();

            var libraryService = _serviceProvider.GetRequiredService<ILibraryService>();
            await libraryService.ClearAllLibraryDataAsync();

            var playbackService = _serviceProvider.GetRequiredService<IMusicPlaybackService>();
            await playbackService.ClearQueueAsync();

            await _app.CheckAndNavigateToMainContent();
        }
        catch (Exception ex) {
            Debug.WriteLine($"[CRITICAL] Application reset failed. Error: {ex.Message}\n{ex.StackTrace}");
            // Re-throw to allow the caller (e.g., a ViewModel) to handle showing an error dialog.
            throw;
        }
    }
}