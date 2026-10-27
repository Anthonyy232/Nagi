using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Services.Abstractions;
using Nagi.WinUI.Pages;
using Microsoft.Windows.AppLifecycle;

namespace Nagi.WinUI.Services.Implementations;

public class ApplicationLifecycle : IApplicationLifecycle
{
    private readonly MainWindow _window;
    private readonly IWindowService _windowService;
    private readonly ILibraryReader _libraryReader;
    private readonly IUISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly Func<MainPage> _createMainPage;
    private readonly Func<OnboardingPage> _createOnboardingPage;
    private readonly ILogger<ApplicationLifecycle> _logger;

    public ApplicationLifecycle(MainWindow window, IWindowService windowService, ILibraryReader libraryReader,
        IUISettingsService settingsService, IThemeService themeService, Func<MainPage> createMainPage,
        Func<OnboardingPage> createOnboardingPage, ILogger<ApplicationLifecycle> logger)
    {
        _createMainPage = createMainPage;
        _createOnboardingPage = createOnboardingPage;
        _window = window;
        _windowService = windowService;
        _libraryReader = libraryReader;
        _settingsService = settingsService;
        _themeService = themeService;
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

    public void Restart() => AppInstance.Restart("");
}
