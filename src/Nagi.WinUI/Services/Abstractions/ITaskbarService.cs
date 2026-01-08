using System;
using System.Threading.Tasks;

namespace Nagi.WinUI.Services.Abstractions;

/// <summary>
///     Provides an abstraction for integrating media playback controls into the Windows taskbar thumbnail.
///     This service manages the thumbnail toolbar buttons (previous, play/pause, next) that appear
///     in the taskbar preview when hovering over the application icon.
/// </summary>
public interface ITaskbarService : IDisposable
{
    /// <summary>
    ///     Initializes the taskbar integration for the specified window.
    ///     This must be called after the window is created and has a valid handle.
    /// </summary>
    /// <param name="windowHandle">The native handle (HWND) of the main application window.</param>
    void Initialize(nint windowHandle);

    /// <summary>
    ///     Processes Windows messages related to taskbar button interactions.
    ///     This should be called from the window's message handler (WndProc).
    /// </summary>
    /// <param name="msg">The message identifier.</param>
    /// <param name="wParam">Additional message information.</param>
    /// <param name="lParam">Additional message information.</param>
    void HandleWindowMessage(int msg, nint wParam, nint lParam);

    /// <summary>
    ///     Refreshes the icons displayed on the taskbar thumbnail buttons.
    ///     This should be called when the theme changes to ensure icons match the current theme.
    ///     The operation is debounced to handle rapid theme changes efficiently.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task RefreshIconsAsync();
}
