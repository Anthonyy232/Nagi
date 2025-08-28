using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Manages high-level application lifecycle events, such as navigation and application reset.
/// </summary>
public class ApplicationLifecycle : IApplicationLifecycle {
    private readonly App _app;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ApplicationLifecycle> _logger;

    public ApplicationLifecycle(App app, IServiceProvider serviceProvider, ILogger<ApplicationLifecycle> logger) {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger;
    }

    /// <summary>
    ///     Navigates to the main content of the application, performing initial setup checks if necessary.
    /// </summary>
    public async Task NavigateToMainContentAsync() {
        _logger.LogInformation("Navigating to main application content.");
        await _app.CheckAndNavigateToMainContent();
    }

    /// <summary>
    ///     Resets the application to its default state by clearing all settings, library data, and the playback queue,
    ///     then navigates to the main content (which may trigger the onboarding process).
    /// </summary>
    public async Task ResetAndNavigateToOnboardingAsync() {
        _logger.LogInformation("Starting application reset process.");
        try {
            var settingsService = _serviceProvider.GetRequiredService<IUISettingsService>();
            await settingsService.ResetToDefaultsAsync();
            _logger.LogDebug("UI settings have been reset.");

            var libraryService = _serviceProvider.GetRequiredService<ILibraryService>();
            await libraryService.ClearAllLibraryDataAsync();
            _logger.LogDebug("Library data has been cleared.");

            var playbackService = _serviceProvider.GetRequiredService<IMusicPlaybackService>();
            await playbackService.ClearQueueAsync();
            _logger.LogDebug("Playback queue has been cleared.");

            await _app.CheckAndNavigateToMainContent();
            _logger.LogInformation("Application reset completed successfully.");
        }
        catch (Exception ex) {
            // A failure during reset is a critical issue that can leave the app in a bad state.
            _logger.LogCritical(ex, "A critical failure occurred during the application reset process.");
            // Re-throw to allow the caller (e.g., a ViewModel) to handle showing an error dialog.
            throw;
        }
    }
}