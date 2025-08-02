// Nagi.WinUI.Services.Abstractions/IWindowService.cs

using System;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;

namespace Nagi.WinUI.Services.Abstractions;

/// <summary>
/// Defines a contract for a service that abstracts and manages interactions with the application's windows.
/// </summary>
public interface IWindowService {
    /// <summary>
    /// Occurs when the main application window is about to close.
    /// </summary>
    event Action<AppWindowClosingEventArgs>? Closing;

    /// <summary>
    /// Occurs when a property of the main application window has changed.
    /// This is a lower-level event; prefer UIStateChanged for coordinating application logic.
    /// </summary>
    event Action<AppWindowChangedEventArgs>? VisibilityChanged;

    /// <summary>
    /// Occurs when the overall UI state of the application changes, such as when the main window is hidden,
    /// minimized, or the mini-player is shown/hidden. This is the preferred event for coordinating logic
    /// that depends on the window's state.
    /// </summary>
    event Action? UIStateChanged;

    /// <summary>
    /// Gets a value indicating whether the main window is currently visible on-screen.
    /// </summary>
    bool IsVisible { get; }

    /// <summary>
    /// Gets a value indicating whether the mini-player window is currently active.
    /// </summary>
    bool IsMiniPlayerActive { get; }

    /// <summary>
    /// Gets a value indicating whether the main window is currently in a standard minimized state.
    /// </summary>
    bool IsMinimized { get; }

    /// <summary>
    /// Gets or sets a value indicating whether the application is in the process of exiting.
    /// This flag helps distinguish between a close action that should be intercepted (e.g., hide to tray)
    /// and a final, user-initiated shutdown (e.g., from a tray menu).
    /// </summary>
    bool IsExiting { get; set; }

    /// <summary>
    /// Asynchronously performs initial setup for the service.
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Hides the main window, typically to the system tray.
    /// </summary>
    void Hide();

    /// <summary>
    /// Shows and activates the main window, bringing it to the foreground.
    /// </summary>
    void ShowAndActivate();

    /// <summary>
    /// Closes the main application window, which will terminate the application unless the close is canceled.
    /// </summary>
    void Close();

    /// <summary>
    /// Minimizes the main window. This action may trigger the mini-player if the corresponding setting is enabled.
    /// </summary>
    void MinimizeToMiniPlayer();

    /// <summary>
    /// Sets the process efficiency mode. The calling coordinator (e.g., PlayerViewModel) is responsible
    /// for determining when this mode should be enabled or disabled.
    /// </summary>
    /// <param name="isEnabled">True to enable efficiency mode; false to disable it.</param>
    void SetEfficiencyMode(bool isEnabled);
}