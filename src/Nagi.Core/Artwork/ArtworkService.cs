using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Library;
using Nagi.Core.Metadata;
using Nagi.Core.OnlineProviders;
using Nagi.Core.OnlineProviders.Pipelines;
using Nagi.Core.Platform;
using Nagi.Core.Settings;

namespace Nagi.Core.Artwork;

public sealed class ArtworkService : IArtworkService
{
    internal const string ImageHttpClientName = "ImageDownloader";

    private readonly ConcurrentDictionary<Guid, Lazy<Task<string?>>> _artistImageDownloads = new();
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly IFileSystemService _fileSystem;
    private readonly IPathConfiguration _pathConfig;
    private readonly IImageProcessor _imageProcessor;
    private readonly IArtistMetadataService _artistMetadataService;
    private readonly IAlbumCoverService _albumCoverService;
    private readonly ISettingsService _settingsService;
    private readonly ProviderClient _imageDownloads;
    private readonly LibraryNotifier _notifier;
    private readonly LibraryWriteLock _writeLock;
    private readonly ILogger<ArtworkService> _logger;
    private readonly SemaphoreSlim _metadataFetchSemaphore = new(1, 1);
    private volatile CancellationTokenSource? _metadataFetchCts;

    public ArtworkService(
        IDbContextFactory<MusicDbContext> contextFactory,
        IFileSystemService fileSystem,
        IPathConfiguration pathConfig,
        IImageProcessor imageProcessor,
        IArtistMetadataService artistMetadataService,
        IAlbumCoverService albumCoverService,
        ISettingsService settingsService,
        IHttpClientFactory httpClientFactory,
        ProviderPipelineProvider pipelines,
        LibraryNotifier notifier,
        LibraryWriteLock writeLock,
        ILogger<ArtworkService> logger)
    {
        _contextFactory = contextFactory;
        _fileSystem = fileSystem;
        _pathConfig = pathConfig;
        _imageProcessor = imageProcessor;
        _artistMetadataService = artistMetadataService;
        _albumCoverService = albumCoverService;
        _settingsService = settingsService;
        _imageDownloads = new ProviderClient(ProviderCatalog.ImageDownload, httpClientFactory.CreateClient(ImageHttpClientName),
            pipelines, logger);
        _notifier = notifier;
        _writeLock = writeLock;
        _logger = logger;
    }

    private CancellationToken ShutdownToken => _writeLock.ShutdownToken;

