namespace Nagi.Presentation.Services.Abstractions;

/// <summary>
///     What the main window shows, and restarting the app.
/// </summary>
public interface IApplicationLifecycle
{
    /// <summary>
    ///     Whether the main window shows first-run onboarding rather than the main shell.
    /// </summary>
    bool IsShowingOnboarding { get; }

    void ShowMainShell();

    void ShowOnboarding();

    void Restart();
}
