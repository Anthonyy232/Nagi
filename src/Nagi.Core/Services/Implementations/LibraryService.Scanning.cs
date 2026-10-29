using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Constants;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Folder scanning: change detection, rescans, lyrics and cover-art discovery, and ReplayGain analysis.
/// </summary>
public partial class LibraryService
{
    private async Task<IEnumerable<Folder>> GetRootFoldersAsync(CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await context.Folders.AsNoTracking()
            .Where(f => f.ParentFolderId == null)
            .OrderBy(f => f.Name)
            .ThenBy(f => f.Path)
            .ThenBy(f => f.Id)
            .ToListAsync(token).ConfigureAwait(false);
    }

    public async Task ScanFolderForMusicAsync(string folderPath, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var folder = await _libraryReader.GetFolderByPathAsync(folderPath).ConfigureAwait(false) ?? await AddFolderAsync(folderPath).ConfigureAwait(false);
        if (folder is null)
        {
            _logger.LogWarning("Failed to add or find folder for path {FolderPath}, aborting scan.", folderPath);
            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Error_FailedToAddFolder, Percentage = 100 });
            return;
        }

        await RescanFolderForMusicAsync(folder.Id, progress, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> RescanFolderForMusicAsync(Guid folderId, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RescanFolderForMusicAsync(folderId, false, progress, cancellationToken);
    }

    /// <param name="forceFullScan">If true, re-reads metadata for all files regardless of modification time.</param>
    /// <returns>True if changes were made to the library; otherwise, false.</returns>
    public Task<bool> RescanFolderForMusicAsync(Guid folderId, bool forceFullScan, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        CancelActiveScan("manual folder rescan");
        return RescanFolderForMusicAsync(folderId, forceFullScan, allowFolderRemovalOnMissing: true, progress, cancellationToken);
    }

    public async Task<bool> RefreshFolderInBackgroundAsync(Guid folderId, CancellationToken cancellationToken = default)
    {
        var scanned = await RescanFolderForMusicAsync(folderId, false, allowFolderRemovalOnMissing: false,
            cancellationToken: cancellationToken, registerAsActiveScan: false).ConfigureAwait(false);
        if (scanned && await _settingsService.GetVolumeNormalizationEnabledAsync().ConfigureAwait(false))
            await RunReplayGainAnalysisAsync(null, cancellationToken).ConfigureAwait(false);
        return scanned;
    }

    private async Task<bool> RescanFolderForMusicAsync(Guid folderId, bool forceFullScan, bool allowFolderRemovalOnMissing,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool registerAsActiveScan = true,
        bool throwOnFailure = false)
    {
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        var operationToken = operationCts.Token;

        if (registerAsActiveScan)
            SetActiveScanCancellationSource(operationCts);

        try
        {
            await _scanSemaphore.WaitAsync(operationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (registerAsActiveScan)
                ClearActiveScanCancellationSource(operationCts);
            _logger.LogDebug("Scan for folder ID {FolderId} was cancelled before acquiring semaphore.", folderId);
            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_ScanCancelled, Percentage = 100 });
            return false;
        }

        try
        {
            var scanResult = await Task.Run(async () =>
            {
                var folder = await _libraryReader.GetFolderByIdAsync(folderId).ConfigureAwait(false);
                if (folder is null)
                {
                    _logger.LogWarning("Cannot rescan folder: Folder with ID {FolderId} not found.", folderId);
                    progress?.Report(new ScanProgress { StatusText = string.Format(Resources.Strings.Format_NotFound, Resources.Strings.Label_Folder), Percentage = 100 });
                    if (throwOnFailure)
                        throw new InvalidOperationException($"Folder {folderId} was not found.");
                    return false;
                }

                var isNetwork = _fileSystem.IsNetworkPath(folder.Path);
                _logger.LogInformation(
                    "Scanning folder '{FolderName}' (path={FolderPath}, network={IsNetwork}, forceFull={ForceFull}).",
                    folder.Name, folder.Path, isNetwork, forceFullScan);

                try
                {
                    operationToken.ThrowIfCancellationRequested();

                    if (!_fileSystem.DirectoryExists(folder.Path))
                    {
                        if (!allowFolderRemovalOnMissing)
                        {
                            // Auto-refresh/batch path: folder being unreachable (e.g., NAS not mounted
                            // at app launch, removable drive not inserted) must NOT permanently remove
                            // the user's folder from the library. Just warn and skip.
                            _logger.LogWarning(
                                "Folder path '{FolderPath}' not reachable during auto-refresh. Skipping folder {FolderId}; no changes will be made.",
                                folder.Path, folder.Id);
                            progress?.Report(new ScanProgress
                            { StatusText = Resources.Strings.Status_ScanFailed, Percentage = 100 });
                            if (throwOnFailure)
                                throw new DirectoryNotFoundException($"Library folder was not found: {folder.Path}");
                            return false;
                        }

                        _logger.LogWarning(
                            "Folder path '{FolderPath}' no longer exists. Removing folder {FolderId} from library.",
                            folder.Path, folder.Id);
                        progress?.Report(new ScanProgress
                        { StatusText = Resources.Strings.Status_ScanFailed, Percentage = 100 });
                        return await RemoveFolderCoreAsync(folderId, operationToken).ConfigureAwait(false);
                    }

                    progress?.Report(new ScanProgress
                    { StatusText = Resources.Strings.Status_PreparingScanCaches, IsIndeterminate = true });
                    var (filesToAdd, filesToUpdate, filesRemovedFromDisk, unchangedFiles) =
                        await AnalyzeFolderChangesAsync(folderId, folder.Path, forceFullScan, operationToken).ConfigureAwait(false);

                    if (filesToAdd.Any() || filesToUpdate.Any() || filesRemovedFromDisk.Any())
                        _logger.LogInformation("Changes detected: +{New} ~{Updated} -{Removed}",
                            filesToAdd.Count, filesToUpdate.Count, filesRemovedFromDisk.Count);

                    operationToken.ThrowIfCancellationRequested();

                    var allFilePathsToDelete = filesRemovedFromDisk.Distinct().ToList();

                    // Safety guard: if the scan proposes to delete the majority of existing songs
                    // while barely adding any, something upstream failed (e.g., a transient NAS
                    // enumeration failure). Skip the delete pass to avoid data loss; the next
                    // successful scan will reconcile correctly.
                    if (allFilePathsToDelete.Count > 50)
                    {
                        await using var guardContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
                        var existingCount = await guardContext.Songs.AsNoTracking()
                            .CountAsync(s => s.FolderId == folderId, operationToken).ConfigureAwait(false);
                        if (existingCount > 0 &&
                            allFilePathsToDelete.Count > Math.Max(50, existingCount / 2) &&
                            filesToAdd.Count < allFilePathsToDelete.Count / 10)
                        {
                            _logger.LogError(
                                "Scan safety guard tripped for folder {FolderId}: proposed to delete {DeleteCount}/{ExistingCount} songs while only adding {AddCount}. Skipping delete pass; likely a transient enumeration failure (NAS blip / unmounted drive).",
                                folderId, allFilePathsToDelete.Count, existingCount, filesToAdd.Count);
                            allFilePathsToDelete = new List<string>();
                            filesRemovedFromDisk = new List<string>();
                        }
                    }

                    if (allFilePathsToDelete.Any())
                    {
                        progress?.Report(new ScanProgress
                        { StatusText = Resources.Strings.Status_CleaningUpLibrary, IsIndeterminate = true });
                        await using var deleteContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

                        var songsToDeleteQuery = deleteContext.Songs
                            .Where(s => s.FolderId == folderId && allFilePathsToDelete.Contains(s.FilePath));

                        var lrcPathsToDelete = await songsToDeleteQuery
                            .Where(s => s.LrcFilePath != null)
                            .Select(s => s.LrcFilePath!)
                            .ToListAsync(operationToken).ConfigureAwait(false);

                        var albumArtPathsToDelete = await songsToDeleteQuery
                            .Where(s => s.AlbumArtUriFromTrack != null)
                            .Select(s => s.AlbumArtUriFromTrack!)
                            .Distinct()
                            .ToListAsync(operationToken).ConfigureAwait(false);

                        await songsToDeleteQuery.ExecuteDeleteAsync(operationToken).ConfigureAwait(false);

                        DeleteCachedLrcFiles(lrcPathsToDelete);

                        await DeleteUnreferencedCoverArtAsync(deleteContext, albumArtPathsToDelete, operationToken).ConfigureAwait(false);
                    }

                    // Before the folder image check, so it can replace the images that are gone.
                    var clearedArtistImages = await ClearMissingArtistImagesAsync(folderId, operationToken).ConfigureAwait(false);

                    var filesToProcess = filesToAdd.Concat(filesToUpdate).ToList();

                    if (!filesToProcess.Any())
                    {
                        if (allFilePathsToDelete.Any())
                        {
                            progress?.Report(new ScanProgress
                            { StatusText = Resources.Strings.Status_Finalizing, IsIndeterminate = true });
                            await using var cleanupContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
                            DeleteOrphanedArtistImages(await CleanUpOrphanedEntitiesAsync(cleanupContext, operationToken).ConfigureAwait(false));
                        }

                        // Run all independent post-scan checks in parallel (each manages its own DbContext).
                        // No new files were added, so no new directories exist — skip EnsureSubFolders.
                        var lrcTask = UpdateMissingLrcPathsAsync(folderId, operationToken);
                        var coverArtTask = UpdateMissingCoverArtAsync(folderId, folder.Path, operationToken);
                        var artistImageTask = UpdateMissingArtistImagesFromFoldersAsync(folderId, operationToken);

                        await Task.WhenAll(lrcTask, coverArtTask, artistImageTask).ConfigureAwait(false);
                        // After the folder art check, so a track's own folder art wins over its album's cover.
                        var syncedAlbumCovers = await SyncAlbumCoversWithTracksAsync(folderId, operationToken).ConfigureAwait(false);
                        var albumCoverUpdates = await ApplyAlbumCoversToTracksWithoutArtAsync(folderId, operationToken).ConfigureAwait(false);

                        var lrcUpdates = lrcTask.Result;
                        var coverArtUpdates = coverArtTask.Result + albumCoverUpdates;
                        var artistImageUpdates = artistImageTask.Result;

                        var hasChanges = allFilePathsToDelete.Any() || lrcUpdates > 0 || coverArtUpdates > 0 ||
                                         artistImageUpdates > 0 || syncedAlbumCovers > 0 || clearedArtistImages > 0;

                        var updates = new List<string>();
                        if (lrcUpdates > 0)
                            updates.Add(FormatCount(lrcUpdates, Resources.Strings.Format_ScanFoundLyrics_Singular, Resources.Strings.Format_ScanFoundLyrics_Plural));
                        if (coverArtUpdates > 0)
                            updates.Add(FormatCount(coverArtUpdates, Resources.Strings.Format_ScanFoundCoverArt_Singular, Resources.Strings.Format_ScanFoundCoverArt_Plural));
                        if (artistImageUpdates > 0)
                            updates.Add(FormatCount(artistImageUpdates, Resources.Strings.Format_ScanFoundArtistImages_Singular, Resources.Strings.Format_ScanFoundArtistImages_Plural));

                        var statusMessage = updates.Count > 0
                            ? string.Join(" ", [Resources.Strings.Status_ScanComplete, .. updates])
                            : Resources.Strings.Status_ScanCompleteUpToDate;

                        progress?.Report(new ScanProgress
                        { StatusText = statusMessage, Percentage = 100 });
                        if (hasChanges)
                        {
                            LibraryContentChanged?.Invoke(this, new LibraryContentChangedEventArgs(LibraryChangeType.FolderRescanned, folderId));
                        }
                        return hasChanges;
                    }

                    var filePathsBeingUpdated = filesToUpdate.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var (newSongsFound, discoveredDirs, replacedCovers) = await ExtractAndSaveMetadataStreamingAsync(
                        folderId, filesToProcess, folder.Path, filePathsBeingUpdated, unchangedFiles, progress,
                        operationToken).ConfigureAwait(false);

                    operationToken.ThrowIfCancellationRequested();

                    // For pure-add scans, LRC and cover art were already checked during metadata extraction
                    // (AtlMetadataService.GetLrcPathAsync / ProcessCoverArtFromDirectoryAsync) — skip redundant
                    // re-checks. Only run them for rescans where files may have changed since extraction.
                    bool isPureAddScan = filesToUpdate.Count == 0 && filesRemovedFromDisk.Count == 0;

                    // Run all independent post-scan checks in parallel (each manages its own DbContext).
                    // Pass discoveredDirs directly to skip the internal SELECT DISTINCT DirectoryPath query.
                    var postScanTasks = new List<Task>
                    {
                        UpdateMissingArtistImagesFromFoldersAsync(folderId, operationToken),
                        EnsureSubFoldersExistAsync(folderId, folder.Path, discoveredDirs, operationToken)
                    };
                    if (!isPureAddScan)
                    {
                        postScanTasks.Add(UpdateMissingLrcPathsAsync(folderId, operationToken));
                        postScanTasks.Add(UpdateMissingCoverArtAsync(folderId, folder.Path, operationToken));
                    }
                    await Task.WhenAll(postScanTasks).ConfigureAwait(false);
                    if (!isPureAddScan)
                        await SyncAlbumCoversWithTracksAsync(folderId, operationToken).ConfigureAwait(false);
                    await ApplyAlbumCoversToTracksWithoutArtAsync(folderId, operationToken).ConfigureAwait(false);

                    progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_Finalizing, IsIndeterminate = true });

                    // Only clean up orphaned entities if updates or removals may have created them.
                    // Pure adds (InitialScan) cannot produce orphans, so skip the expensive cleanup queries.
                    if (filesRemovedFromDisk.Count > 0 || filesToUpdate.Count > 0)
                    {
                        await using (var finalContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false))
                        {
                            DeleteOrphanedArtistImages(await CleanUpOrphanedEntitiesAsync(finalContext, operationToken).ConfigureAwait(false));
                            // Only after every batch is saved, since songs read later in the scan may reuse a replaced cover.
                            await DeleteUnreferencedCoverArtAsync(finalContext, replacedCovers, operationToken).ConfigureAwait(false);
                        }
                    }

                    var summary = newSongsFound > 0
                        ? FormatCount(newSongsFound, Resources.Strings.Format_ScanAddedSongs_Singular, Resources.Strings.Format_ScanAddedSongs_Plural)
                        : Resources.Strings.Status_ScanCompleteUpToDate;
                    progress?.Report(new ScanProgress { StatusText = summary, Percentage = 100 });
                    LibraryContentChanged?.Invoke(this, new LibraryContentChangedEventArgs(LibraryChangeType.FolderRescanned, folderId));
                    return true;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Scan for folder ID {FolderId} was cancelled.", folderId);
                    progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_ScanCancelled, Percentage = 100 });
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "FATAL: Rescan for folder ID {FolderId} failed.", folderId);
                    progress?.Report(new ScanProgress
                    { StatusText = Resources.Strings.Status_ScanFailed, Percentage = 100 });
                    if (throwOnFailure)
                        throw;
                    return false;
                }
            }, operationToken);

            // Batch refreshes run ReplayGain once after their final folder.
            if (scanResult && registerAsActiveScan && await _settingsService.GetVolumeNormalizationEnabledAsync().ConfigureAwait(false))
            {
                await RunReplayGainAnalysisAsync(progress, operationToken).ConfigureAwait(false);
            }

            if (scanResult)
                _ = StartMetadataBackgroundFetchAsync();

            return scanResult;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Scan for folder ID {FolderId} was cancelled during execution.", folderId);
            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_ScanCancelled, Percentage = 100 });
            return false;
        }
        finally
        {
            if (registerAsActiveScan)
                ClearActiveScanCancellationSource(operationCts);
            _scanSemaphore.Release();
        }
    }

    private static string FormatCount(int count, string singular, string plural) =>
        string.Format(count == 1 ? singular : plural, count.ToString("N0"));

    private async Task RunReplayGainAnalysisAsync(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Volume normalization is enabled. Starting ReplayGain analysis.");

        try
        {
            await _replayGainService.ScanLibraryAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("ReplayGain analysis was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReplayGain analysis failed.");
            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_NormalizationFailed, Percentage = 100 });
        }
    }

    public Task<bool> RefreshAllFoldersAsync(IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RefreshAllFoldersAsync(false, progress, cancellationToken);
    }

    public Task<bool> ForceRescanMetadataAsync(IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RefreshAllFoldersCoreAsync(true, true, progress, cancellationToken);
    }

    public Task<bool> RefreshAllFoldersAsync(bool forceFullScan, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return RefreshAllFoldersCoreAsync(forceFullScan, false, progress, cancellationToken);
    }

    private async Task<bool> RefreshAllFoldersCoreAsync(bool forceFullScan, bool throwOnFailure,
        IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        CancelActiveScan(forceFullScan ? "forced library refresh" : "library refresh");
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        var operationToken = operationCts.Token;

        try
        {
            SetActiveScanCancellationSource(operationCts);

            var folders = (await GetRootFoldersAsync(operationToken).ConfigureAwait(false)).ToList();
            var totalFolders = folders.Count;

            if (totalFolders == 0)
            {
                _logger.LogDebug("No folders found in the library to refresh.");
                progress?.Report(
                    new ScanProgress { StatusText = Resources.Strings.Status_NoFoldersToRefresh, Percentage = 100 });
                return false;
            }

            var anyChangesMade = false;

            foreach (var (folderIndex, folder) in folders.Index())
            {
                operationToken.ThrowIfCancellationRequested();

                var newProgress = new Progress<ScanProgress>(p =>
                {
                    var totalPercentage = (folderIndex + p.Percentage / 100.0) / totalFolders * 100.0;
                    var status = totalFolders > 1
                        ? string.Format(Resources.Strings.Format_ScanFolderProgress, folderIndex + 1, totalFolders, folder.Name, p.StatusText)
                        : p.StatusText;

                    progress?.Report(new ScanProgress
                    {
                        StatusText = status,
                        Percentage = totalPercentage,
                        IsIndeterminate = p.IsIndeterminate,
                    });
                });

                var changes = await RescanFolderForMusicAsync(folder.Id, forceFullScan,
                        allowFolderRemovalOnMissing: false, newProgress, operationToken, registerAsActiveScan: false,
                        throwOnFailure: throwOnFailure)
                    .ConfigureAwait(false);
                operationToken.ThrowIfCancellationRequested();
                anyChangesMade |= changes;
            }

            // Run ReplayGain analysis ONCE after all folders are scanned (if enabled and changes were made)
            if (anyChangesMade && await _settingsService.GetVolumeNormalizationEnabledAsync().ConfigureAwait(false))
            {
                await RunReplayGainAnalysisAsync(progress, operationToken).ConfigureAwait(false);
            }

            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_LibraryRefreshComplete, Percentage = 100 });
            if (anyChangesMade)
            {
                LibraryContentChanged?.Invoke(this, new LibraryContentChangedEventArgs(LibraryChangeType.LibraryRescanned));
            }
            return anyChangesMade;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Library refresh was cancelled.");
            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_ScanCancelled, Percentage = 100 });
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh all library folders.");
            if (throwOnFailure)
                throw;
            return false;
        }
        finally
        {
            ClearActiveScanCancellationSource(operationCts);
        }
    }

    private async Task EnsureSubFoldersExistAsync(Guid rootFolderId, string rootFolderPath,
        HashSet<string> discoveredDirectories, CancellationToken cancellationToken)
    {
        if (discoveredDirectories.Count == 0) return;

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var folders = await context.Folders.ToListAsync(cancellationToken).ConfigureAwait(false);
        var foldersByPath = folders.ToDictionary(f => PathCanonicalizer.NormalizeDirectoryPath(f.Path), StringComparer.OrdinalIgnoreCase);
        var normalizedRootPath = PathCanonicalizer.NormalizeDirectoryPath(rootFolderPath);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Include empty intermediate directories so the browser can reach nested albums.
        foreach (var directory in discoveredDirectories)
        {
            var currentPath = PathCanonicalizer.NormalizeDirectoryPath(directory);
            while (!string.IsNullOrEmpty(currentPath) &&
                   !currentPath.Equals(normalizedRootPath, StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(currentPath);
                var parent = Path.GetDirectoryName(currentPath);
                if (string.IsNullOrEmpty(parent)) break;
                currentPath = PathCanonicalizer.NormalizeDirectoryPath(parent);
            }
        }

        // Parents are shorter than their descendants and must be inserted first.
        foreach (var path in paths.OrderBy(p => p.Length))
        {
            var parentPath = Path.GetDirectoryName(path);
            var parentId = !string.IsNullOrEmpty(parentPath) &&
                           foldersByPath.TryGetValue(PathCanonicalizer.NormalizeDirectoryPath(parentPath), out var parent)
                ? parent.Id
                : rootFolderId;

            if (foldersByPath.TryGetValue(path, out var existing))
            {
                existing.ParentFolderId = parentId;
                continue;
            }

            var folder = new Folder
            {
                Name = Path.GetFileName(path),
                Path = path,
                ParentFolderId = parentId,
                LastModifiedDate = DateTime.UtcNow
            };
            context.Folders.Add(folder);
            foldersByPath.Add(path, folder);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <returns>
    ///     Updates include songs whose cached cover is missing. <c>unchangedFiles</c> are the updates a forced scan
    ///     re-reads although they didn't change on disk, so they keep their art and lyrics.
    /// </returns>
    private async Task<(List<string> filesToAdd, List<string> filesToUpdate, List<string> filesRemovedFromDisk,
            HashSet<string> unchangedFiles)>
        AnalyzeFolderChangesAsync(Guid folderId, string folderPath, bool forceFullScan, CancellationToken cancellationToken)
    {
        await using var analysisContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var dbFiles = await analysisContext.Songs
            .AsNoTracking()
            .Where(s => s.FolderId == folderId)
            .Select(s => new { s.FilePath, s.FileModifiedDate, s.AlbumArtUriFromTrack })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Covers are named by their content, so re-reading a song recreates a deleted cover at its old path.
        var missingCovers = dbFiles.Select(s => s.AlbumArtUriFromTrack).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        missingCovers.RemoveWhere(_fileSystem.FileExists);
        if (missingCovers.Count > 0)
            _logger.LogInformation("Re-reading songs for {Count} missing cached covers.", missingCovers.Count);

        var diskFileMap = _fileSystem.EnumerateFilesWithLastWriteTime(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(x => FileExtensions.MusicFileExtensions.Contains(Path.GetExtension(x.Path)))
            .ToDictionary(x => x.Path, x => x.LastWriteTimeUtc, StringComparer.OrdinalIgnoreCase);

        cancellationToken.ThrowIfCancellationRequested();

        var dbFileMap = dbFiles.ToDictionary(s => s.FilePath, StringComparer.OrdinalIgnoreCase);

        var filesToAdd = new List<string>();
        var filesToUpdate = new List<string>();
        var unchangedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, diskModTime) in diskFileMap)
        {
            if (dbFileMap.TryGetValue(path, out var dbFile))
            {
                if (diskModTime != dbFile.FileModifiedDate ||
                    (dbFile.AlbumArtUriFromTrack is { } cover && missingCovers.Contains(cover)))
                {
                    filesToUpdate.Add(path);
                }
                else if (forceFullScan)
                {
                    filesToUpdate.Add(path);
                    unchangedFiles.Add(path);
                }
            }
            else
            {
                filesToAdd.Add(path);
            }
        }

        var filesRemovedFromDisk = dbFileMap.Keys
            .Where(path => !diskFileMap.ContainsKey(path))
            .ToList();

        return (filesToAdd, filesToUpdate, filesRemovedFromDisk, unchangedFiles);
    }

    /// <summary>
    ///     Scans for songs in the folder that are missing LRC file references but now have
    ///     matching .lrc files on disk. This handles the case where a user adds LRC files
    ///     to an already-scanned folder without modifying the music files themselves.
    /// </summary>
    private async Task<int> UpdateMissingLrcPathsAsync(Guid folderId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var songsWithoutLrc = await context.Songs
            .Where(s => s.FolderId == folderId && s.LrcFilePath == null)
            .Select(s => new { s.Id, s.FilePath })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (songsWithoutLrc.Count == 0)
            return 0;

        // First pass: find LRC files using per-directory caches to avoid redundant filesystem calls.
        // Multiple songs in the same directory share a single GetFiles enumeration.
        var lrcFilesByDir = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var txtFilesByDir = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var songLrcMappings = new Dictionary<Guid, string>();

        foreach (var song in songsWithoutLrc)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directory = Path.GetDirectoryName(song.FilePath);
            if (string.IsNullOrEmpty(directory)) continue;

            var nameWithoutExt = Path.GetFileNameWithoutExtension(song.FilePath);

            if (!lrcFilesByDir.TryGetValue(directory, out var lrcByName))
            {
                lrcByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var p in _fileSystem.GetFiles(directory, "*.lrc"))
                        lrcByName.TryAdd(Path.GetFileNameWithoutExtension(p), p);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error enumerating LRC files in '{Directory}'.", directory);
                }
                lrcFilesByDir[directory] = lrcByName;
            }

            if (lrcByName.TryGetValue(nameWithoutExt, out var lrcPath))
            {
                songLrcMappings[song.Id] = lrcPath;
                continue;
            }

            // Lazy-populate TXT cache for this directory (only when no .lrc found)
            if (!txtFilesByDir.TryGetValue(directory, out var txtByName))
            {
                txtByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var p in _fileSystem.GetFiles(directory, "*.txt"))
                        txtByName.TryAdd(Path.GetFileNameWithoutExtension(p), p);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error enumerating TXT files in '{Directory}'.", directory);
                }
                txtFilesByDir[directory] = txtByName;
            }

            if (txtByName.TryGetValue(nameWithoutExt, out var txtPath))
                songLrcMappings[song.Id] = txtPath;
        }

        if (songLrcMappings.Count == 0)
            return 0;

        // Process in batches to avoid SQL parameter limits and memory issues with large libraries
        const int batchSize = 500;
        var totalUpdated = 0;

        foreach (var batchIds in songLrcMappings.Keys.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var songsToUpdate = await context.Songs
                .Where(s => batchIds.Contains(s.Id))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            foreach (var song in songsToUpdate)
            {
                song.LrcFilePath = songLrcMappings[song.Id];
            }

            totalUpdated += songsToUpdate.Count;
        }

        if (totalUpdated > 0)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Updated {Count} songs with newly discovered LRC files.", totalUpdated);
        }

        return totalUpdated;
    }

    /// <summary>
    ///     Scans for songs in the folder that are missing cover art but now have
    ///     matching cover art files (cover.jpg, folder.png, etc.) on disk in their directory hierarchy.
    ///     This handles the case where a user adds cover art files to an already-scanned folder
    ///     without modifying the music files themselves.
    /// </summary>
    private async Task<int> UpdateMissingCoverArtAsync(Guid folderId, string baseFolderPath, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var songsWithoutCoverArt = await context.Songs
            .Where(s => s.FolderId == folderId && s.AlbumArtUriFromTrack == null)
            .Select(s => new { s.Id, s.FilePath })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (songsWithoutCoverArt.Count == 0)
            return 0;

        _logger.LogDebug("Found {Count} songs without cover art in folder {FolderId}. Searching for cover art files...",
            songsWithoutCoverArt.Count, folderId);

        // Cache processed cover art by directory to avoid redundant processing for songs in same folder
        var directoryCache = new Dictionary<string, (string? uri, string? lightSwatch, string? darkSwatch)?>(StringComparer.OrdinalIgnoreCase);
        var songCoverArtMappings = new Dictionary<Guid, (string? uri, string? lightSwatch, string? darkSwatch)>();

        // Shared per-directory cache for FindCoverArtInDirectory results across all hierarchy walks.
        // Parent directories (e.g., artist folder, root folder) are shared by multiple song directories
        // and would otherwise be scanned once per album directory without this cache.
        var hierarchyDirCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var song in songsWithoutCoverArt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var songDirectory = Path.GetDirectoryName(song.FilePath) ?? string.Empty;

            if (directoryCache.TryGetValue(songDirectory, out var cachedResult))
            {
                if (cachedResult.HasValue)
                {
                    songCoverArtMappings[song.Id] = cachedResult.Value;
                }
                continue;
            }

            var coverArtPath = FindCoverArtInDirectoryHierarchy(song.FilePath, baseFolderPath, hierarchyDirCache);
            if (coverArtPath != null)
            {
                try
                {
                    // Read and process the cover art file directly using the image processor
                    // This is MUCH faster than re-extracting full audio metadata
                    var imageBytes = await _fileSystem.ReadAllBytesAsync(coverArtPath).ConfigureAwait(false);
                    if (imageBytes.Length > 0)
                    {
                        var coverArtResult = await _imageProcessor.SaveCoverArtAndExtractColorsAsync(imageBytes).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(coverArtResult.uri))
                        {
                            var result = (coverArtResult.uri, coverArtResult.lightSwatchId, coverArtResult.darkSwatchId);
                            directoryCache[songDirectory] = result;
                            songCoverArtMappings[song.Id] = result;
                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process cover art file {CoverArtPath} for song {SongId}.",
                        coverArtPath, song.Id);
                }
            }

            directoryCache[songDirectory] = null;
        }

        if (songCoverArtMappings.Count == 0)
            return 0;

        const int batchSize = 500;
        var totalUpdated = 0;

        foreach (var batchIds in songCoverArtMappings.Keys.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var songsToUpdate = await context.Songs
                .Where(s => batchIds.Contains(s.Id))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            foreach (var song in songsToUpdate)
            {
                var coverArtInfo = songCoverArtMappings[song.Id];
                song.AlbumArtUriFromTrack = coverArtInfo.uri;
                song.LightSwatchId = coverArtInfo.lightSwatch;
                song.DarkSwatchId = coverArtInfo.darkSwatch;
            }

            totalUpdated += songsToUpdate.Count;
        }

        if (totalUpdated > 0)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Updated {Count} songs with newly discovered cover art files.", totalUpdated);
        }

        return totalUpdated;
    }

    /// <summary>
    ///     Searches for a cover art file in the directory hierarchy, starting from the song's
    ///     directory and walking up to the base folder path.
    /// </summary>
    private string? FindCoverArtInDirectoryHierarchy(string songFilePath, string? baseFolderPath,
        Dictionary<string, string?> hierarchyDirCache)
    {
        try
        {
            var currentDirectory = Path.GetDirectoryName(songFilePath);
            if (string.IsNullOrEmpty(currentDirectory)) return null;

            var normalizedBasePath = baseFolderPath?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            while (!string.IsNullOrEmpty(currentDirectory))
            {
                if (!hierarchyDirCache.TryGetValue(currentDirectory, out var coverArtPath))
                {
                    coverArtPath = ImageStorageHelper.FindCoverArtInDirectory(_fileSystem, currentDirectory, _logger);
                    hierarchyDirCache.Add(currentDirectory, coverArtPath);
                }

                if (coverArtPath != null) return coverArtPath;

                var normalizedCurrent = currentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.IsNullOrEmpty(normalizedBasePath) &&
                    normalizedCurrent.Equals(normalizedBasePath, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                currentDirectory = Path.GetDirectoryName(currentDirectory);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while searching for cover art in directory hierarchy for '{SongFilePath}'.",
                songFilePath);
            return null;
        }
    }

}
