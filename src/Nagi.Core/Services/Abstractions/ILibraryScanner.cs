using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

public interface ILibraryScanner
{
    event EventHandler<ArtistMetadataUpdatedEventArgs>? ArtistMetadataUpdated;
    event EventHandler<IEnumerable<ArtistMetadataUpdatedEventArgs>>? ArtistMetadataBatchUpdated;
    event EventHandler<LibraryContentChangedEventArgs>? LibraryContentChanged;

    Task ScanFolderForMusicAsync(string folderPath, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<bool> RescanFolderForMusicAsync(Guid folderId, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<bool> RefreshAllFoldersAsync(IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Quietly picks up file changes in one library folder. Unlike a manual rescan it never cancels
    ///     another scan and never removes a folder that is temporarily unreachable.
    /// </summary>
    Task<bool> RefreshFolderInBackgroundAsync(Guid folderId, CancellationToken cancellationToken = default);

    Task<bool> ForceRescanMetadataAsync(IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<Artist?> GetArtistDetailsAsync(Guid artistId, bool allowOnlineFetch, CancellationToken cancellationToken = default);
    /// <summary>
    ///     Fills in missing artist images, biographies and album covers from the enabled online sources.
    ///     Returns at once if online metadata is turned off or a run is already in progress.
    /// </summary>
    Task StartMetadataBackgroundFetchAsync();

    /// <summary>
    ///     Collapses library rows that represent the same physical folder or file under different
    ///     path representations (e.g., mapped drive vs UNC). Returns the number of duplicate rows
    ///     removed. Safe to invoke repeatedly.
    /// </summary>
    Task<int> DeduplicateLibraryAsync(CancellationToken cancellationToken = default);
}