    public async Task<Artist?> GetArtistDetailsAsync(Guid artistId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var artist = await context.Artists.AsTracking().FirstOrDefaultAsync(a => a.Id == artistId, cancellationToken).ConfigureAwait(false);

        if (artist is null) return null;

        var needsUpdate = string.IsNullOrWhiteSpace(artist.Biography) ||
                          string.IsNullOrWhiteSpace(artist.LocalImageCachePath);
        // Once looked up, the result stands, so an artist with nothing to find isn't looked up on every visit.
        if (needsUpdate && artist.MetadataLastCheckedUtc == null)
        {
            try
            {
                var songDirectories = (await LoadSongDirectoriesByArtistAsync(context, [artist.Id], cancellationToken)
                        .ConfigureAwait(false)).GetValueOrDefault(artist.Id, []);
                var (_, updated) = await FetchArtistMetadataAsync(artist, songDirectories, cancellationToken).ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (updated) _notifier.RaiseArtistImagesChanged([new ArtistImageChange(artist.Id, artist.LocalImageCachePath)]);
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
        if (ShutdownToken.IsCancellationRequested) return;
        if (!_settingsService.Get(CoreSettings.FetchOnlineMetadata)) return;
        if (!await _metadataFetchSemaphore.WaitAsync(0).ConfigureAwait(false))
        {
            _logger.LogDebug("Metadata background fetch already running, skipping.");
            return;
        }

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ShutdownToken);
        _metadataFetchCts = runCts;
        try
        {
            var token = runCts.Token;
            await Task.Run(() => Task.WhenAll(FetchMissingArtistMetadataAsync(token), FetchMissingAlbumCoversAsync(token)),
                token).ConfigureAwait(false);
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

    /// <summary>
    ///     Cancels a running metadata fetch and keeps a new one from starting until <paramref name="action" /> finishes,
    ///     so the fetch can't write images or covers for a library that is being cleared.
    /// </summary>
    internal async Task RunWithoutMetadataFetchAsync(Func<Task> action)
    {
        try
        {
            _metadataFetchCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished and disposed its token source after it was read.
        }

        await _metadataFetchSemaphore.WaitAsync(ShutdownToken).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _metadataFetchSemaphore.Release();
        }
    }

    public async Task<bool> UpdateArtistImageAsync(Guid artistId, string localFilePath)
    {
        if (string.IsNullOrWhiteSpace(localFilePath) || !_fileSystem.FileExists(localFilePath)) return false;

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var artist = await context.Artists.FindAsync(artistId).ConfigureAwait(false);
        if (artist == null) return false;

        try
        {
            var originalBytes = await _fileSystem.ReadAllBytesAsync(localFilePath).ConfigureAwait(false);
            var processedBytes = await _imageProcessor.ProcessImageBytesAsync(originalBytes).ConfigureAwait(false);
            var newPath = await ImageStorageHelper.SaveImageBytesAsync(_fileSystem, _pathConfig.ArtistImageCachePath,
                artistId.ToString(), CachedImageKind.Custom, processedBytes).ConfigureAwait(false);
            if (newPath is null) return false;

            artist.LocalImageCachePath = newPath;
            await context.SaveChangesAsync().ConfigureAwait(false);

            _notifier.RaiseArtistImagesChanged([new ArtistImageChange(artist.Id, artist.LocalImageCachePath)]);
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

            ImageStorageHelper.DeleteImage(_fileSystem, cachePath, artistId.ToString(), CachedImageKind.Custom);
            artist.LocalImageCachePath = ImageStorageHelper.FindImage(_fileSystem, cachePath, artistId.ToString(), CachedImageKind.Folder)
                                         ?? ImageStorageHelper.FindImage(_fileSystem, cachePath, artistId.ToString(), CachedImageKind.Fetched);

            await context.SaveChangesAsync().ConfigureAwait(false);
            _notifier.RaiseArtistImagesChanged([new ArtistImageChange(artist.Id, artist.LocalImageCachePath)]);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove image for artist {ArtistName} (Id: {ArtistId})", artist.Name, artistId);
            return false;
        }
    }

    /// <summary>
    ///     Clears the image of artists linked to songs or albums in the given folder whose cached image file is missing,
    ///     so the folder scan and the online lookup look for one again.
    /// </summary>
    /// <returns>The number of artists cleared.</returns>
    internal async Task<int> ClearMissingArtistImagesAsync(Guid folderId, CancellationToken cancellationToken)
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
        _notifier.RaiseArtistImagesChanged(artistIds.Select(id => new ArtistImageChange(id, null)).ToList());
        return artistIds.Count;
    }

    /// <summary>Looks for an artist image (such as artist.jpg) beside the songs in the folder.</summary>
    /// <returns>The number of artists updated with newly found local images.</returns>
    internal async Task<int> UpdateMissingArtistImagesFromFoldersAsync(Guid folderId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // A folder image replaces a fetched one, but never a custom image or an earlier folder image.
        var artistsNeedingImages = await context.Artists
            .Where(a => a.SongArtists.Any(sa => sa.Song.FolderId == folderId))
            .Where(ImageStorageHelper.HasNoUserOrFolderImage())
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (artistsNeedingImages.Count == 0) return 0;

        _logger.LogDebug("Checking local artist images for {Count} artists in folder {FolderId}.",
            artistsNeedingImages.Count, folderId);

        var songDirsByArtistId = await LoadSongDirectoriesByArtistAsync(context,
            artistsNeedingImages.Select(a => a.Id).ToList(), cancellationToken).ConfigureAwait(false);

        // Artists often share directories, which are then listed once.
        var directoryCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var changes = new List<ArtistImageChange>();
        foreach (var artist in artistsNeedingImages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dirs = songDirsByArtistId.GetValueOrDefault(artist.Id, []);
            if (await UseFolderImageAsync(artist, dirs, cancellationToken, directoryCache).ConfigureAwait(false))
                changes.Add(new ArtistImageChange(artist.Id, artist.LocalImageCachePath));
        }

        if (changes.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Updated {Count} artists with local folder images.", changes.Count);
            _notifier.RaiseArtistImagesChanged(changes);
        }

        return changes.Count;
    }

    internal void DeleteArtistImages(IEnumerable<Guid> artistIds)
    {
        if (!_fileSystem.DirectoryExists(_pathConfig.ArtistImageCachePath)) return;
        foreach (var artistId in artistIds)
        {
            try
            {
                // Every kind, plus the {id}.jpg images versions before the kinds saved.
                foreach (var path in _fileSystem.GetFiles(_pathConfig.ArtistImageCachePath, $"{artistId}.*"))
                    _fileSystem.DeleteFile(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete images for removed artist {ArtistId}.", artistId);
            }
        }
    }

    /// <summary>
    ///     Brings album covers in line with their tracks after the folder's songs changed: an album whose cover none of
    ///     its tracks use takes its first track's art, tracks without art take their album's cover, and covers that
    ///     nothing uses any more are deleted. Runs after the folder art check, so a track's own art wins over its
    ///     album's cover.
    /// </summary>
    /// <param name="replacedCovers">Covers songs used before they were read again, to delete once unused.</param>
    /// <param name="keepAlbumCovers">Whether albums keep their covers, because songs were only added to them.</param>
    /// <returns>The number of albums and tracks that got another cover.</returns>
    internal async Task<(int Albums, int Tracks)> UpdateAlbumCoversAsync(Guid folderId, IEnumerable<string> replacedCovers,
        bool keepAlbumCovers, CancellationToken cancellationToken)
    {
        var albums = keepAlbumCovers ? 0 : await SyncAlbumCoversWithTracksAsync(folderId, cancellationToken).ConfigureAwait(false);
        var tracks = await ApplyAlbumCoversToTracksWithoutArtAsync(folderId, cancellationToken).ConfigureAwait(false);
        // Only after every song is saved, since songs read later may reuse a replaced cover.
        await DeleteUnreferencedCoverArtAsync(replacedCovers, cancellationToken).ConfigureAwait(false);
        return (albums, tracks);
    }

    /// <summary>
    ///     Deletes cached covers that no song or album uses. Covers are named by their content, so songs and albums
    ///     share them.
    /// </summary>
    internal async Task DeleteUnreferencedCoverArtAsync(IEnumerable<string> paths, CancellationToken token)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        foreach (var batch in paths.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(MusicDbContext.MaxParametersPerQuery))
        {
            var references = await context.Songs
                .Where(s => s.AlbumArtUriFromTrack != null && batch.Contains(s.AlbumArtUriFromTrack))
                .Select(s => s.AlbumArtUriFromTrack!)
                .Union(context.Albums
                    .Where(a => a.CoverArtUri != null && batch.Contains(a.CoverArtUri) && a.Songs.Any())
                    .Select(a => a.CoverArtUri!))
                .ToListAsync(token).ConfigureAwait(false);

            foreach (var path in batch.Except(references, StringComparer.OrdinalIgnoreCase))
            {
                if (!IsExistingCacheFile(path, _pathConfig.AlbumArtCachePath)) continue;
                try
                {
                    _fileSystem.DeleteFile(path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete unreferenced cover art {FilePath}.", path);
                }
            }
        }
    }

    private bool IsExistingCacheFile(string? filePath, string cachePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;

        try
        {
            return PathCanonicalizer.IsSameOrInside(Path.GetFullPath(filePath), Path.GetFullPath(cachePath))
                   && _fileSystem.FileExists(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not validate cache path for {FilePath}", filePath);
            return false;
        }
    }

    private async Task FetchMissingArtistMetadataAsync(CancellationToken token)
    {
        const int batchSize = 50;
        Guid? afterId = null;
        var lookupsOff = false;
        while (!lookupsOff && !token.IsCancellationRequested)
        {
            await using var batchContext = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
            var query = batchContext.Artists.Where(a => a.MetadataLastCheckedUtc == null);
            if (afterId.HasValue) query = query.Where(a => a.Id > afterId.Value);
            var artistsInBatch = await query.OrderBy(a => a.Id).Take(batchSize)
                .ToListAsync(token).ConfigureAwait(false);

            if (artistsInBatch.Count == 0) break;
            // A failed lookup stays retryable, but must not loop forever in this run.
            afterId = artistsInBatch[^1].Id;
            var directoriesByArtist = await LoadSongDirectoriesByArtistAsync(batchContext,
                artistsInBatch.Select(a => a.Id).ToList(), token).ConfigureAwait(false);

            var pendingUpdates = new List<ArtistImageChange>();
            var stopwatch = Stopwatch.StartNew();
            var processedCount = 0;

            foreach (var artist in artistsInBatch)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    var (lookedUp, updated) = await FetchArtistMetadataAsync(artist,
                        directoriesByArtist.GetValueOrDefault(artist.Id, []), token).ConfigureAwait(false);
                    if (updated) pendingUpdates.Add(new ArtistImageChange(artist.Id, artist.LocalImageCachePath));

                    if (!lookedUp)
                    {
                        _logger.LogDebug("Online metadata was turned off or has no source; stopping the artist pass.");
                        lookupsOff = true;
                        break;
                    }

                    if (++processedCount >= 10 || stopwatch.Elapsed.TotalSeconds >= 5)
                    {
                        await batchContext.SaveChangesAsync(token).ConfigureAwait(false);

                        if (pendingUpdates.Count > 0)
                        {
                            _logger.LogInformation("Saved partial batch of {Count} artists metadata (Time: {Elapsed}s).", pendingUpdates.Count, stopwatch.Elapsed.TotalSeconds.ToString("F1"));
                            _notifier.RaiseArtistImagesChanged(pendingUpdates.ToList());
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

            // Saves MetadataLastCheckedUtc even for artists nothing was found for.
            await batchContext.SaveChangesAsync(token).ConfigureAwait(false);

            if (pendingUpdates.Count > 0)
            {
                _logger.LogInformation("Saved final batch of {Count} artists metadata.", pendingUpdates.Count);
                _notifier.RaiseArtistImagesChanged(pendingUpdates);
            }
        }
    }

    /// <summary>
    ///     Looks for the artist's image in its song directories, then online. The caller saves the tracked artist.
    /// </summary>
    /// <returns>
    ///     LookedUp is false when online metadata is off or has no source, so only a folder image was looked for.
    /// </returns>
    private async Task<(bool LookedUp, bool Updated)> FetchArtistMetadataAsync(Artist artist,
        IReadOnlyList<string?> songDirectories, CancellationToken cancellationToken)
    {
        // As in Navidrome, a folder image (artist.*, album/artist.*) comes before the online sources, and replaces a
        // fetched image but never a custom image or an earlier folder image.
        var updated = !ImageStorageHelper.IsUserOrFolderImage(artist.LocalImageCachePath)
                      && await UseFolderImageAsync(artist, songDirectories, cancellationToken).ConfigureAwait(false);
        if (updated) _logger.LogInformation("Using local folder image for artist '{ArtistName}'.", artist.Name);

        var found = await _artistMetadataService.FindAsync(artist.Name, artist.MusicBrainzId, cancellationToken)
            .ConfigureAwait(false);
        if (found is not null)
            updated |= await ApplyOnlineMetadataAsync(artist, found, cancellationToken).ConfigureAwait(false);

        return (found is not null, updated);
    }

    /// <returns>Whether the artist's image was found and saved as its folder image.</returns>
    private async Task<bool> UseFolderImageAsync(Artist artist, IReadOnlyList<string?> songDirectories,
        CancellationToken cancellationToken, Dictionary<string, string?>? directoryCache = null)
    {
        var imageBytes = await FindArtistImageInDirectoriesAsync(songDirectories, artist.Name, cancellationToken, directoryCache)
            .ConfigureAwait(false);
        if (imageBytes is null) return false;

        try
        {
            var processed = await _imageProcessor.ProcessImageBytesAsync(imageBytes).ConfigureAwait(false);
            var localPath = await ImageStorageHelper.SaveImageBytesAsync(_fileSystem,
                _pathConfig.ArtistImageCachePath, artist.Id.ToString(), CachedImageKind.Folder, processed).ConfigureAwait(false);
            if (string.IsNullOrEmpty(localPath)) return false;

            artist.LocalImageCachePath = localPath;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process local artist image for '{ArtistName}'.", artist.Name);
            return false;
        }
    }

    /// <returns>Whether anything about the artist changed.</returns>
    private async Task<bool> ApplyOnlineMetadataAsync(Artist artist, ArtistMetadataResult found,
        CancellationToken cancellationToken)
    {
        var updated = false;
        var isConclusive = found.IsConclusive;

        if (found.MusicBrainzId is not null)
        {
            artist.MusicBrainzId = found.MusicBrainzId;
            updated = true;
        }

        if (!string.IsNullOrEmpty(found.Biography))
        {
            artist.Biography = found.Biography;
            updated = true;
        }

        if (!string.IsNullOrEmpty(found.ImageUrl) &&
            !ImageStorageHelper.IsUserOrFolderImage(artist.LocalImageCachePath))
        {
            var downloadedPath =
                await DownloadAndCacheArtistImageAsync(artist, new Uri(found.ImageUrl), cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(downloadedPath))
            {
                artist.LocalImageCachePath = downloadedPath;
                updated = true;
            }
            else
            {
                isConclusive = false;
            }
        }

        // Incomplete metadata remains retryable after a provider or image-download failure.
        if (isConclusive ||
            (!string.IsNullOrWhiteSpace(artist.Biography) && !string.IsNullOrWhiteSpace(artist.LocalImageCachePath)))
        {
            artist.MetadataLastCheckedUtc = DateTime.UtcNow;
        }
        else
        {
            _logger.LogDebug(
                "Metadata lookup incomplete for '{ArtistName}'; leaving MetadataLastCheckedUtc null for retry.",
                artist.Name);
        }

        return updated;
    }

    private async Task<string?> DownloadAndCacheArtistImageAsync(Artist artist, Uri imageUrl, CancellationToken cancellationToken)
    {
        var artistId = artist.Id;
        // Callers share one download that runs until shutdown, and each stops waiting on its own token. The entry
        // goes when the download ends rather than when a caller gives up, so it is never started twice.
        var lazyTask = _artistImageDownloads.GetOrAdd(artistId, _ => new Lazy<Task<string?>>(async () =>
        {
            try
            {
                return await DownloadAndWriteImageInternalAsync(artistId, imageUrl, ShutdownToken).ConfigureAwait(false);
            }
            finally
            {
                _artistImageDownloads.TryRemove(artistId, out Lazy<Task<string?>>? _);
            }
        }));
        try
        {
            return await lazyTask.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Artist image download failed for artist '{ArtistName}'.", artist.Name);
            return null;
        }
    }

    private async Task<string?> DownloadAndWriteImageInternalAsync(Guid artistId, Uri imageUrl, CancellationToken cancellationToken)
    {
        var existing = ImageStorageHelper.FindImage(_fileSystem, _pathConfig.ArtistImageCachePath, artistId.ToString(), CachedImageKind.Fetched);
        if (existing is not null) return existing;

        var imageBytes = await DownloadImageBytesAsync(imageUrl, cancellationToken).ConfigureAwait(false);
        if (imageBytes is null) return null;

        var processedBytes = await Task.Run(() => _imageProcessor.ProcessImageBytesAsync(imageBytes), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await ImageStorageHelper.SaveImageBytesAsync(_fileSystem, _pathConfig.ArtistImageCachePath,
            artistId.ToString(), CachedImageKind.Fetched, processedBytes).ConfigureAwait(false);
    }

    private async Task<byte[]?> DownloadImageBytesAsync(Uri imageUrl, CancellationToken cancellationToken) =>
        (await _imageDownloads.GetBytesAsync(imageUrl, $"image download {imageUrl}", cancellationToken).ConfigureAwait(false)).Data;

    /// <summary>
    ///     The directories of each artist's songs on disk, where an artist image may sit beside them.
    /// </summary>
    private static async Task<Dictionary<Guid, IReadOnlyList<string?>>> LoadSongDirectoriesByArtistAsync(
        MusicDbContext context, List<Guid> artistIds, CancellationToken ct)
    {
        var rows = await context.Songs
            .AsNoTracking()
            .OnDisk()
            .SelectMany(s => s.SongArtists.Where(sa => artistIds.Contains(sa.ArtistId)),
                (s, sa) => new { sa.ArtistId, s.DirectoryPath })
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.GroupBy(r => r.ArtistId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string?>)g.Select(r => r.DirectoryPath).ToList());
    }

    /// <summary>
    ///     Searches the song directories in two passes following Navidrome's priority: first their parent directories
    ///     ("artist folder", e.g. F:/Music/Daft Punk/), then the song directories themselves ("album folder", e.g.
    ///     F:/Music/Daft Punk/Discovery/). Drive roots are left out of the first pass.
    /// </summary>
    private async Task<byte[]?> FindArtistImageInDirectoriesAsync(IReadOnlyList<string?> songDirectories, string artistName,
        CancellationToken ct, Dictionary<string, string?>? directoryCache = null)
    {
        var directories = songDirectories.OfType<string>().Where(dir => dir.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var parents = directories.Select(Path.GetDirectoryName).OfType<string>()
            .Where(parent => parent.Length > 0 && !string.IsNullOrEmpty(Path.GetDirectoryName(parent)));

        foreach (var dir in parents.Concat(directories).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            if (directoryCache == null || !directoryCache.TryGetValue(dir, out var imagePath))
            {
                imagePath = FindArtistImageInDirectory(dir);
                if (directoryCache != null) directoryCache[dir] = imagePath;
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

    private async Task FetchMissingAlbumCoversAsync(CancellationToken token)
    {
        const int batchSize = 50;
        // "Unknown Album" groups unrelated tracks, so no single cover is right for it.
        var unknownAlbum = Album.UnknownAlbumName;
        Guid? afterId = null;
        var lookupsOff = false;
        while (!lookupsOff && !token.IsCancellationRequested)
        {
            List<AlbumCoverCandidate> albums;
            await using (var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false))
            {
                var query = context.Albums.Where(a =>
                    a.CoverArtUri == null && a.CoverArtLastCheckedUtc == null && a.Title != unknownAlbum);
                if (afterId.HasValue) query = query.Where(a => a.Id > afterId.Value);
                albums = await query.OrderBy(a => a.Id).Take(batchSize)
                    .Select(a => new AlbumCoverCandidate(a.Id, a.Title, a.PrimaryArtistName,
                        a.Songs.Where(s => s.MusicBrainzReleaseId != null).Select(s => s.MusicBrainzReleaseId).FirstOrDefault(),
                        a.Songs.Where(s => s.MusicBrainzReleaseGroupId != null).Select(s => s.MusicBrainzReleaseGroupId).FirstOrDefault()))
                    .ToListAsync(token).ConfigureAwait(false);
            }

            if (albums.Count == 0) break;
            // A failed lookup stays retryable, but must not loop forever in this run.
            afterId = albums[^1].Id;

            var updatedCount = 0;
            foreach (var album in albums)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    var updated = await FetchAlbumCoverAsync(album, token).ConfigureAwait(false);
                    if (updated is null)
                    {
                        _logger.LogDebug("Online metadata was turned off or has no cover source; stopping the album pass.");
                        lookupsOff = true;
                        break;
                    }

                    if (updated.Value) updatedCount++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to fetch a cover for album {AlbumId} in background.", album.Id);
                }
            }

            if (updatedCount > 0)
            {
                _logger.LogInformation("Added online covers to {Count} albums.", updatedCount);
                _notifier.Raise(LibraryChangeKind.SongsChanged);
            }
        }
    }

    /// <returns>Whether the album got a cover, or null when online metadata is off or has no cover source.</returns>
    private async Task<bool?> FetchAlbumCoverAsync(AlbumCoverCandidate album, CancellationToken token)
    {
        var result = await _albumCoverService.FindCoverUrlAsync(
            new AlbumCoverQuery(album.Title, album.ArtistName, album.ReleaseId, album.ReleaseGroupId), token).ConfigureAwait(false);
        if (result is null) return null;

        var imageBytes = result.Data is null
            ? null
            : await DownloadImageBytesAsync(new Uri(result.Data), token).ConfigureAwait(false);
        // A cover that failed to download stays retryable, like a source that failed.
        if (imageBytes is null && (result.Data is not null || !result.IsConclusive)) return false;

        // Covers are shared by content, so a scan deleting an unused cover could delete the one stored here.
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await StoreAlbumCoverAsync(album.Id, imageBytes, token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<bool> StoreAlbumCoverAsync(Guid albumId, byte[]? imageBytes, CancellationToken token)
    {
        (string? Uri, string? LightSwatchId, string? DarkSwatchId) cover = default;
        if (imageBytes is not null)
        {
            cover = await _imageProcessor.SaveCoverArtAndExtractColorsAsync(imageBytes).ConfigureAwait(false);
            if (cover.Uri is null) return false;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var checkedAt = DateTime.UtcNow;
        // Art a scan found in the meantime wins, so only an album still without a cover takes the online one.
        var stored = cover.Uri is not null && await context.Albums.Where(a => a.Id == albumId && a.CoverArtUri == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(a => a.CoverArtUri, cover.Uri)
                .SetProperty(a => a.CoverArtLastCheckedUtc, checkedAt), token)
            .ConfigureAwait(false) > 0;
        if (!stored)
        {
            await context.Albums.Where(a => a.Id == albumId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.CoverArtLastCheckedUtc, checkedAt), token)
                .ConfigureAwait(false);
            return false;
        }

        await context.Songs.Where(s => s.AlbumId == albumId && s.AlbumArtUriFromTrack == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.AlbumArtUriFromTrack, cover.Uri)
                .SetProperty(s => s.LightSwatchId, cover.LightSwatchId)
                .SetProperty(s => s.DarkSwatchId, cover.DarkSwatchId), token)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    ///     Gives each album whose cover none of its tracks use the art of its first track that has some. Without such
    ///     art, a cover whose cached file is missing is cleared so the online pass looks for one again. Replaced covers
    ///     are deleted when nothing else uses them.
    /// </summary>
    /// <returns>The number of albums updated.</returns>
    private async Task<int> SyncAlbumCoversWithTracksAsync(Guid folderId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // A cover a track uses is current; if its file is missing, scanning the folder of that track recreates it.
        var albums = await context.Albums
            .Where(a => a.Songs.Any(s => s.FolderId == folderId) &&
                        (a.CoverArtUri == null || !a.Songs.Any(s => s.AlbumArtUriFromTrack == a.CoverArtUri)))
            .Select(a => new
            {
                a.Id,
                a.CoverArtUri,
                TrackArt = a.Songs
                    .Where(s => s.AlbumArtUriFromTrack != null)
                    .OrderBy(s => s.DiscNumber).ThenBy(s => s.TrackNumber).ThenBy(s => s.FilePath)
                    .Select(s => s.AlbumArtUriFromTrack)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var updatedCount = 0;
        var replacedCovers = new List<string>();
        foreach (var album in albums)
        {
            // A track re-read without art of its own drops the album cover it showed, which doesn't make that cover
            // out of date.
            if (album.TrackArt is null && (album.CoverArtUri is null || _fileSystem.FileExists(album.CoverArtUri)))
                continue;

            updatedCount += await context.Albums.Where(a => a.Id == album.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(a => a.CoverArtUri, album.TrackArt)
                    .SetProperty(a => a.CoverArtLastCheckedUtc, (DateTime?)null), cancellationToken)
                .ConfigureAwait(false);
            if (album.CoverArtUri is not null) replacedCovers.Add(album.CoverArtUri);
        }

        await DeleteUnreferencedCoverArtAsync(replacedCovers, cancellationToken).ConfigureAwait(false);
        if (updatedCount > 0)
            _logger.LogInformation("Updated the cover of {Count} albums to match their tracks.", updatedCount);
        return updatedCount;
    }

    /// <summary>
    ///     Gives tracks without embedded or folder art the cover of their album, which may come from another
    ///     track or an online source.
    /// </summary>
    /// <returns>The number of tracks updated.</returns>
    private async Task<int> ApplyAlbumCoversToTracksWithoutArtAsync(Guid folderId, CancellationToken cancellationToken)
    {
        // "Unknown Album" groups unrelated tracks, so one of them must not lend its art to the rest.
        var unknownAlbum = Album.UnknownAlbumName;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var tracksWithoutArt = context.Songs.Where(s => s.FolderId == folderId && s.AlbumArtUriFromTrack == null &&
                                                        s.Album != null && s.Album.CoverArtUri != null &&
                                                        s.Album.Title != unknownAlbum);
        var covers = await tracksWithoutArt.Select(s => s.Album!.CoverArtUri!).Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var updatedCount = 0;
        foreach (var cover in covers)
        {
            // The album's cover came from one of its tracks or the online pass, which stored its theme colors.
            var swatches = await context.Songs.Where(s => s.AlbumArtUriFromTrack == cover)
                .Select(s => new { s.LightSwatchId, s.DarkSwatchId })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var lightSwatchId = swatches?.LightSwatchId;
            var darkSwatchId = swatches?.DarkSwatchId;

            updatedCount += await tracksWithoutArt.Where(s => s.Album!.CoverArtUri == cover)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(s => s.AlbumArtUriFromTrack, cover)
                    .SetProperty(s => s.LightSwatchId, lightSwatchId)
                    .SetProperty(s => s.DarkSwatchId, darkSwatchId), cancellationToken)
                .ConfigureAwait(false);
        }

        if (updatedCount > 0)
            _logger.LogInformation("Gave {Count} tracks without art their album cover.", updatedCount);
        return updatedCount;
    }

    private sealed record AlbumCoverCandidate(Guid Id, string Title, string ArtistName, string? ReleaseId, string? ReleaseGroupId);
}
