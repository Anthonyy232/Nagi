using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Constants;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;
using Nagi.Core.Http.Pipelines;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Artist details, images, and biographies from local folders and online metadata providers, and the
///     background run that also fills in album covers.
/// </summary>
public partial class LibraryService
{
    public async Task<Artist?> GetArtistDetailsAsync(Guid artistId, bool allowOnlineFetch, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var artist = await context.Artists.AsTracking().FirstOrDefaultAsync(a => a.Id == artistId, cancellationToken).ConfigureAwait(false);

        if (artist is null) return null;

        var needsUpdate = string.IsNullOrWhiteSpace(artist.Biography) ||
                          string.IsNullOrWhiteSpace(artist.LocalImageCachePath);
        // Only fetch if never checked before. Once checked, respect the result.
        var neverChecked = artist.MetadataLastCheckedUtc == null;
        if (allowOnlineFetch && needsUpdate && neverChecked)
        {
            try
            {
                await FetchAndUpdateArtistFromRemoteAsync(context, artist, cancellationToken, saveChanges: true, suppressEvents: false).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to fetch online metadata for artist '{ArtistName}'. Proceeding with local data.", artist.Name);
            }
        }

        return await context.Artists.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == artistId, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartMetadataBackgroundFetchAsync()
    {
        if (_shutdownCts.IsCancellationRequested) return;
        if (!await _settingsService.GetFetchOnlineMetadataEnabledAsync().ConfigureAwait(false)) return;
        if (!await _metadataFetchSemaphore.WaitAsync(0).ConfigureAwait(false))
        {
            _logger.LogDebug("Metadata background fetch already running, skipping.");
            return;
        }

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        _metadataFetchCts = runCts;
        try
        {
            var token = runCts.Token;
            await Task.Run(async () =>
            {
                var enabledProviders = await _settingsService
                    .GetEnabledServiceProvidersAsync(Models.ServiceCategory.Metadata)
                    .ConfigureAwait(false);

                if (enabledProviders.Count == 0)
                {
                    _logger.LogDebug("Metadata background fetch: no providers enabled, exiting.");
                    return;
                }

                await Task.WhenAll(
                    FetchMissingArtistMetadataAsync(enabledProviders, token),
                    FetchMissingAlbumCoversAsync(enabledProviders, token)).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Metadata background fetch was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in metadata background fetch.");
        }
        finally
        {
            _metadataFetchCts = null;
            _metadataFetchSemaphore.Release();
        }
    }

    private async Task FetchMissingArtistMetadataAsync(IReadOnlyList<ServiceProviderSetting> enabledProviders,
        CancellationToken token)
    {
        const int batchSize = 50;
        Guid? afterId = null;
        while (!token.IsCancellationRequested)
        {
            await using var batchContext = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
            var query = batchContext.Artists.Where(a => a.MetadataLastCheckedUtc == null);
            if (afterId.HasValue) query = query.Where(a => a.Id > afterId.Value);
            var artistsInBatch = await query.OrderBy(a => a.Id).Take(batchSize)
                .ToListAsync(token).ConfigureAwait(false);

            if (artistsInBatch.Count == 0) break;
            // A failed lookup stays retryable, but must not loop forever in this run.
            afterId = artistsInBatch[^1].Id;
            var artistIdsToUpdate = artistsInBatch.Select(a => a.Id).ToArray();

            var directoryRows = await batchContext.SongArtists
                .AsNoTracking()
                .Where(sa => artistIdsToUpdate.Contains(sa.ArtistId))
                .Select(sa => new { sa.ArtistId, sa.Song.DirectoryPath })
                .Distinct()
                .ToListAsync(token).ConfigureAwait(false);

            var directoriesByArtist = directoryRows
                .GroupBy(r => r.ArtistId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string?>)g.Select(r => r.DirectoryPath).ToList());

            var pendingUpdates = new List<ArtistMetadataUpdatedEventArgs>();
            var stopwatch = Stopwatch.StartNew();
            int processedCount = 0;

            foreach (var artist in artistsInBatch)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    if (!directoriesByArtist.TryGetValue(artist.Id, out var artistDirectories))
                        artistDirectories = Array.Empty<string?>();

                    var (updated, newImagePath) = await FetchAndUpdateArtistFromRemoteAsync(
                        batchContext, artist, token, saveChanges: false, suppressEvents: true,
                        preloadedEnabledProviders: enabledProviders,
                        preloadedSongDirectories: artistDirectories).ConfigureAwait(false);

                    if (updated)
                    {
                        pendingUpdates.Add(new ArtistMetadataUpdatedEventArgs(artist.Id, newImagePath));
                    }

                    if (++processedCount >= 10 || stopwatch.Elapsed.TotalSeconds >= 5)
                    {
                        await batchContext.SaveChangesAsync(token).ConfigureAwait(false);

                        if (pendingUpdates.Count > 0)
                        {
                            _logger.LogInformation("Saved partial batch of {Count} artists metadata (Time: {Elapsed}s).", pendingUpdates.Count, stopwatch.Elapsed.TotalSeconds.ToString("F1"));
                            ArtistMetadataBatchUpdated?.Invoke(this, pendingUpdates.ToList());
                            pendingUpdates.Clear();
                        }

                        processedCount = 0;
                        stopwatch.Restart();
                    }
                }
                catch (DbUpdateConcurrencyException)
                {
                    _logger.LogWarning(
                        "Concurrency conflict for artist {ArtistId} during background fetch. Ignoring.",
                        artist.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to update artist {ArtistId} in background.", artist.Id);
                }
            }

            if (token.IsCancellationRequested) break;

            // Persist MetadataLastCheckedUtc even when no content was updated.
            await batchContext.SaveChangesAsync(token).ConfigureAwait(false);

            if (pendingUpdates.Count > 0)
            {
                _logger.LogInformation("Saved final batch of {Count} artists metadata.", pendingUpdates.Count);

                ArtistMetadataBatchUpdated?.Invoke(this, pendingUpdates);
            }
        }
    }

    /// <summary>
    ///     Clears the image of artists linked to songs or albums in the given folder whose cached image file is missing,
    ///     so the folder scan and the online lookup look for one again.
    /// </summary>
    /// <returns>The number of artists cleared.</returns>
    private async Task<int> ClearMissingArtistImagesAsync(Guid folderId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var cachedImages = await context.Artists
            .Where(a => a.LocalImageCachePath != null &&
                        (a.SongArtists.Any(sa => sa.Song.FolderId == folderId) ||
                         a.AlbumArtists.Any(aa => aa.Album.Songs.Any(s => s.FolderId == folderId))))
            .Select(a => new { a.Id, Path = a.LocalImageCachePath! })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var artistIds = cachedImages.Where(a => !_fileSystem.FileExists(a.Path)).Select(a => a.Id).ToList();
        if (artistIds.Count == 0) return 0;

        await context.Artists.Where(a => artistIds.Contains(a.Id))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(a => a.LocalImageCachePath, (string?)null)
                .SetProperty(a => a.MetadataLastCheckedUtc, (DateTime?)null), cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("Cleared the missing cached image of {Count} artists.", artistIds.Count);
        foreach (var artistId in artistIds)
            ArtistMetadataUpdated?.Invoke(this, new ArtistMetadataUpdatedEventArgs(artistId, null));
        return artistIds.Count;
    }

    /// <summary>
    ///     Finds artists linked to songs in the given folder that have no local or custom image,
    ///     then searches their associated directories for artist image files (e.g., artist.jpg).
    ///     Updates each artist's <see cref="Artist.LocalImageCachePath"/> and fires
    ///     <see cref="ArtistMetadataUpdated"/> when an image is found.
    /// </summary>
    /// <returns>The number of artists updated with newly found local images.</returns>
    private async Task<int> UpdateMissingArtistImagesFromFoldersAsync(Guid folderId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Find distinct artists linked to songs in this folder.
        // Skip if a custom image or a previously-found local folder image is already cached.
        // We DO NOT skip if they only have a .fetched (external) image, as local files take precedence.
        var artistsNeedingImages = await context.Artists
            .Where(a => a.SongArtists.Any(sa => sa.Song.FolderId == folderId))
            .Where(a => a.LocalImageCachePath == null ||
                        (!a.LocalImageCachePath.Contains(".local.") && !a.LocalImageCachePath.Contains(".custom.")))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (artistsNeedingImages.Count == 0) return 0;

        _logger.LogDebug("Checking local artist images for {Count} artists in folder {FolderId}.",
            artistsNeedingImages.Count, folderId);

        // Batch-load song directories for all target artists in a single query (avoids N+1 round-trips).
        var artistIds = artistsNeedingImages.Select(a => a.Id).ToList();
        var songDirPairs = await context.SongArtists
            .AsNoTracking()
            .Where(sa => artistIds.Contains(sa.ArtistId))
            .Select(sa => new { sa.ArtistId, sa.Song.DirectoryPath })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var songDirsByArtistId = songDirPairs
            .GroupBy(x => x.ArtistId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string?>)g.Select(x => x.DirectoryPath).ToList());

        // Cache processed directories to avoid redundant GetFiles calls when multiple artists share a directory.
        var directoryCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // Collect events to fire after SaveChangesAsync succeeds, so UI state and DB stay in sync.
        var pendingEvents = new List<ArtistMetadataUpdatedEventArgs>();
        foreach (var artist in artistsNeedingImages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dirs = songDirsByArtistId.TryGetValue(artist.Id, out var d) ? d : Array.Empty<string?>();
            var localImageBytes = await FindArtistImageInDirectoriesAsync(dirs, artist.Name, cancellationToken, directoryCache).ConfigureAwait(false);
            if (localImageBytes == null) continue;

            try
            {
                var processed = await _imageProcessor.ProcessImageBytesAsync(localImageBytes).ConfigureAwait(false);
                var localPath = await ImageStorageHelper.SaveImageBytesAsync(_fileSystem,
                    _pathConfig.ArtistImageCachePath, artist.Id.ToString(), ".local", processed).ConfigureAwait(false);
                if (string.IsNullOrEmpty(localPath)) continue;

                artist.LocalImageCachePath = localPath;
                pendingEvents.Add(new ArtistMetadataUpdatedEventArgs(artist.Id, localPath));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process local artist image for '{ArtistName}'.", artist.Name);
            }
        }

        if (pendingEvents.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Updated {Count} artists with local folder images.", pendingEvents.Count);
            // Fire events only after the DB write succeeds to keep UI state consistent with persisted data.
            foreach (var eventArgs in pendingEvents)
                ArtistMetadataUpdated?.Invoke(this, eventArgs);
        }

        return pendingEvents.Count;
    }

