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
    private readonly Func<MainPage> _createMainPage;
    private readonly Func<OnboardingPage> _createOnboardingPage;
    private readonly ILogger<ApplicationLifecycle> _logger;

    public ApplicationLifecycle(MainWindow window, IWindowService windowService, ILibraryReader libraryReader,
        Func<MainPage> createMainPage, Func<OnboardingPage> createOnboardingPage, ILogger<ApplicationLifecycle> logger)
    {
        _createMainPage = createMainPage;
        _createOnboardingPage = createOnboardingPage;
        _window = window;
        _windowService = windowService;
        _libraryReader = libraryReader;
        _logger = logger;
    }

    public bool IsShowingOnboarding { get; private set; }

    public async Task NavigateToMainContentAsync()
    {
        if (_windowService.IsExiting) return;
        _logger.LogInformation("Navigating to main application content.");

        var hasFolders = await _libraryReader.HasAnyFolderAsync();
        if (_windowService.IsExiting) return;
        if (hasFolders)
        {
            IsShowingOnboarding = false;
            if (_window.Content is not MainPage) _window.SetContent(_createMainPage());
        }
        else
        {
            IsShowingOnboarding = true;
            if (_window.Content is not OnboardingPage) _window.SetContent(_createOnboardingPage());
        }
    }

    public void Restart() => AppInstance.Restart("");
}
