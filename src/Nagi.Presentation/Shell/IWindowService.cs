namespace Nagi.Presentation.Shell;

/// <summary>
///     The main window and the mini player. <see cref="WindowBehavior" /> decides when to use which.
/// </summary>
public interface IWindowService
{
    bool IsVisible { get; }

    bool IsMiniPlayerActive { get; }

    bool IsMinimized { get; }

    bool IsFullScreen { get; }

    /// <summary>
    ///     True once the main window is closing for good, which exits the app: after <see cref="Exit" />, or after a
    ///     close that no <see cref="Closing" /> handler cancelled.
    /// </summary>
    bool IsExiting { get; }

    /// <summary>
    ///     Raised before the main window closes; a handler can cancel the close.
    /// </summary>
    event Action<WindowClosingEventArgs>? Closing;

    event Action? Minimized;

    /// <summary>
    ///     Raised when the main window is shown, hidden, minimized, restored, enters or leaves full screen, or the
    ///     mini-player opens or closes.
    /// </summary>
    event Action? UIStateChanged;

    /// <summary>
    ///     Hides the main window, typically to the system tray.
    /// </summary>
    void Hide();

    /// <summary>
    ///     Shows, restores and activates the main window, closing the mini player.
    /// </summary>
    void ShowAndActivate();

    /// <summary>
    ///     Shows the main window minimized to the taskbar, without activating it.
    /// </summary>
    void ShowMinimized();

    /// <summary>
    ///     Shows the mini player in place of the hidden or minimized main window, which leaves the taskbar until it is
    ///     shown again. Restoring from the mini player, or closing it, shows the main window.
    /// </summary>
    void ShowMiniPlayer();

    /// <summary>
    ///     Closes the main window and exits the app, bypassing hide-to-tray.
    /// </summary>
    void Exit();

    /// <summary>
    ///     Lowers the process's resource priority while nothing needs it, such as when hidden and not playing.
    ///     Asking for the mode already in effect does nothing.
    /// </summary>
    void SetEfficiencyMode(bool isEnabled);

    void SetFullScreen(bool isFullScreen);
}

/// <summary>
///     Lets a <see cref="IWindowService.Closing" /> handler keep the main window open.
/// </summary>
public sealed class WindowClosingEventArgs : EventArgs
{
    public bool Cancel { get; set; }
}
