using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines the contract for scanning folders and fetching online metadata.
/// </summary>
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

    Task<bool> ForceRescanMetadataAsync(IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<Artist?> GetArtistDetailsAsync(Guid artistId, bool allowOnlineFetch, CancellationToken cancellationToken = default);
    Task StartArtistMetadataBackgroundFetchAsync();

    /// <summary>
    ///     Collapses library rows that represent the same physical folder or file under different
    ///     path representations (e.g., mapped drive vs UNC). Returns the number of duplicate rows
    ///     removed. Safe to invoke repeatedly.
    /// </summary>
    Task<int> DeduplicateLibraryAsync(CancellationToken cancellationToken = default);
}
