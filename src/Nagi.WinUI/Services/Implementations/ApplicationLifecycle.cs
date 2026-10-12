using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Pages;
using Nagi.WinUI.Services.Abstractions;
using Microsoft.Windows.AppLifecycle;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Manages high-level application lifecycle events, such as navigation and application reset.
/// </summary>
public class ApplicationLifecycle : IApplicationLifecycle
{
    private readonly MainWindow _window;
    private readonly IWindowService _windowService;
    private readonly ILibraryReader _libraryReader;
    private readonly ILibraryWriter _libraryWriter;
    private readonly IUISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly IMusicPlaybackService _playbackService;
    private readonly Func<MainPage> _createMainPage;
    private readonly Func<OnboardingPage> _createOnboardingPage;
    private readonly ILogger<ApplicationLifecycle> _logger;

    public ApplicationLifecycle(MainWindow window, IWindowService windowService, ILibraryReader libraryReader,
        ILibraryWriter libraryWriter, IUISettingsService settingsService, IThemeService themeService,
        IMusicPlaybackService playbackService, Func<MainPage> createMainPage,
        Func<OnboardingPage> createOnboardingPage, ILogger<ApplicationLifecycle> logger)
    {
        _createMainPage = createMainPage;
        _createOnboardingPage = createOnboardingPage;
        _window = window;
        _windowService = windowService;
        _libraryReader = libraryReader;
        _libraryWriter = libraryWriter;
        _settingsService = settingsService;
        _themeService = themeService;
        _playbackService = playbackService;
        _logger = logger;
    }

    /// <summary>
    ///     Shows the main shell when a music library has been configured, or onboarding when it has not,
    ///     then applies the saved theme to the new content.
    /// </summary>
    public async Task NavigateToMainContentAsync()
    {
        if (_windowService.IsExiting) return;
        _logger.LogInformation("Navigating to main application content.");

        var hasFolders = await _libraryReader.HasAnyFolderAsync();
        if (_windowService.IsExiting) return;
        if (hasFolders)
        {
            if (_window.Content is not MainPage) _window.Content = _createMainPage();
        }
        else
        {
            if (_window.Content is not OnboardingPage) _window.Content = _createOnboardingPage();
        }

        var savedTheme = await _settingsService.GetThemeAsync();
        _themeService.ApplyTheme(savedTheme);

        _window.NotifyContentLoaded();
        _window.InitializeCustomTitleBar();
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
            await _settingsService.ResetToDefaultsAsync();
            _logger.LogDebug("UI settings have been reset.");

            await _libraryWriter.ClearAllLibraryDataAsync();
            _logger.LogDebug("Library data has been cleared.");

            await _playbackService.ClearQueueAsync();
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
