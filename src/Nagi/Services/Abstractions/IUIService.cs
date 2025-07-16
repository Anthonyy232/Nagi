using System.Threading.Tasks;

namespace Nagi.Services.Abstractions;

/// <summary>
/// Abstracts UI-related operations like showing dialogs or pickers.
/// </summary>
public interface IUIService {
    /// <summary>
    /// Shows a confirmation dialog to the user.
    /// </summary>
    /// <param name="title">The dialog's title.</param>
    /// <param name="content">The main message of the dialog.</param>
    /// <param name="primaryButtonText">The text for the confirmation button.</param>
    /// <param name="closeButtonText">The text for the cancellation button. If null, the button is not shown.</param>
    /// <returns>True if the user confirmed (primary button), false otherwise.</returns>
    Task<bool> ShowConfirmationDialogAsync(string title, string content, string primaryButtonText = "OK", string? closeButtonText = "Cancel");

    /// <summary>
    /// Opens a folder picker dialog for the user to select a single folder.
    /// </summary>
    /// <returns>The path of the selected folder, or null if the user cancelled.</returns>
    Task<string?> PickSingleFolderAsync();

    /// <summary>
    /// Opens the system's file explorer to the directory containing the specified file path.
    /// </summary>
    /// <param name="filePath">The full path to a file within the target directory.</param>
    Task OpenFolderInExplorerAsync(string filePath);
}