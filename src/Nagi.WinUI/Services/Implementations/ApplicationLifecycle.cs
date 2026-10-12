using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Services.Abstractions;
using Microsoft.Windows.AppLifecycle;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Manages high-level application lifecycle events, such as navigation and application reset.
/// </summary>
public class ApplicationLifecycle : IApplicationLifecycle
{
    private readonly App _app;
    private readonly ILogger<ApplicationLifecycle> _logger;
    private readonly IServiceProvider _serviceProvider;

    public ApplicationLifecycle(App app, IServiceProvider serviceProvider, ILogger<ApplicationLifecycle> logger)
    {
        _app = app;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    ///     Navigates to the main content of the application, performing initial setup checks if necessary.
    /// </summary>
    public Task NavigateToMainContentAsync()
    {
        _logger.LogInformation("Navigating to main application content.");
        return _app.CheckAndNavigateToMainContent();
    }

    /// <summary>
    ///     Resets the application to its default state by clearing all settings, library data, and the playback queue,
    ///     then restarts the application.
    /// </summary>
    public async Task ResetAndRestartAsync()
    {
        _logger.LogInformation("Starting application reset process.");
        try
        {
            var settingsService = _serviceProvider.GetRequiredService<IUISettingsService>();
            await settingsService.ResetToDefaultsAsync();
            _logger.LogDebug("UI settings have been reset.");

            var libraryWriter = _serviceProvider.GetRequiredService<ILibraryWriter>();
            await libraryWriter.ClearAllLibraryDataAsync();
            _logger.LogDebug("Library data has been cleared.");

            var playbackService = _serviceProvider.GetRequiredService<IMusicPlaybackService>();
            await playbackService.ClearQueueAsync();
            _logger.LogDebug("Playback queue has been cleared.");

            _logger.LogInformation("Application reset completed successfully. Restarting...");
            AppInstance.Restart("");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "A critical failure occurred during the application reset process.");
            throw;
        }
    }
}
