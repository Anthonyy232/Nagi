using System.Diagnostics;
using System.Globalization;
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
///     Artist details, images, and biographies from local folders and online metadata providers.
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

    public async Task StartArtistMetadataBackgroundFetchAsync()
    {
        if (_shutdownCts.IsCancellationRequested) return;
        if (!await _metadataFetchSemaphore.WaitAsync(0).ConfigureAwait(false))
        {
            _logger.LogDebug("Artist metadata background fetch already running, skipping.");
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
                    _logger.LogDebug("Artist metadata background fetch: no providers enabled, exiting.");
                    return;
                }

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
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Artist metadata background fetch was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in artist metadata background fetch.");
        }
        finally
        {
            _metadataFetchCts = null;
            _metadataFetchSemaphore.Release();
        }
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
        if (artist.LocalImageCachePath == null ||
            (!artist.LocalImageCachePath.Contains(".custom.") && !artist.LocalImageCachePath.Contains(".local.")))
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

        _logger.LogDebug("Using metadata providers for '{ArtistName}': {Providers}",
            artist.Name, string.Join(", ", enabledProviders.Select(p => p.Id)));

        var enabledIds = enabledProviders.Select(p => p.Id).ToHashSet();

        var tasks = new Dictionary<string, Task<(bool IsConclusive, string? ImageUrl, string? Biography)>>();

        var mbid = artist.MusicBrainzId;
        Task<ServiceResult<string>>? musicBrainzTask = null;
        if (string.IsNullOrEmpty(mbid) && enabledIds.Contains(ServiceProviderIds.MusicBrainz))
        {
            musicBrainzTask = _musicBrainzService.SearchArtistAsync(artist.Name, cancellationToken);
        }

        // Name-based providers can start before MusicBrainz resolves the ID needed by the others.
        if (enabledIds.Contains(ServiceProviderIds.LastFm))
            tasks[ServiceProviderIds.LastFm] = FetchFromLastFmAsync(artist.Name, cancellationToken);
        if (enabledIds.Contains(ServiceProviderIds.Deezer))
            tasks[ServiceProviderIds.Deezer] = FetchFromDeezerAsync(artist.Name, cancellationToken);

        var anyProviderConclusive = false;
        var allProvidersConclusive = true;

        string? finalImageUrl = null;
        string? finalBiography = null;
        try
        {
            if (musicBrainzTask != null)
            {
                try
                {
                    var result = await musicBrainzTask.ConfigureAwait(false);
                    anyProviderConclusive = result.IsConclusive;
                    allProvidersConclusive = result.IsConclusive;
                    var resolvedMbid = result.Data;
                    if (!string.IsNullOrEmpty(resolvedMbid))
                    {
                        mbid = resolvedMbid;
                        artist.MusicBrainzId = mbid;
                        wasMetadataFoundAndUpdated = true;
                        _logger.LogInformation("Resolved MusicBrainz ID {MBID} for artist '{ArtistName}'", mbid, artist.Name);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    allProvidersConclusive = false;
                    _logger.LogDebug(ex, "MusicBrainz lookup failed for '{ArtistName}'.", artist.Name);
                }
            }

            if (!string.IsNullOrEmpty(mbid))
            {
                if (enabledIds.Contains(ServiceProviderIds.TheAudioDb))
                    tasks[ServiceProviderIds.TheAudioDb] = FetchFromTheAudioDbAsync(mbid, cancellationToken);
                if (enabledIds.Contains(ServiceProviderIds.FanartTv))
                    tasks[ServiceProviderIds.FanartTv] = FetchFromFanartTvAsync(mbid, cancellationToken);
                if (enabledIds.Contains(ServiceProviderIds.Wikipedia))
                    tasks[ServiceProviderIds.Wikipedia] = FetchFromWikipediaAsync(mbid, cancellationToken);
            }

            foreach (var provider in enabledProviders)
            {
                if (!tasks.TryGetValue(provider.Id, out var task)) continue;

                try
                {
                    // Sequential await in priority order. If a higher priority task is still running,
                    // we wait for it. If it finishes and has data, we break early and ignore lower priority ones.
                    var (isConclusive, imageUrl, biography) = await task.ConfigureAwait(false);
                    anyProviderConclusive |= isConclusive;
                    allProvidersConclusive &= isConclusive;

                    if (string.IsNullOrEmpty(finalImageUrl) && !string.IsNullOrEmpty(imageUrl))
                    {
                        finalImageUrl = imageUrl;
                        _logger.LogDebug("Using image from {Provider} for artist '{ArtistName}'.", provider.DisplayName, artist.Name);
                    }

                    if (string.IsNullOrEmpty(finalBiography) && !string.IsNullOrEmpty(biography))
                    {
                        finalBiography = biography;
                        _logger.LogDebug("Using biography from {Provider} for artist '{ArtistName}'.", provider.DisplayName, artist.Name);
                    }

                    if (!string.IsNullOrEmpty(finalImageUrl) && !string.IsNullOrEmpty(finalBiography))
                        break;
                }
                catch (OperationCanceledException)
                {
                    allProvidersConclusive = false;
                    if (cancellationToken.IsCancellationRequested) break;
                }
                catch (Exception ex)
                {
                    allProvidersConclusive = false;
                    _logger.LogDebug(ex, "Metadata provider {Provider} failed for '{ArtistName}'.", provider.DisplayName, artist.Name);
                }
            }
        }
        finally
        {
            // Observe unfinished providers even when cancellation interrupts the identity lookup.
            foreach (var task in tasks.Values)
            {
                _ = task.ContinueWith(
                    static (t, state) => ((ILogger<LibraryService>)state!).LogDebug(
                        t.Exception!.InnerException, "Metadata provider task faulted after lookup stopped waiting"),
                    _logger,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrEmpty(finalBiography))
        {
            artist.Biography = finalBiography;
            wasMetadataFoundAndUpdated = true;
        }

        // Download and cache the best image found from external providers.
        // Skip if a local folder image or custom image is already set.
        if (!string.IsNullOrEmpty(finalImageUrl) &&
            (artist.LocalImageCachePath == null || (!artist.LocalImageCachePath.Contains(".custom.") && !artist.LocalImageCachePath.Contains(".local."))))
        {
            var downloadedPath =
                await DownloadAndCacheArtistImageAsync(artist, new Uri(finalImageUrl), cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(downloadedPath))
            {
                artist.LocalImageCachePath = downloadedPath;
                wasMetadataFoundAndUpdated = true;
            }
            else
            {
                allProvidersConclusive = false;
            }
        }

        // Incomplete metadata remains retryable after a provider or image-download failure.
        var stampChecked = (anyProviderConclusive && allProvidersConclusive) ||
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

    private async Task<(bool IsConclusive, string? ImageUrl, string? Biography)> FetchFromTheAudioDbAsync(string mbid, CancellationToken ct)
    {
        var result = await _theAudioDbService.GetArtistMetadataAsync(mbid, CultureInfo.CurrentCulture.TwoLetterISOLanguageName, ct).ConfigureAwait(false);
        if (result.Status == ServiceResultStatus.Success && result.Data is not null)
        {
            // Prefer square thumbnail over landscape fanart for round frame display
            var url = result.Data.ThumbUrl ?? result.Data.FanartUrl;
            return (true, url, result.Data.Biography);
        }
        return (result.IsConclusive, null, null);
    }

    private async Task<(bool IsConclusive, string? ImageUrl, string? Biography)> FetchFromFanartTvAsync(string mbid, CancellationToken ct)
    {
        var result = await _fanartTvService.GetArtistImagesAsync(mbid, ct).ConfigureAwait(false);
        if (result.Status == ServiceResultStatus.Success && result.Data is not null)
        {
            // Prefer square thumbnail over landscape background for round frame display
            var url = result.Data.ThumbUrl ?? result.Data.BackgroundUrl;
            return (true, url, null);
        }
        return (result.IsConclusive, null, null);
    }

    private async Task<(bool IsConclusive, string? ImageUrl, string? Biography)> FetchFromLastFmAsync(string artistName, CancellationToken ct)
    {
        var result = await _lastFmService.GetArtistInfoAsync(artistName, CultureInfo.CurrentCulture.TwoLetterISOLanguageName, ct).ConfigureAwait(false);
        if (result.Status == ServiceResultStatus.Success && result.Data is not null)
        {
            return (true, result.Data.ImageUrl, result.Data.Biography);
        }
        return (result.IsConclusive, null, null);
    }

    private async Task<(bool IsConclusive, string? ImageUrl, string? Biography)> FetchFromWikipediaAsync(string mbid, CancellationToken ct)
    {
        var result = await _wikipediaService.GetArtistBiographyAsync(mbid, CultureInfo.CurrentCulture.TwoLetterISOLanguageName, ct).ConfigureAwait(false);
        return (result.IsConclusive, null, result.Data);
    }

    private async Task<(bool IsConclusive, string? ImageUrl, string? Biography)> FetchFromDeezerAsync(string artistName, CancellationToken ct)
    {
        var result = await _deezerService.GetArtistImageUrlAsync(artistName, ct).ConfigureAwait(false);
        return (result.IsConclusive, result.Data, null);
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

        using var httpClient = _httpClientFactory.CreateClient("ImageDownloader");

        return await _pipelines.ExecuteWithFallbackAsync<string?>(
            ServiceProviderIds.ImageDownload,
            async ct =>
            {
                _logger.LogDebug("Downloading artist image: {ImageUrl}", imageUrl);
                return await httpClient.GetAsync(imageUrl, ct).ConfigureAwait(false);
            },
            async (response, ct) =>
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Image download returned {Status} for {ImageUrl}", response.StatusCode, imageUrl);
                    return null;
                }

                var imageBytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                // Process image to standardized size (600px max, preserves aspect ratio).
                // Offload heavy image processing to a background thread to prevent blocking.
                var processedBytes = await Task.Run(() => _imageProcessor.ProcessImageBytesAsync(imageBytes), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return await ImageStorageHelper.SaveImageBytesAsync(_fileSystem, _pathConfig.ArtistImageCachePath,
                    artistId.ToString(), ".fetched", processedBytes).ConfigureAwait(false);
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