    private string? FindArtistImageInDirectory(string directory)
    {
        try
        {
            return _fileSystem.GetFiles(directory, "*.*").FirstOrDefault(filePath =>
                FileExtensions.ImageFileExtensions.Contains(Path.GetExtension(filePath)) &&
                Path.GetFileNameWithoutExtension(filePath)
                    .Equals(FileExtensions.ArtistImageFileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate artist images in directory '{Directory}'.", directory);
            return null;
        }
    }

    /// <summary>
    ///     Searches for an artist image file in the folders associated with the artist's songs.
    ///     Queries the database for song directories, then delegates to
    ///     <see cref="FindArtistImageInDirectoriesAsync"/>. Used by single-artist code paths.
    /// </summary>
    private async Task<byte[]?> FindArtistImageInFoldersAsync(Artist artist, MusicDbContext context, CancellationToken ct)
    {
        var songDirectories = await context.Songs
            .AsNoTracking()
            .Where(s => s.SongArtists.Any(sa => sa.ArtistId == artist.Id))
            .Select(s => s.DirectoryPath)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return await FindArtistImageInDirectoriesAsync(songDirectories, artist.Name, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Core artist-image scanning logic. Takes a pre-loaded list of song directories and
    ///     searches them in two passes following Navidrome priority:
    ///     Pass A — parent directories ("artist folder", e.g. F:/Music/Daft Punk/);
    ///     Pass B — song directories themselves ("album folder", e.g. F:/Music/Daft Punk/Discovery/).
    ///     Root directories are excluded from pass A to avoid scanning drive roots.
    /// </summary>
    private Task<byte[]?> FindArtistImageInDirectoriesAsync(
        IReadOnlyList<string?> songDirectories, string artistName, CancellationToken ct, Dictionary<string, string?>? directoryCache = null)
    {
        var directories = songDirectories.OfType<string>().Where(dir => dir.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var parents = directories.Select(Path.GetDirectoryName).OfType<string>()
            .Where(parent => parent.Length > 0 && !string.IsNullOrEmpty(Path.GetDirectoryName(parent)));
        return ScanDirectoriesForArtistImageAsync(parents.Concat(directories).Distinct(StringComparer.OrdinalIgnoreCase),
            artistName, ct, directoryCache);
    }

    private async Task<byte[]?> ScanDirectoriesForArtistImageAsync(IEnumerable<string> directories, string artistName, CancellationToken ct, Dictionary<string, string?>? directoryCache = null)
    {
        foreach (var dir in directories)
        {
            ct.ThrowIfCancellationRequested();

            if (directoryCache == null || !directoryCache.TryGetValue(dir, out var imagePath))
            {
                imagePath = FindArtistImageInDirectory(dir);
                if (directoryCache != null)
                {
                    directoryCache[dir] = imagePath;
                }
            }

            if (imagePath == null) continue;

            try
            {
                var bytes = await _fileSystem.ReadAllBytesAsync(imagePath).ConfigureAwait(false);
                if (bytes.Length > 0)
                {
                    _logger.LogDebug("Found artist image for '{ArtistName}' at '{ImagePath}'.", artistName, imagePath);
                    return bytes;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read artist image at '{ImagePath}'.", imagePath);
            }
        }
        return null;
    }

    public async Task<bool> UpdateArtistImageAsync(Guid artistId, string localFilePath)
    {
        if (string.IsNullOrWhiteSpace(localFilePath) || !_fileSystem.FileExists(localFilePath)) return false;

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var artist = await context.Artists.FindAsync(artistId).ConfigureAwait(false);
        if (artist == null) return false;

        try
        {
            var cachePath = _pathConfig.ArtistImageCachePath;

            var originalBytes = await _fileSystem.ReadAllBytesAsync(localFilePath).ConfigureAwait(false);
            var processedBytes = await _imageProcessor.ProcessImageBytesAsync(originalBytes).ConfigureAwait(false);
            var newPath = await ImageStorageHelper.SaveImageBytesAsync(_fileSystem, cachePath,
                artistId.ToString(), ".custom", processedBytes).ConfigureAwait(false);
            if (newPath is null) return false;

            artist.LocalImageCachePath = newPath;
            await context.SaveChangesAsync().ConfigureAwait(false);

            ArtistMetadataUpdated?.Invoke(this, new ArtistMetadataUpdatedEventArgs(artist.Id, artist.LocalImageCachePath));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update custom image for artist {ArtistName} (Id: {ArtistId})", artist.Name, artistId);
            return false;
        }
    }

    public async Task<bool> RemoveArtistImageAsync(Guid artistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var artist = await context.Artists.FindAsync(artistId).ConfigureAwait(false);
        if (artist == null) return false;

        try
        {
            var cachePath = _pathConfig.ArtistImageCachePath;

            ImageStorageHelper.DeleteImage(_fileSystem, cachePath, artistId.ToString(), ".custom");
            artist.LocalImageCachePath = ImageStorageHelper.FindImage(_fileSystem, cachePath, artistId.ToString(), ".local")
                                         ?? ImageStorageHelper.FindImage(_fileSystem, cachePath, artistId.ToString(), ".fetched");

            await context.SaveChangesAsync().ConfigureAwait(false);
            ArtistMetadataUpdated?.Invoke(this, new ArtistMetadataUpdatedEventArgs(artist.Id, artist.LocalImageCachePath));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove image for artist {ArtistName} (Id: {ArtistId})", artist.Name, artistId);
            return false;
        }
    }

    private async Task<(bool Updated, string? NewImagePath)> FetchAndUpdateArtistFromRemoteAsync(
        MusicDbContext context,
        Artist artist,
        CancellationToken cancellationToken = default,
        bool saveChanges = true,
        bool suppressEvents = false,
        IReadOnlyList<ServiceProviderSetting>? preloadedEnabledProviders = null,
        IReadOnlyList<string?>? preloadedSongDirectories = null)
    {
        var wasMetadataFoundAndUpdated = false;

        // Navidrome priority: local folder images first (artist.*, album/artist.*), then external providers.
        // Skip if a custom image or a previously-found local folder image is already cached.
        // We DO NOT skip if they only have a .fetched (external) image, as local files take precedence.
        if (!ImageStorageHelper.IsUserOrFolderImage(artist.LocalImageCachePath))
        {
            var localImageBytes = preloadedSongDirectories is not null
                ? await FindArtistImageInDirectoriesAsync(preloadedSongDirectories, artist.Name, cancellationToken).ConfigureAwait(false)
                : await FindArtistImageInFoldersAsync(artist, context, cancellationToken).ConfigureAwait(false);
            if (localImageBytes != null)
            {
                try
                {
                    var processed = await _imageProcessor.ProcessImageBytesAsync(localImageBytes).ConfigureAwait(false);
                    var localPath = await ImageStorageHelper.SaveImageBytesAsync(_fileSystem,
                        _pathConfig.ArtistImageCachePath, artist.Id.ToString(), ".local", processed).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(localPath))
                    {
                        artist.LocalImageCachePath = localPath;
                        wasMetadataFoundAndUpdated = true;
                        _logger.LogInformation("Using local folder image for artist '{ArtistName}'.", artist.Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process local artist image for '{ArtistName}'.", artist.Name);
                }
            }
        }

        // Get enabled metadata providers in priority order. Callers in batch paths preload this
        // once for the whole run to avoid repeated settings-store reads + list rebuilds per artist.
        var enabledProviders = preloadedEnabledProviders
            ?? await _settingsService.GetEnabledServiceProvidersAsync(Models.ServiceCategory.Metadata).ConfigureAwait(false);

        if (enabledProviders.Count == 0)
        {
            _logger.LogDebug("No metadata providers enabled. Skipping remote fetch for artist '{ArtistName}'.", artist.Name);
            if (wasMetadataFoundAndUpdated && saveChanges)
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (!suppressEvents && wasMetadataFoundAndUpdated)
                ArtistMetadataUpdated?.Invoke(this, new ArtistMetadataUpdatedEventArgs(artist.Id, artist.LocalImageCachePath));
            return (wasMetadataFoundAndUpdated, artist.LocalImageCachePath);
        }

        var found = await _artistMetadataService
            .FindAsync(artist.Name, artist.MusicBrainzId, enabledProviders, cancellationToken).ConfigureAwait(false);
        var isConclusive = found.IsConclusive;

        if (found.MusicBrainzId is not null)
        {
            artist.MusicBrainzId = found.MusicBrainzId;
            wasMetadataFoundAndUpdated = true;
        }

        if (!string.IsNullOrEmpty(found.Biography))
        {
            artist.Biography = found.Biography;
            wasMetadataFoundAndUpdated = true;
        }

        // Download and cache the best image found from external providers.
        // Skip if a local folder image or custom image is already set.
        if (!string.IsNullOrEmpty(found.ImageUrl) &&
            !ImageStorageHelper.IsUserOrFolderImage(artist.LocalImageCachePath))
        {
            var downloadedPath =
                await DownloadAndCacheArtistImageAsync(artist, new Uri(found.ImageUrl), cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(downloadedPath))
            {
                artist.LocalImageCachePath = downloadedPath;
                wasMetadataFoundAndUpdated = true;
            }
            else
            {
                isConclusive = false;
            }
        }

        // Incomplete metadata remains retryable after a provider or image-download failure.
        var stampChecked = isConclusive ||
            (!string.IsNullOrWhiteSpace(artist.Biography) && !string.IsNullOrWhiteSpace(artist.LocalImageCachePath));
        if (stampChecked)
        {
            artist.MetadataLastCheckedUtc = DateTime.UtcNow;
        }
        else
        {
            _logger.LogDebug(
                "Metadata lookup incomplete for '{ArtistName}'; leaving MetadataLastCheckedUtc null for retry.",
                artist.Name);
        }

        if (saveChanges && (wasMetadataFoundAndUpdated || stampChecked))
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!suppressEvents && wasMetadataFoundAndUpdated)
        {
            ArtistMetadataUpdated?.Invoke(this, new ArtistMetadataUpdatedEventArgs(artist.Id, artist.LocalImageCachePath));
        }

        return (wasMetadataFoundAndUpdated, artist.LocalImageCachePath);
    }

    private async Task<string?> DownloadAndCacheArtistImageAsync(Artist artist, Uri imageUrl, CancellationToken cancellationToken)
    {
        var artistId = artist.Id;
        var lazyTask = _artistImageProcessingTasks.GetOrAdd(artistId, _ =>
            new Lazy<Task<string?>>(() => DownloadAndWriteImageInternalAsync(artistId, imageUrl, cancellationToken)));
        try
        {
            return await lazyTask.Value.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Artist image download failed for artist '{ArtistName}'.", artist.Name);
            return null;
        }
        finally
        {
            _artistImageProcessingTasks.TryRemove(new KeyValuePair<Guid, Lazy<Task<string?>>>(artistId, lazyTask));
        }
    }

    private async Task<string?> DownloadAndWriteImageInternalAsync(Guid artistId, Uri imageUrl, CancellationToken cancellationToken)
    {
        var localPath = Path.Combine(_pathConfig.ArtistImageCachePath, $"{artistId}.fetched.jpg");
        if (_fileSystem.FileExists(localPath)) return localPath;

        var imageBytes = await DownloadImageBytesAsync(imageUrl, cancellationToken).ConfigureAwait(false);
        if (imageBytes is null) return null;

        var processedBytes = await Task.Run(() => _imageProcessor.ProcessImageBytesAsync(imageBytes), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await ImageStorageHelper.SaveImageBytesAsync(_fileSystem, _pathConfig.ArtistImageCachePath,
            artistId.ToString(), ".fetched", processedBytes).ConfigureAwait(false);
    }

    private async Task<byte[]?> DownloadImageBytesAsync(Uri imageUrl, CancellationToken cancellationToken)
    {
        using var httpClient = _httpClientFactory.CreateClient("ImageDownloader");

        return await _pipelines.ExecuteWithFallbackAsync<byte[]?>(
            ServiceProviderIds.ImageDownload,
            async ct =>
            {
                _logger.LogDebug("Downloading image: {ImageUrl}", imageUrl);
                return await httpClient.GetAsync(imageUrl, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Image download returned {Status} for {ImageUrl}", response.StatusCode, imageUrl);
                    return null;
                }

                return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            },
            fallback: null,
            _logger,
            $"image download {imageUrl}",
            cancellationToken).ConfigureAwait(false);
    }

    private void OnFetchOnlineMetadataEnabledChanged(bool isEnabled)
    {
        if (!isEnabled) CancelMetadataFetch();
    }

    private void CancelMetadataFetch()
    {
        var runCts = _metadataFetchCts;
        try
        {
            runCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished and disposed its token source after we captured it.
        }
    }
}
