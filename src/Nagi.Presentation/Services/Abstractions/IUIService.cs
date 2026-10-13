using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nagi.Presentation.Services.Abstractions;

public enum CrashReportResult
{
    Close,

    Reset
}

public interface IUIService
{
    Task<Nagi.Presentation.Models.PlaybackMixOptions?> ShowPlayForDialogAsync();
    Task<(int Minutes, bool FinishTrack)?> ShowSleepTimerDialogAsync();
    /// <summary>
    ///     Shows a confirmation dialog to the user.
    /// </summary>
    /// <param name="primaryButtonText">The text for the confirmation button.</param>
    /// <param name="closeButtonText">The text for the cancellation button, or null to use the localized Cancel label.</param>
    /// <param name="defaultToCancel">Makes the cancellation button the default, for destructive actions.</param>
    /// <returns>True if the user confirmed (primary button), false otherwise.</returns>
    Task<bool> ShowConfirmationDialogAsync(string title, string content, string? primaryButtonText = null,
        string? closeButtonText = null, bool defaultToCancel = false);

    /// <returns>The path of the selected folder, or null if the user cancelled.</returns>
    Task<string?> PickSingleFolderAsync();

    /// <summary>
    ///     Opens the system's file explorer to the directory containing the specified file path.
    /// </summary>
    /// <param name="filePath">The full path to a file within the target directory.</param>
    Task OpenFolderInExplorerAsync(string filePath);

    /// <summary>
    ///     Opens <paramref name="uri" /> in the default browser.
    /// </summary>
    /// <returns>False if no app could open it.</returns>
    Task<bool> OpenUriAsync(Uri uri);

    /// <summary>
    ///     Shows a simple message dialog with a single "OK" button.
    /// </summary>
    Task ShowMessageDialogAsync(string title, string message);

    /// <summary>
    ///     Shows a dialog informing the user about a previous application crash.
    /// </summary>
    /// <returns>A <see cref="CrashReportResult" /> indicating the user's choice.</returns>
    Task<CrashReportResult> ShowCrashReportDialogAsync(string title, string introduction, string logContent, string githubUrl);

    /// <summary>
    ///     Opens a file open picker dialog that allows selecting a single file.
    /// </summary>
    /// <param name="fileTypes">The file extensions to filter by (e.g., ".zip").</param>
    /// <returns>The path of the selected file, or null if the user cancelled.</returns>
    Task<string?> PickSingleFileAsync(IEnumerable<string> fileTypes);

    /// <summary>
    ///     Opens a file open picker dialog that allows selecting multiple files.
    /// </summary>
    /// <param name="fileTypes">The file extensions to filter by (e.g., ".m3u", ".m3u8").</param>
    /// <returns>The paths of the selected files, or an empty list if the user cancelled.</returns>
    Task<IReadOnlyList<string>> PickOpenMultipleFilesAsync(IEnumerable<string> fileTypes);

    /// <summary>
    ///     Shows the smart playlist editor for a new playlist, or for <paramref name="editing" /> when given.
    /// </summary>
    /// <returns>The created or updated playlist, or null if the user cancelled.</returns>
    Task<Nagi.Core.Models.SmartPlaylist?> ShowSmartPlaylistEditorAsync(Nagi.Core.Models.SmartPlaylist? editing = null);

    /// <summary>
    ///     Asks for the name and optional cover image of a new playlist.
    /// </summary>
    /// <returns>The user's choices, or null if the user cancelled.</returns>
    Task<Nagi.Presentation.Models.NewPlaylistRequest?> ShowCreatePlaylistDialogAsync();

    /// <summary>
    ///     Asks for a new name for the playlist called <paramref name="currentName" />.
    /// </summary>
    /// <returns>The new name, or null if the user cancelled.</returns>
    Task<string?> ShowRenamePlaylistDialogAsync(string currentName);

    /// <summary>
    ///     Shows the lyrics editor with <paramref name="lyrics" />, offering removal when <paramref name="canRemove" /> is set.
    /// </summary>
    /// <returns>The user's choice, or null if the user cancelled.</returns>
    Task<Nagi.Presentation.Models.LyricsEdit?> ShowLyricsEditorAsync(string lyrics, bool canRemove);

    /// <summary>
    ///     Shows a dialog for FFmpeg setup with a recheck button that doesn't close the dialog.
    /// </summary>
    /// <param name="checkAction">An async function that checks if FFmpeg is installed. Returns true if detected.</param>
    /// <returns>True if FFmpeg was detected after a recheck, false if the user cancelled.</returns>
    Task<bool> ShowFFmpegSetupDialogAsync(string title, string instructions, Func<Task<bool>> checkAction);
}
