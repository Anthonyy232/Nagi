using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines the contract for scanning folders and fetching online metadata.
/// </summary>
public interface ILibraryScanner
{
    event EventHandler<ArtistMetadataUpdatedEventArgs>? ArtistMetadataUpdated;
    event EventHandler<bool>? ScanCompleted;

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
}