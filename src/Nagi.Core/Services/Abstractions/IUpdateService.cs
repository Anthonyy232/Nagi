namespace Nagi.Core.Services.Abstractions;

/// <summary>
/// Defines the contract for a service that handles application updates.
/// </summary>
public interface IUpdateService {
    /// <summary>
    /// Checks for updates on application startup, respecting user settings.
    /// This method is designed to run in the background and may show a dialog if an update is found.
    /// </summary>
    Task CheckForUpdatesOnStartupAsync();

    /// <summary>
    /// Manually triggers an update check.
    /// This method should provide UI feedback to the user, such as dialogs for success, failure, or no updates found.
    /// </summary>
    Task CheckForUpdatesManuallyAsync();
}