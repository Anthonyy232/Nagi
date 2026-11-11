using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Artwork;
using Nagi.Core.Data;
using Nagi.Core.Platform;

namespace Nagi.Core.Library;

/// <summary>
///     Keeps the library's artwork in line with its songs as scans and syncs change them: artist images from the
///     music folders, album covers taken from their tracks, and cached images nothing uses any more. Callers hold the
///     <see cref="LibraryWriteLock" />.
/// </summary>
public sealed class LibraryArtwork
{
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly IFileSystemService _fileSystem;
    private readonly IPathConfiguration _pathConfig;
    private readonly IImageProcessor _imageProcessor;
    private readonly LibraryNotifier _notifier;
    private readonly ILogger<LibraryArtwork> _logger;

    public LibraryArtwork(
        IDbContextFactory<MusicDbContext> contextFactory,
        IFileSystemService fileSystem,
        IPathConfiguration pathConfig,
        IImageProcessor imageProcessor,
        LibraryNotifier notifier,
        ILogger<LibraryArtwork> logger)
    {
        _contextFactory = contextFactory;
        _fileSystem = fileSystem;
        _pathConfig = pathConfig;
        _imageProcessor = imageProcessor;
        _notifier = notifier;
        _logger = logger;
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

        foreach (var chunk in artistIds.Chunk(MusicDbContext.MaxParametersPerQuery))
            await context.Artists.Where(a => chunk.Contains(a.Id))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(a => a.LocalImageCachePath, (string?)null)
                    .SetProperty(a => a.MetadataLastCheckedUtc, (DateTime?)null), cancellationToken)
                .ConfigureAwait(false);
        _logger.LogInformation("Cleared the missing cached image of {Count} artists.", artistIds.Count);
        _notifier.RaiseArtistImagesChanged(artistIds.Select(id => new ArtistImageChange(id, null, null)).ToList());
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

        // Artists often share directories, which the finder then lists once.
        var finder = new MediaFileFinder(_fileSystem, _logger);

        var changes = new List<ArtistImageChange>();
        foreach (var artist in artistsNeedingImages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dirs = songDirsByArtistId.GetValueOrDefault(artist.Id, []);
            if (await UseFolderImageAsync(artist, dirs, finder, cancellationToken).ConfigureAwait(false))
                changes.Add(new ArtistImageChange(artist));
        }

        if (changes.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Updated {Count} artists with local folder images.", changes.Count);
            _notifier.RaiseArtistImagesChanged(changes);
        }

        return changes.Count;
    }

    /// <summary>
    ///     Looks for the artist's image in its song directories and saves it as the artist's folder image. The caller
    ///     saves the tracked artist.
    /// </summary>
    /// <returns>Whether the artist's image was found and saved as its folder image.</returns>
    internal async Task<bool> UseFolderImageAsync(Artist artist, IReadOnlyList<string?> songDirectories,
        MediaFileFinder finder, CancellationToken cancellationToken)
    {
        var imageBytes = await FindArtistImageInDirectoriesAsync(songDirectories, artist.Name, finder, cancellationToken)
            .ConfigureAwait(false);
        if (imageBytes is null) return false;

        try
        {
            var processed = await _imageProcessor.ProcessImageBytesAsync(imageBytes).ConfigureAwait(false);
            var localPath = await ImageStorageHelper.SaveImageBytesAsync(_fileSystem,
                _pathConfig.ArtistImageCachePath, artist.Id.ToString(), CachedImageKind.Folder, processed).ConfigureAwait(false);
            if (string.IsNullOrEmpty(localPath)) return false;

            artist.SetImage(localPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process local artist image for '{ArtistName}'.", artist.Name);
            return false;
        }
    }

    /// <summary>
    ///     The directories of each artist's songs on disk, where an artist image may sit beside them.
    /// </summary>
    internal static async Task<Dictionary<Guid, IReadOnlyList<string?>>> LoadSongDirectoriesByArtistAsync(
        MusicDbContext context, List<Guid> artistIds, CancellationToken ct)
    {
        var directories = new Dictionary<Guid, IReadOnlyList<string?>>();
        foreach (var chunk in artistIds.Distinct().Chunk(MusicDbContext.MaxParametersPerQuery))
        {
            var rows = await context.Songs
                .AsNoTracking()
                .OnDisk()
                .SelectMany(s => s.SongArtists.Where(sa => chunk.Contains(sa.ArtistId)),
                    (s, sa) => new { sa.ArtistId, s.DirectoryPath })
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var group in rows.GroupBy(r => r.ArtistId))
                directories[group.Key] = group.Select(r => r.DirectoryPath).ToList();
        }

        return directories;
    }

    /// <summary>
    ///     Searches the song directories in two passes following Navidrome's priority: first their parent directories
    ///     ("artist folder", e.g. F:/Music/Daft Punk/), then the song directories themselves ("album folder", e.g.
    ///     F:/Music/Daft Punk/Discovery/). Drive roots are left out of the first pass.
    /// </summary>
    private async Task<byte[]?> FindArtistImageInDirectoriesAsync(IReadOnlyList<string?> songDirectories, string artistName,
        MediaFileFinder finder, CancellationToken ct)
    {
        var directories = songDirectories.OfType<string>().Where(dir => dir.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var parents = directories.Select(Path.GetDirectoryName).OfType<string>()
            .Where(parent => parent.Length > 0 && !string.IsNullOrEmpty(Path.GetDirectoryName(parent)));

        foreach (var dir in parents.Concat(directories).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            if (finder.FindArtistImage(dir) is not { } imagePath) continue;

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
}
