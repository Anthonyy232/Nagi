using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Library;
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
    private readonly LibraryArtwork _libraryArtwork;
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
        LibraryArtwork libraryArtwork,
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
        _libraryArtwork = libraryArtwork;
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
                var songDirectories = (await LibraryArtwork.LoadSongDirectoriesByArtistAsync(context, [artist.Id], cancellationToken)
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

    public async Task RunWithoutMetadataFetchAsync(Func<Task> action)
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
            var directoriesByArtist = await LibraryArtwork.LoadSongDirectoriesByArtistAsync(batchContext,
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
                      && await _libraryArtwork.UseFolderImageAsync(artist, songDirectories, cancellationToken).ConfigureAwait(false);
        if (updated) _logger.LogInformation("Using local folder image for artist '{ArtistName}'.", artist.Name);

        var found = await _artistMetadataService.FindAsync(artist.Name, artist.MusicBrainzId, cancellationToken)
            .ConfigureAwait(false);
        if (found is not null)
            updated |= await ApplyOnlineMetadataAsync(artist, found, cancellationToken).ConfigureAwait(false);

        return (found is not null, updated);
    }

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

    private sealed record AlbumCoverCandidate(Guid Id, string Title, string ArtistName, string? ReleaseId, string? ReleaseGroupId);
}
