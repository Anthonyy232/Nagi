using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     The library's folders on disk: adding and removing them, and scanning them for songs.
/// </summary>
public interface ILibraryFolderService
{
    /// <summary>
    ///     Adds the folder without scanning it.
    /// </summary>
    /// <returns>The existing folder when the path is already in the library.</returns>
    /// <exception cref="FolderOverlapsException">The path is inside, or contains, a folder already in the library.</exception>
    Task<Folder?> AddFolderAsync(string path, string? name = null);

    /// <summary>
    ///     Removes a folder on disk and its songs. A music server is removed with <see cref="IMusicServerService.RemoveAsync" />,
    ///     which also signs out of it.
    /// </summary>
    /// <returns>False if the folder isn't a folder on disk in the library or couldn't be removed.</returns>
    Task<bool> RemoveFolderAsync(Guid folderId);

    /// <summary>
    ///     Reads the folder's new and changed files, cancelling any other scan the user started. A folder that no longer
    ///     exists is removed from the library.
    /// </summary>
    /// <returns>True if the library changed.</returns>
    Task<bool> RescanFolderForMusicAsync(Guid folderId, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Quietly picks up file changes in one folder. Unlike <see cref="RescanFolderForMusicAsync" /> it never cancels
    ///     another scan and never removes a folder that is temporarily unreachable.
    /// </summary>
    /// <returns>True if the library changed.</returns>
    Task<bool> RefreshFolderInBackgroundAsync(Guid folderId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Rescans every folder on disk. A folder that can't be reached is left as it is.
    /// </summary>
    /// <returns>True if the library changed.</returns>
    Task<bool> RefreshAllFoldersAsync(IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads every file in every folder on disk again, such as after the artist split characters changed.
    /// </summary>
    /// <exception cref="Exception">A folder couldn't be scanned.</exception>
    Task<bool> ForceRescanMetadataAsync(IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Collapses library rows that represent the same physical folder or file under different
    ///     path representations (e.g., mapped drive vs UNC). Returns the number of duplicate rows
    ///     removed. Safe to invoke repeatedly.
    /// </summary>
    Task<int> DeduplicateLibraryAsync(CancellationToken cancellationToken = default);
}
