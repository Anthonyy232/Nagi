using Microsoft.Windows.AppLifecycle;
using Nagi.Presentation.Shell;
using Nagi.WinUI.Pages;

namespace Nagi.WinUI.Services.Implementations;

public class ApplicationLifecycle : IApplicationLifecycle
{
    private readonly MainWindow _window;
    private readonly Func<MainPage> _createMainPage;
    private readonly Func<OnboardingPage> _createOnboardingPage;

    public ApplicationLifecycle(MainWindow window, Func<MainPage> createMainPage,
        Func<OnboardingPage> createOnboardingPage)
    {
        _window = window;
        _createMainPage = createMainPage;
        _createOnboardingPage = createOnboardingPage;
    }

    public bool IsShowingOnboarding { get; private set; }

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

    public void Restart() => AppInstance.Restart("");
}
