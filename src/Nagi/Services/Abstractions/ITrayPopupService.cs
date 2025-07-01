namespace Nagi.Services.Abstractions;

/// <summary>
/// Defines a service for managing the visibility of a tray-based popup window.
/// </summary>
public interface ITrayPopupService {
    /// <summary>
    /// Shows the popup if it is hidden, or hides it if it is visible.
    /// </summary>
    void ShowOrHidePopup();
}