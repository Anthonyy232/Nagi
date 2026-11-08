namespace Nagi.Presentation.Shell;

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

    /// <summary>
    ///     Exits the way closing the app does, saving the window and the playback session and stopping the services,
    ///     then starts the app again. It returns before the app has closed.
    /// </summary>
    void Restart();
}
