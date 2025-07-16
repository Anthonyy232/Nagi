using System;
using System.Threading.Tasks;
using Nagi.Models;
using Nagi.Services.Implementations;

namespace Nagi.Services.Abstractions;

/// <summary>
/// Defines the contract for scanning folders and fetching online metadata.
/// </summary>
public interface ILibraryScanner {
    event EventHandler<ArtistMetadataUpdatedEventArgs>? ArtistMetadataUpdated;
    Task ScanFolderForMusicAsync(string folderPath, IProgress<ScanProgress>? progress = null);
    Task<bool> RescanFolderForMusicAsync(Guid folderId, IProgress<ScanProgress>? progress = null);
    Task<bool> RefreshAllFoldersAsync(IProgress<ScanProgress>? progress = null);
    Task<Artist?> GetArtistDetailsAsync(Guid artistId, bool allowOnlineFetch);
    Task StartArtistMetadataBackgroundFetchAsync();
}