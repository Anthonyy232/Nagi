namespace Nagi.Presentation.Services.Abstractions;

public interface IApplicationLifecycle
{
    /// <summary>
    ///     Whether the main window shows first-run onboarding rather than the main shell.
    /// </summary>
    bool IsShowingOnboarding { get; }

    /// <summary>
    ///     Shows the main shell when a library folder exists, or onboarding when there is none.
    /// </summary>
    Task NavigateToMainContentAsync();

    void Restart();
}
