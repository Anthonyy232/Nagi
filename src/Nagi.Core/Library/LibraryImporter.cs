using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Lyrics;
using Nagi.Core.Metadata;
using Nagi.Core.Platform;
using Nagi.Core.Settings;

namespace Nagi.Core.Library;

/// <summary>
///     Writes a root folder's songs: turns metadata read from files or a music server into songs, albums, artists and
///     genres, and removes songs with what only they used. Callers hold the <see cref="LibraryWriteLock" />.
/// </summary>
public sealed class LibraryImporter
{
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly IFileSystemService _fileSystem;
    private readonly IPathConfiguration _pathConfig;
    private readonly LibraryArtwork _artwork;
    private readonly LibraryNotifier _notifier;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<LibraryImporter> _logger;

    public LibraryImporter(
        IDbContextFactory<MusicDbContext> contextFactory,
        IFileSystemService fileSystem,
        IPathConfiguration pathConfig,
        LibraryArtwork artwork,
        LibraryNotifier notifier,
        ISettingsService settingsService,
        ILogger<LibraryImporter> logger)
    {
        _contextFactory = contextFactory;
        _fileSystem = fileSystem;
        _pathConfig = pathConfig;
        _artwork = artwork;
        _notifier = notifier;
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>
    ///     Starts an import into the root folder. Its batches must be written one after another.
    /// </summary>
    /// <param name="existingPaths">The paths of songs already in the folder, which are updated rather than added.</param>
    /// <param name="keepMediaAssets">
    ///     Whether the song at a path keeps its cover and lyrics paths rather than taking the metadata's, because they
    ///     weren't read.
    /// </param>
    internal async Task<SongImport> BeginAsync(Guid folderId, IReadOnlySet<string> existingPaths,
        Func<string, bool> keepMediaAssets, CancellationToken cancellationToken)
    {
        var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            context.ChangeTracker.AutoDetectChangesEnabled = false;
            var caches = await LoadCachesAsync(context, cancellationToken).ConfigureAwait(false);
            return new SongImport(context, caches, folderId, existingPaths, keepMediaAssets,
                _settingsService.Get(CoreSettings.ArtistSplitCharacters), _settingsService.Get(CoreSettings.GenreSplitCharacters));
        }
        catch
        {
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     Deletes the folder's songs at these paths, with the cached lyrics and covers only they used. Deleting most of
    ///     a folder's songs while adding hardly any means the scan or sync missed them, as when a NAS or server library
    ///     is briefly offline, so then nothing is deleted: that would lose their playlist entries and listening
    ///     history, and the next good pass reconciles instead.
    /// </summary>
    /// <param name="existingCount">The number of songs in the folder before the scan or sync.</param>
    /// <param name="addedCount">The number of songs the scan or sync added.</param>
    /// <returns>The number of songs deleted.</returns>
    internal async Task<int> DeleteSongsAsync(Guid folderId, IReadOnlyCollection<string> filePaths, int existingCount,
        int addedCount, CancellationToken cancellationToken)
    {
        if (filePaths.Count == 0) return 0;
        if (existingCount > 0 && filePaths.Count > Math.Max(50, existingCount / 2) && addedCount < filePaths.Count / 10)
        {
            _logger.LogError(
                "Safety guard tripped for folder {FolderId}: proposed to delete {DeleteCount}/{ExistingCount} songs while only adding {AddCount}. Keeping them; the folder or server library is likely offline.",
                folderId, filePaths.Count, existingCount, addedCount);
            return 0;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var deleted = 0;
        foreach (var paths in filePaths.Chunk(MusicDbContext.MaxParametersPerQuery))
        {
            var (count, files) = await DeleteSongRowsAsync(
                context.Songs.Where(s => s.FolderId == folderId && paths.Contains(s.FilePath)), cancellationToken).ConfigureAwait(false);
            await DeleteSongFilesAsync(files, cancellationToken).ConfigureAwait(false);
            deleted += count;
        }

        return deleted;
    }

    /// <summary>
    ///     Removes a root folder with its songs, and everything only they used, and announces it.
    /// </summary>
    /// <returns>False if the folder isn't in the library or couldn't be removed.</returns>
    internal async Task<bool> RemoveRootAsync(Guid folderId, CancellationToken cancellationToken)
    {
        SongFiles files;
        string folderName;
        await using (var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var folder = await context.Folders.FindAsync([folderId], cancellationToken).ConfigureAwait(false);
            if (folder is null)
            {
                _logger.LogWarning("Could not remove folder: Folder with ID {FolderId} not found.", folderId);
                return false;
            }

            folderName = folder.Name;
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                (_, files) = await DeleteSongRowsAsync(context.Songs.Where(s => s.FolderId == folderId), cancellationToken)
                    .ConfigureAwait(false);
                context.Folders.Remove(folder);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            // Disposing the uncommitted transaction rolls it back; a cancellation propagates to the scan that asked for it.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Folder removal for ID {FolderId} failed and was rolled back.", folderId);
                return false;
            }
        }

        await CleanUpOrphansAsync(cancellationToken).ConfigureAwait(false);
        await DeleteSongFilesAsync(files, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Removed folder '{FolderName}' and associated data.", folderName);
        _notifier.Raise(LibraryChangeKind.RootRemoved, folderId);
        return true;
    }

    /// <summary>
    ///     Deletes subfolders, albums, genres and artists that no song uses any more, and the removed artists' images.
    /// </summary>
    internal async Task CleanUpOrphansAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var songDirectories = await context.Songs.Select(s => s.DirectoryPath).Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var occupiedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in songDirectories)
        {
            var path = PathCanonicalizer.NormalizeDirectoryPath(directory);
            while (!string.IsNullOrEmpty(path) && occupiedPaths.Add(path))
                path = Path.GetDirectoryName(path);
        }

        // Delete leaves first, preserving roots and ancestors of occupied directories.
        while (true)
        {
            var leaves = await context.Folders
                .Where(f => f.ParentFolderId != null && !f.SubFolders.Any())
                .Select(f => new { f.Id, f.Path })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var ids = leaves.Where(f => !occupiedPaths.Contains(PathCanonicalizer.NormalizeDirectoryPath(f.Path)))
                .Select(f => f.Id).ToArray();
            if (ids.Length == 0) break;
            foreach (var chunk in ids.Chunk(MusicDbContext.MaxParametersPerQuery))
                await context.Folders.Where(f => chunk.Contains(f.Id))
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        await context.Albums.Where(a => !a.Songs.Any()).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.Genres.Where(g => !g.Songs.Any()).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        var orphanedArtistIds = await context.Artists
            .Where(a => !a.SongArtists.Any() && !a.AlbumArtists.Any())
            .Select(a => a.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (orphanedArtistIds.Count == 0) return;

        foreach (var chunk in orphanedArtistIds.Chunk(MusicDbContext.MaxParametersPerQuery))
            await context.Artists.Where(a => chunk.Contains(a.Id))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        _artwork.DeleteArtistImages(orphanedArtistIds);
    }

    private sealed record SongFiles(List<string> LrcPaths, List<string> CoverPaths);

    private static async Task<(int Deleted, SongFiles Files)> DeleteSongRowsAsync(IQueryable<Song> songs,
        CancellationToken cancellationToken)
    {
        var lrcPaths = await songs
            .Where(s => s.LrcFilePath != null)
            .Select(s => s.LrcFilePath!)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var coverPaths = await songs
            .Where(s => s.AlbumArtUriFromTrack != null)
            .Select(s => s.AlbumArtUriFromTrack!)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var deleted = await songs.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return (deleted, new SongFiles(lrcPaths, coverPaths));
    }

    private async Task DeleteSongFilesAsync(SongFiles files, CancellationToken cancellationToken)
    {
        // Only files in Nagi's own cache: an LRC that sits next to the song belongs to the user.
        foreach (var lrcPath in files.LrcPaths.Where(path => LrcCache.Contains(_pathConfig.LrcCachePath, path) && _fileSystem.FileExists(path)))
            try
            {
                _fileSystem.DeleteFile(lrcPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete cached LRC file {LrcPath}.", lrcPath);
            }

        await _artwork.DeleteUnreferencedCoverArtAsync(files.CoverPaths, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ImportCaches> LoadCachesAsync(MusicDbContext context, CancellationToken cancellationToken)
    {
        var caches = new ImportCaches(
            new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<(string Title, string Artist), Guid>(),
            new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase));

        var existingArtists = await context.Artists.Select(a => new { a.Id, a.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var artist in existingArtists)
            caches.ArtistIds.TryAdd(artist.Name, artist.Id);

        var existingAlbumsWithArtist = await context.Albums
            .AsNoTracking()
            .Select(a => new
            {
                a.Id,
                a.Title,
                PrimaryArtistName = a.AlbumArtists
                    .Where(aa => aa.Order == 0)
                    .Select(aa => aa.Artist.Name)
                    .FirstOrDefault() ?? string.Empty
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var album in existingAlbumsWithArtist)
            caches.AlbumIds.TryAdd(SongImport.AlbumKey(album.Title, album.PrimaryArtistName), album.Id);

        var existingGenres = await context.Genres.Select(g => new { g.Id, g.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var genre in existingGenres)
            caches.GenreIds.TryAdd(genre.Name, genre.Id);

        _logger.LogDebug("Pre-loaded {ArtistCount} artists, {AlbumCount} albums, {GenreCount} genres into scan caches.",
            caches.ArtistIds.Count, caches.AlbumIds.Count, caches.GenreIds.Count);
        return caches;
    }
}

/// <summary>
///     Name-to-ID lookups for artists, albums and genres that live for one import, so batches written one after
///     another reuse the rows earlier batches created instead of adding duplicates.
/// </summary>
internal sealed record ImportCaches(
    Dictionary<string, Guid> ArtistIds,
    Dictionary<(string Title, string Artist), Guid> AlbumIds,
    Dictionary<string, Guid> GenreIds);

/// <summary>
///     One import into a root folder, from <see cref="LibraryImporter.BeginAsync" />.
/// </summary>
internal sealed class SongImport(
    MusicDbContext context,
    ImportCaches caches,
    Guid folderId,
    IReadOnlySet<string> existingPaths,
    Func<string, bool> keepMediaAssets,
    string artistSplitCharacters,
    string genreSplitCharacters) : IAsyncDisposable
{
    /// <summary>
    ///     The covers songs used before they were read again, which may be unused once the import is done.
    /// </summary>
    public HashSet<string> ReplacedCovers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask DisposeAsync() => context.DisposeAsync();

    /// <param name="apply">Fills in what <typeparamref name="TMetadata" /> knows beyond the tags; isNew is true for an added song.</param>
    public async Task WriteBatchAsync<TMetadata>(IReadOnlyCollection<TMetadata> batch, CancellationToken cancellationToken,
        Action<Song, TMetadata, bool>? apply = null) where TMetadata : SongFileMetadata
    {
        // An initial scan only adds songs, so it skips loading songs that can't exist.
        var updatePaths = batch.Select(m => m.FilePath).Where(existingPaths.Contains).ToList();
        var existingSongs = updatePaths.Count == 0
            ? new Dictionary<string, Song>(StringComparer.OrdinalIgnoreCase)
            : await context.Songs
                .Include(s => s.Genres)
                .Include(s => s.SongArtists)
                .Where(s => updatePaths.Contains(s.FilePath))
                .AsSplitQuery()
                .ToDictionaryAsync(s => s.FilePath, StringComparer.OrdinalIgnoreCase, cancellationToken).ConfigureAwait(false);

        foreach (var metadata in batch) metadata.NormalizeTags(artistSplitCharacters, genreSplitCharacters);

        var artistNames = batch.SelectMany(m => m.Artists.Concat(m.AlbumArtists))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var genreNames = batch.SelectMany(m => m.Genres).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var artistLookup = ResolveArtists(artistNames);
        await ApplyTaggedArtistIdsAsync(batch, artistLookup, cancellationToken).ConfigureAwait(false);
        var genreLookup = ResolveGenres(genreNames);
        var albumLookup = await ResolveAlbumsAsync(BuildAlbumDefinitions(batch), artistLookup, cancellationToken).ConfigureAwait(false);

        foreach (var metadata in batch)
        {
            existingSongs.TryGetValue(metadata.FilePath, out var existingSong);
            var previousArt = existingSong?.AlbumArtUriFromTrack;
            var song = MapSong(metadata, artistLookup, albumLookup, genreLookup, existingSong);
            apply?.Invoke(song, metadata, existingSong is null);
            if (previousArt is not null && previousArt != song.AlbumArtUriFromTrack)
                ReplacedCovers.Add(previousArt);
        }

        // With AutoDetectChangesEnabled=false, collection operations (Clear/Add on navigation properties)
        // may not be reflected in entity states until DetectChanges is called explicitly.
        context.ChangeTracker.DetectChanges();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.ChangeTracker.Clear();
    }

    internal static (string Title, string Artist) AlbumKey(string title, string artist) =>
        (title.ToUpperInvariant(), artist.ToUpperInvariant());

    private static (string Title, string Artist) AlbumKey(SongFileMetadata metadata) =>
        AlbumKey(metadata.Album!, metadata.AlbumArtists[0]);

    private static Dictionary<(string Title, string Artist), (string Title, List<string> ArtistNames, int? Year)> BuildAlbumDefinitions(
        IEnumerable<SongFileMetadata> batch)
    {
        var albumDefinitions = new Dictionary<(string Title, string Artist), (string Title, List<string> ArtistNames, int? Year)>();

        foreach (var metadata in batch)
        {
            var albumKey = AlbumKey(metadata);
            if (albumDefinitions.TryGetValue(albumKey, out var existing))
            {
                if (existing.Year is null && metadata.Year.HasValue)
                    albumDefinitions[albumKey] = (existing.Title, existing.ArtistNames, metadata.Year);
                continue;
            }

            albumDefinitions[albumKey] = (metadata.Album!, metadata.AlbumArtists, metadata.Year);
        }

        return albumDefinitions;
    }

    private Dictionary<string, Artist> ResolveArtists(IReadOnlyCollection<string> artistNames)
    {
        var artistLookup = new Dictionary<string, Artist>(StringComparer.OrdinalIgnoreCase);
        // The cache holds every existing artist, and batches are written one after another.
        foreach (var name in artistNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (caches.ArtistIds.TryGetValue(name, out var id))
            {
                var artist = new Artist { Id = id, Name = name };
                context.Attach(artist);
                artistLookup.Add(name, artist);
            }
            else
            {
                var artist = new Artist { Name = name };
                context.Artists.Add(artist);
                artistLookup.Add(name, artist);
                caches.ArtistIds.Add(name, artist.Id);
            }
        }

        return artistLookup;
    }

    // Tags only fill artists without an ID, so an ID resolved earlier is never replaced mid-scan.
    private async Task ApplyTaggedArtistIdsAsync(IEnumerable<SongFileMetadata> batch,
        Dictionary<string, Artist> artistLookup, CancellationToken cancellationToken)
    {
        var taggedIds = new Dictionary<Artist, string>();
        foreach (var metadata in batch)
        {
            Collect(metadata.Artists, metadata.MusicBrainzArtistIds);
            Collect(metadata.AlbumArtists, metadata.MusicBrainzAlbumArtistIds);
        }

        if (taggedIds.Count == 0) return;

        var existingIds = taggedIds.Keys.Where(a => context.Entry(a).State != EntityState.Added).Select(a => a.Id).ToList();
        var unidentifiedIds = (await context.Artists
            .Where(a => existingIds.Contains(a.Id) && a.MusicBrainzId == null)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

        foreach (var (artist, musicBrainzId) in taggedIds)
        {
            var entry = context.Entry(artist);
            if (entry.State == EntityState.Added)
            {
                artist.MusicBrainzId = musicBrainzId;
            }
            else if (unidentifiedIds.Contains(artist.Id))
            {
                artist.MusicBrainzId = musicBrainzId;
                entry.Property(a => a.MusicBrainzId).IsModified = true;
            }
        }

        void Collect(List<string> names, List<string> musicBrainzIds)
        {
            // Tags can't say which ID belongs to which name unless there is one per artist.
            if (names.Count != musicBrainzIds.Count) return;
            for (var i = 0; i < names.Count; i++)
            {
                if (artistLookup.TryGetValue(names[i], out var artist))
                    taggedIds.TryAdd(artist, musicBrainzIds[i]);
            }
        }
    }

    private Dictionary<string, Genre> ResolveGenres(IReadOnlyCollection<string> genreNames)
    {
        var genreLookup = new Dictionary<string, Genre>(StringComparer.OrdinalIgnoreCase);
        var trackedGenres = context.ChangeTracker.Entries<Genre>()
            .Select(entry => entry.Entity)
            .ToDictionary(genre => genre.Id);

        // Reuse genres loaded with existing songs before attaching stubs.
        foreach (var name in genreNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Genre genre;
            if (caches.GenreIds.TryGetValue(name, out var id))
            {
                if (!trackedGenres.TryGetValue(id, out genre!))
                {
                    genre = new Genre { Id = id, Name = name };
                    context.Attach(genre);
                }
            }
            else
            {
                genre = new Genre { Name = name };
                context.Genres.Add(genre);
                caches.GenreIds.Add(name, genre.Id);
            }
            genreLookup.Add(name, genre);
        }

        return genreLookup;
    }

    private async Task<Dictionary<(string Title, string Artist), Album>> ResolveAlbumsAsync(
        IReadOnlyDictionary<(string Title, string Artist), (string Title, List<string> ArtistNames, int? Year)> albumDefinitions,
        Dictionary<string, Artist> artistLookup,
        CancellationToken cancellationToken)
    {
        var albumLookup = new Dictionary<(string Title, string Artist), Album>();
        if (albumDefinitions.Count == 0) return albumLookup;

        var cachedEntries = new List<(Guid Id, (string Title, string Artist) Key)>();
        var uncachedKeys = new List<(string Title, string Artist)>();
        foreach (var albumKey in albumDefinitions.Keys)
        {
            if (caches.AlbumIds.TryGetValue(albumKey, out var id))
                cachedEntries.Add((id, albumKey));
            else
                uncachedKeys.Add(albumKey);
        }

        if (cachedEntries.Count > 0)
        {
            var cachedIds = cachedEntries.Select(e => e.Id).ToList();
            var loadedAlbums = await context.Albums
                .Include(a => a.AlbumArtists).ThenInclude(aa => aa.Artist)
                .Where(a => cachedIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, cancellationToken).ConfigureAwait(false);

            foreach (var (albumId, albumKey) in cachedEntries)
            {
                var album = loadedAlbums[albumId];
                var definition = albumDefinitions[albumKey];
                albumLookup.Add(albumKey, album);
                SyncAlbumArtists(album, definition.ArtistNames, artistLookup);
                if (album.Year is null && definition.Year.HasValue)
                {
                    album.Year = definition.Year;
                    context.Entry(album).Property(a => a.Year).IsModified = true;
                }
            }
        }

        // The cache holds every existing album and only this import adds albums, so a miss is a new album.
        foreach (var albumKey in uncachedKeys)
        {
            var definition = albumDefinitions[albumKey];
            var newAlbum = new Album { Title = definition.Title, Year = definition.Year };
            for (var i = 0; i < definition.ArtistNames.Count; i++)
                newAlbum.AlbumArtists.Add(new AlbumArtist { Artist = artistLookup[definition.ArtistNames[i]], Order = i });
            context.Albums.Add(newAlbum);
            albumLookup[albumKey] = newAlbum;
            caches.AlbumIds.TryAdd(albumKey, newAlbum.Id);
        }

        return albumLookup;
    }

    private static void SyncAlbumArtists(Album album, IReadOnlyList<string> artistNames, Dictionary<string, Artist> artistLookup)
    {
        var currentArtistNames = album.AlbumArtists
            .OrderBy(aa => aa.Order)
            .Select(aa => aa.Artist.Name);

        if (currentArtistNames.SequenceEqual(artistNames, StringComparer.OrdinalIgnoreCase))
            return;

        album.AlbumArtists.Clear();
        for (var i = 0; i < artistNames.Count; i++)
            album.AlbumArtists.Add(new AlbumArtist { Artist = artistLookup[artistNames[i]], Order = i });
    }

    private Song MapSong(
        SongFileMetadata metadata,
        Dictionary<string, Artist> artistLookup,
        Dictionary<(string Title, string Artist), Album> albumLookup,
        Dictionary<string, Genre> genreLookup,
        Song? existingSong)
    {
        var album = albumLookup[AlbumKey(metadata)];
        var genres = metadata.Genres.Select(name => genreLookup[name]).ToList();

        var song = existingSong ?? new Song();

        song.FilePath = metadata.FilePath;
        song.DirectoryPath = Path.GetDirectoryName(metadata.FilePath) ?? string.Empty;
        song.Title = metadata.Title;
        song.DurationTicks = metadata.Duration.Ticks;
        if (!keepMediaAssets(metadata.FilePath))
        {
            song.AlbumArtUriFromTrack = metadata.CoverArtUri;
            song.LightSwatchId = metadata.LightSwatchId;
            song.DarkSwatchId = metadata.DarkSwatchId;
            song.LrcFilePath = metadata.LrcFilePath;
        }
        song.Year = metadata.Year;
        song.TrackNumber = metadata.TrackNumber;
        song.TrackCount = metadata.TrackCount;
        song.DiscNumber = metadata.DiscNumber;
        song.DiscCount = metadata.DiscCount;
        song.SampleRate = metadata.SampleRate;
        song.Bitrate = metadata.Bitrate;
        song.Channels = metadata.Channels;
        song.FileCreatedDate = metadata.FileCreatedDate;
        song.FileModifiedDate = metadata.FileModifiedDate;
        song.FolderId = folderId;
        song.Composer = metadata.Composer;
        song.Bpm = metadata.Bpm;
        song.Lyrics = metadata.Lyrics;

        song.Album = album;
        song.AlbumId = album.Id;

        var newSongArtists = metadata.Artists
            .Select((name, index) => new SongArtist { Artist = artistLookup[name], Order = index })
            .ToList();
        if (!song.SongArtists.OrderBy(sa => sa.Order).Select(sa => (sa.ArtistId, sa.Order))
                .SequenceEqual(newSongArtists.Select(sa => (sa.Artist.Id, sa.Order))))
        {
            song.SongArtists.Clear();
            foreach (var songArtist in newSongArtists)
                song.SongArtists.Add(songArtist);
        }

        if (!song.Genres.SequenceEqual(genres))
        {
            song.Genres.Clear();
            foreach (var genre in genres)
                song.Genres.Add(genre);
        }

        song.Grouping = metadata.Grouping;
        song.Copyright = metadata.Copyright;
        song.Comment = metadata.Comment;
        song.Conductor = metadata.Conductor;
        song.MusicBrainzTrackId = metadata.MusicBrainzTrackId;
        song.MusicBrainzReleaseGroupId = metadata.MusicBrainzReleaseGroupId;
        song.MusicBrainzReleaseId = metadata.MusicBrainzReleaseId;
        song.ReplayGainTrackGain = metadata.ReplayGainTrackGain;
        song.ReplayGainTrackPeak = metadata.ReplayGainTrackPeak;

        if (existingSong == null)
        {
            song.DateAddedToLibrary = DateTime.UtcNow;
            context.Songs.Add(song);
        }

        if (string.IsNullOrEmpty(album.CoverArtUri) && !string.IsNullOrEmpty(metadata.CoverArtUri))
        {
            album.CoverArtUri = metadata.CoverArtUri;
            // AutoDetectChangesEnabled=false: must explicitly mark the modified property.
            context.Entry(album).Property(a => a.CoverArtUri).IsModified = true;
        }

        return song;
    }
}
