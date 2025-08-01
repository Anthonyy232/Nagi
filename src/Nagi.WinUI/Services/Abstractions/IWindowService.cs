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
    /// Occurs when a property of the main application window has changed, such as its visibility.
    /// </summary>
    event Action<AppWindowChangedEventArgs>? VisibilityChanged;

    /// <summary>
    /// Gets a value indicating whether the main window is currently visible.
    /// </summary>
    bool IsVisible { get; }

    /// <summary>
    /// Gets or sets a value indicating whether the application is in the process of exiting.
    /// This flag helps distinguish between a user-initiated close (e.g., from a tray menu)
    /// and a close action that should be intercepted (e.g., clicking the 'X' button when "hide to tray" is enabled).
    /// </summary>
    bool IsExiting { get; set; }

    /// <summary>
    /// Asynchronously initializes the service.
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Hides the main window and enters an efficiency mode.
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
    /// Minimizes the main window, which may trigger the appearance of the mini-player.
    /// </summary>
    void MinimizeToMiniPlayer();
}