// Nagi.Presentation.Services.Abstractions/IWindowService.cs

using System;
using System.Threading.Tasks;

namespace Nagi.Presentation.Services.Abstractions;

public interface IWindowService
{
    bool IsVisible { get; }

    bool IsMiniPlayerActive { get; }

    bool IsMinimized { get; }

    /// <summary>
    ///     Gets or sets a value indicating whether the application is in the process of exiting.
    ///     This flag helps distinguish between a close action that should be intercepted (e.g., hide to tray)
    ///     and a final, user-initiated shutdown (e.g., from a tray menu).
    /// </summary>
    bool IsExiting { get; set; }

    /// <summary>
    ///     Whether the main window is showing first-run onboarding rather than the main shell.
    /// </summary>
    bool IsShowingOnboarding { get; }

    event Action<WindowClosingEventArgs>? Closing;

    /// <summary>
    ///     Occurs when the main window is shown or hidden.
    ///     This is a lower-level event; prefer UIStateChanged for coordinating application logic.
    /// </summary>
    event Action? VisibilityChanged;

    /// <summary>
    ///     Occurs when the overall UI state of the application changes, such as when the main window is hidden,
    ///     minimized, or the mini-player is shown/hidden. This is the preferred event for coordinating logic
    ///     that depends on the window's state.
    /// </summary>
    event Action? UIStateChanged;

    Task InitializeAsync();

    /// <summary>
    ///     Hides the main window, typically to the system tray.
    /// </summary>
    void Hide();

    void ShowAndActivate();

    /// <summary>
    ///     Closes the main application window, which will terminate the application unless the close is canceled.
    /// </summary>
    void Close();

    void ShowMiniPlayer();

    /// <summary>
    ///     Sets the process efficiency mode. The calling coordinator (e.g., PlayerViewModel) is responsible
    ///     for determining when this mode should be enabled or disabled.
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
