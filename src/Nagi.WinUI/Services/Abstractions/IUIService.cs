using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nagi.WinUI.Services.Abstractions;

/// <summary>
///     Represents the user's choice in an update dialog.
/// </summary>
public enum UpdateDialogResult
{
    /// <summary>
    ///     The user chose to install the update.
    /// </summary>
    Install,

    /// <summary>
    ///     The user chose to be reminded later.
    /// </summary>
    RemindLater,

    /// <summary>
    ///     The user chose to skip the current update version.
    /// </summary>
    Skip
}

/// <summary>
///     Abstracts UI-related operations like showing dialogs or pickers.
/// </summary>
public interface IUIService
{
    /// <summary>
    ///     Shows a confirmation dialog to the user.
    /// </summary>
    /// <param name="title">The dialog's title.</param>
    /// <param name="content">The main message of the dialog.</param>
    /// <param name="primaryButtonText">The text for the confirmation button.</param>
    /// <param name="closeButtonText">The text for the cancellation button. If null, the button is not shown.</param>
    /// <returns>True if the user confirmed (primary button), false otherwise.</returns>
    Task<bool> ShowConfirmationDialogAsync(string title, string content, string primaryButtonText = "OK",
        string? closeButtonText = "Cancel");

    /// <summary>
    ///     Opens a folder picker dialog for the user to select a single folder.
    /// </summary>
    /// <returns>The path of the selected folder, or null if the user cancelled.</returns>
    Task<string?> PickSingleFolderAsync();

    /// <summary>
    ///     Opens the system's file explorer to the directory containing the specified file path.
    /// </summary>
    /// <param name="filePath">The full path to a file within the target directory.</param>
    Task OpenFolderInExplorerAsync(string filePath);

    /// <summary>
    ///     Shows a dialog specifically for application updates with three choices.
    /// </summary>
    /// <param name="title">The dialog's title.</param>
    /// <param name="content">The main message of the dialog.</param>
    /// <param name="primaryButtonText">Text for the "install" action.</param>
    /// <param name="secondaryButtonText">Text for the "remind later" action.</param>
    /// <param name="closeButtonText">Text for the "skip version" action.</param>
    /// <returns>An <see cref="UpdateDialogResult" /> indicating the user's choice.</returns>
    Task<UpdateDialogResult> ShowUpdateDialogAsync(string title, string content, string primaryButtonText,
        string secondaryButtonText, string closeButtonText);

    /// <summary>
    ///     Shows a simple message dialog with a single "OK" button.
    /// </summary>
    /// <param name="title">The dialog's title.</param>
    /// <param name="message">The message to display.</param>
    Task ShowMessageDialogAsync(string title, string message);

    /// <summary>
    ///     Shows a dialog informing the user about a previous application crash.
    /// </summary>
    /// <param name="title">The dialog's title.</param>
    /// <param name="introduction">The introductory message to the user.</param>
    /// <param name="logContent">The content of the log file to display.</param>
    /// <param name="githubUrl">The URL to the GitHub issues page.</param>
    Task ShowCrashReportDialogAsync(string title, string introduction, string logContent, string githubUrl);

    /// <summary>
    ///     Opens a file open picker dialog that allows selecting multiple files.
    /// </summary>
    /// <param name="fileTypes">The file extensions to filter by (e.g., ".m3u", ".m3u8").</param>
    /// <returns>The paths of the selected files, or an empty list if the user cancelled.</returns>
    Task<IReadOnlyList<string>> PickOpenMultipleFilesAsync(IEnumerable<string> fileTypes);
}