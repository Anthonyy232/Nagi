using System.Threading.Tasks;

namespace Nagi.Presentation.Services.Abstractions;

public interface IWindowService
{
    bool IsVisible { get; }

    bool IsMiniPlayerActive { get; }

    bool IsMinimized { get; }

    /// <summary>
    ///     True once the main window is closing for good: after <see cref="Exit" />, or after a close that no
    ///     <see cref="Closing" /> handler cancelled.
    /// </summary>
    bool IsExiting { get; }

    /// <summary>
    ///     Whether the main window is showing first-run onboarding rather than the main shell.
    /// </summary>
    bool IsShowingOnboarding { get; }

    /// <summary>
    ///     Raised before the main window closes; a handler can cancel the close.
    /// </summary>
    event Action<WindowClosingEventArgs>? Closing;

    /// <summary>
    ///     Raised when the main window is shown, hidden, minimized or restored, or the mini-player opens or closes.
    /// </summary>
    event Action? UIStateChanged;

    Task InitializeAsync();

    /// <summary>
    ///     Hides the main window, typically to the system tray.
    /// </summary>
    void Hide();

    void ShowAndActivate();

    /// <summary>
    ///     Closes the main window and exits the app, bypassing hide-to-tray.
    /// </summary>
    void Exit();

    void ShowMiniPlayer();

    /// <summary>
    ///     Lowers the process's resource priority while nothing needs it, such as when hidden and not playing.
    /// </summary>
    void SetEfficiencyMode(bool isEnabled);
}

/// <summary>
///     Lets a <see cref="IWindowService.Closing" /> handler keep the main window open.
/// </summary>
public sealed class WindowClosingEventArgs
{
    public bool Cancel { get; set; }
}
