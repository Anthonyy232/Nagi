using Nagi.Presentation.Shell;
using Nagi.WinUI.Pages;

namespace Nagi.WinUI.Services.Implementations;

public class ApplicationLifecycle : IApplicationLifecycle
{
    private readonly MainWindow _window;
    private readonly IWindowService _windowService;
    private readonly Func<MainPage> _createMainPage;
    private readonly Func<OnboardingPage> _createOnboardingPage;

    public ApplicationLifecycle(MainWindow window, IWindowService windowService, Func<MainPage> createMainPage,
        Func<OnboardingPage> createOnboardingPage)
    {
        _window = window;
        _windowService = windowService;
        _createMainPage = createMainPage;
        _createOnboardingPage = createOnboardingPage;
    }

    public bool IsShowingOnboarding { get; private set; }

    /// <summary>
    ///     Whether the app starts again once the main window has closed and the services have stopped.
    /// </summary>
    public bool IsRestarting { get; private set; }

    public void ShowMainShell()
    {
        IsShowingOnboarding = false;
        if (_window.Content is not MainPage) _window.SetContent(_createMainPage());
    }

    public void ShowOnboarding()
    {
        IsShowingOnboarding = true;
        if (_window.Content is not OnboardingPage) _window.SetContent(_createOnboardingPage());
    }

    public void Restart()
    {
        IsRestarting = true;
        _windowService.Exit();
    }
}
