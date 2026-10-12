using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Turns extracted file metadata into songs, albums, artists, and genres in batched database writes.
/// </summary>
public partial class LibraryService
{
    /// <summary>
    ///     Extracts metadata from files and writes to a Channel for streaming consumption.
    ///     This allows the database batch writer to start processing immediately rather than
    ///     waiting for all files to be extracted first, reducing peak memory usage.
    ///     Uses scan-scoped caches for Artists, Albums, and Genres across sequential batches.
    /// </summary>
    private async Task<(int SavedCount, HashSet<string> DiscoveredDirectories)> ExtractAndSaveMetadataStreamingAsync(
        Guid folderId,
        List<string> filesToProcess,
        string baseFolderPath,
        IReadOnlySet<string> filePathsBeingUpdated,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken,
        bool skipMediaAssetsForUpdates = false)
    {
        const int channelCapacity = 100; // ~2 batches of buffer for backpressure
        var channel = Channel.CreateBounded<SongFileMetadata>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        using var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pipelineToken = pipelineCts.Token;

        var totalFiles = filesToProcess.Count;
        var processedCount = 0;
        const int progressReportingBatchSize = 25;
        var discoveredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        progress?.Report(new ScanProgress
        { StatusText = Resources.Strings.Status_PreparingScanCaches, Percentage = 0 });

        // Pre-load scan-scoped caches (names -> IDs) to prevent duplicate entity creation across batches
        var artistIdCache = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var albumIdCache = new Dictionary<(string Title, string Artist), Guid>();
        var genreIdCache = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        // Create the batch context early so it can be reused for both pre-warm queries and batch writes,
        // avoiding a second connection-open + 5-PRAGMA overhead.
        await using var batchContext = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        batchContext.ChangeTracker.AutoDetectChangesEnabled = false;

        {
            // Pre-load all existing artists, albums, and genres into caches
            var existingArtists = await batchContext.Artists.Select(a => new { a.Id, a.Name })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var artist in existingArtists)
            {
                artistIdCache.TryAdd(artist.Name, artist.Id);
            }

            var existingAlbumsWithArtist = await batchContext.Albums
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
            {
                var albumKey = AlbumKey(album.Title, album.PrimaryArtistName);
                albumIdCache.TryAdd(albumKey, album.Id);
            }

            var existingGenres = await batchContext.Genres.Select(g => new { g.Id, g.Name })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var genre in existingGenres)
            {
                genreIdCache.TryAdd(genre.Name, genre.Id);
            }
        }

        _logger.LogDebug("Pre-loaded {ArtistCount} artists, {AlbumCount} albums, {GenreCount} genres into scan caches.",
            artistIdCache.Count, albumIdCache.Count, genreIdCache.Count);

        progress?.Report(new ScanProgress
        { StatusText = Resources.Strings.Status_ReadingSongDetails, Percentage = 0 });

        var totalSaved = 0;

        var isNetworkFolder = _fileSystem.IsNetworkPath(baseFolderPath);
        var failureSummary = new ConcurrentDictionary<string, int>();
        var producerTask = Task.Run(async () =>
        {
            try
            {
                // Limit concurrent file opens on network storage to reduce transient I/O failures.
                var degreeOfParallelism = isNetworkFolder
                    ? Math.Min(4, Environment.ProcessorCount)
                    : Environment.ProcessorCount;

                await Parallel.ForEachAsync(
                    filesToProcess,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = degreeOfParallelism,
                        CancellationToken = pipelineToken
                    },
                    async (filePath, ct) =>
                    {
                        try
                        {
                            var includeMediaAssets = !skipMediaAssetsForUpdates || !filePathsBeingUpdated.Contains(filePath);
                            var extractionTask = includeMediaAssets
                                ? _metadataService.ExtractMetadataAsync(filePath, baseFolderPath)
                                : _metadataService.ExtractMetadataAsync(filePath, baseFolderPath, false);
                            var metadata = await extractionTask.WaitAsync(ct).ConfigureAwait(false);

                            // Retry once for transient failure classes. UnsupportedFormat / CorruptFile
                            // are permanent — no point retrying. Timeouts and file-access errors are
                            // often transient on network storage (brief SMB stall, renegotiation).
                            if (metadata.ExtractionFailed &&
                                (metadata.ErrorMessage == "ExtractionTimeout" || metadata.ErrorMessage == "FileAccessError"))
                            {
                                await Task.Delay(500, ct).ConfigureAwait(false);
                                extractionTask = includeMediaAssets
                                    ? _metadataService.ExtractMetadataAsync(filePath, baseFolderPath)
                                    : _metadataService.ExtractMetadataAsync(filePath, baseFolderPath, false);
                                metadata = await extractionTask.WaitAsync(ct).ConfigureAwait(false);
                            }

                            if (!metadata.ExtractionFailed)
                            {
                                await channel.Writer.WriteAsync(metadata, ct).ConfigureAwait(false);
                            }
                            else
                            {
                                var code = metadata.ErrorMessage ?? "Unknown";
                                failureSummary.AddOrUpdate(code, 1, (_, n) => n + 1);
                                _logger.LogDebug("Failed to extract metadata ({ErrorCode}) from file: {FilePath}", code, filePath);
                            }
                        }
                        finally
                        {
                            var currentCount = Interlocked.Increment(ref processedCount);

                            if (currentCount % progressReportingBatchSize == 0 || currentCount == totalFiles)
                            {
                                _logger.LogInformation("Producer progress: {Count}/{Total}", currentCount, totalFiles);
                                progress?.Report(new ScanProgress
                                {
                                    StatusText = Resources.Strings.Status_ReadingSongDetails,
                                    Percentage = (currentCount + Volatile.Read(ref totalSaved)) / (double)(totalFiles * 2) * 100,
                                });
                            }
                        }
                    }).ConfigureAwait(false);

                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
                throw;
            }
        }, pipelineToken);

        // Consumer: Batch and save to database as metadata arrives
        var consumerTask = Task.Run(async () =>
        {
            try
            {
                var batchNumber = 0;

                await foreach (var batch in channel.Reader.ReadAllAsync(pipelineToken).Chunk(50).ConfigureAwait(false))
                {
                    foreach (var metadata in batch)
                    {
                        var dir = _fileSystem.GetDirectoryName(metadata.FilePath);
                        if (dir != null) discoveredDirectories.Add(dir);
                    }
                    batchNumber++;
                    progress?.Report(new ScanProgress
                    {
                        StatusText = Resources.Strings.Status_ReadingSongDetails,
                        Percentage = (Volatile.Read(ref processedCount) + totalSaved) / (double)(totalFiles * 2) * 100,
                    });

                    var saved = await ProcessSingleBatchAsync(folderId, batch, batchContext, artistIdCache,
                        albumIdCache, genreIdCache, filePathsBeingUpdated, skipMediaAssetsForUpdates,
                        pipelineToken).ConfigureAwait(false);
                    _logger.LogInformation("Consumer finished batch {BatchNumber}. Saved {Count} items.", batchNumber, saved);
                    totalSaved += saved;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                pipelineCts.Cancel();
                channel.Writer.TryComplete(ex);
                _logger.LogError(ex, "Consumer failed in streaming metadata extraction for folder {FolderId}", folderId);
                throw;
            }
        }, pipelineToken);

        await Task.WhenAll(producerTask, consumerTask).ConfigureAwait(false);

        if (!failureSummary.IsEmpty)
        {
            var breakdown = string.Join(", ", failureSummary.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"));
            _logger.LogWarning("Metadata extraction completed with {TotalFailures} failures ({Breakdown}). Individual paths logged at Debug level.",
                failureSummary.Values.Sum(), breakdown);
        }

        return (totalSaved, discoveredDirectories);
    }

    /// <summary>
    ///     Processes a batch of song metadata, using scan-scoped caches for Artists, Albums, and Genres.
    ///     The single channel reader owns these caches and writes batches sequentially.
    /// </summary>
    private async Task<int> ProcessSingleBatchAsync(
        Guid folderId,
        SongFileMetadata[] metadataList,
        MusicDbContext context,
        Dictionary<string, Guid> artistIdCache,
        Dictionary<(string Title, string Artist), Guid> albumIdCache,
        Dictionary<string, Guid> genreIdCache,
        IReadOnlySet<string> filePathsBeingUpdated,
        bool preserveMediaAssetsForUpdates,
        CancellationToken cancellationToken)
    {
        // Only load existing song records for files that are known updates (not new additions).
        // For InitialScan all files are new, so this avoids issuing 3 empty SQL queries per batch.
        var updatePaths = filePathsBeingUpdated.Count > 0
            ? metadataList.Select(m => m.FilePath).Where(filePathsBeingUpdated.Contains).ToList()
            : null;

        Dictionary<string, Song> existingSongs;
        if (updatePaths is null or { Count: 0 })
        {
            existingSongs = new Dictionary<string, Song>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            existingSongs = await context.Songs
                .Include(s => s.Genres)
                .Include(s => s.SongArtists)
                .Where(s => updatePaths.Contains(s.FilePath))
                .AsSplitQuery()
                .ToDictionaryAsync(s => s.FilePath, StringComparer.OrdinalIgnoreCase, cancellationToken).ConfigureAwait(false);
        }

        var artistNames = metadataList.SelectMany(m =>
            m.Artists.Select(ArtistNameHelper.Normalize)
                .Concat(m.AlbumArtists.Select(ArtistNameHelper.Normalize)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (metadataList.Any(m => !m.AlbumArtists.Any() || !m.Artists.Any()))
        {
            artistNames.Add(Artist.UnknownArtistName);
        }

        var genreNames = metadataList.SelectMany(m => m.Genres)
            .Select(g => NormalizeString(g))
            .Where(g => !string.IsNullOrEmpty(g))
            .Select(g => g!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var artistLookup = ResolveArtistsForBatch(context, artistNames, artistIdCache);
        var genreLookup = ResolveGenresForBatch(context, genreNames, genreIdCache);

        var albumDefinitions = BuildAlbumDefinitions(metadataList);
        var albumLookup = await ResolveAlbumsForBatchAsync(context, albumDefinitions, artistLookup, albumIdCache, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Processing single batch for folder {FolderId}. Metadata count: {Count}. First file: {First}, Last file: {Last}",
            folderId, metadataList.Length,
            Path.GetFileName(metadataList[0].FilePath),
            Path.GetFileName(metadataList[^1].FilePath));

        foreach (var metadata in metadataList)
        {
            existingSongs.TryGetValue(metadata.FilePath, out var existingSong);
            AddSongWithDetailsCached(context, folderId, metadata, artistLookup, albumLookup, genreLookup, existingSong,
                preserveMediaAssetsForUpdates && existingSong != null);
        }

        _logger.LogInformation("Saving changes for batch...");
        // With AutoDetectChangesEnabled=false, collection operations (Clear/Add on navigation properties)
        // may not be reflected in entity states until DetectChanges is called explicitly.
        context.ChangeTracker.DetectChanges();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        context.ChangeTracker.Clear();

        _logger.LogInformation("Batch saved successfully.");

        return metadataList.Length;
    }

    private static (string Title, string Artist) AlbumKey(string title, string artist) =>
        (title.ToUpperInvariant(), artist.ToUpperInvariant());

    private static Dictionary<(string Title, string Artist), (string Title, List<string> ArtistNames, int? Year)> BuildAlbumDefinitions(
        IEnumerable<SongFileMetadata> metadataList)
    {
        var albumDefinitions = new Dictionary<(string Title, string Artist), (string Title, List<string> ArtistNames, int? Year)>();

        foreach (var metadata in metadataList)
        {
            if (string.IsNullOrWhiteSpace(metadata.Album))
                continue;

            var albumTitle = NormalizeString(metadata.Album) ?? string.Empty;
            if (string.IsNullOrEmpty(albumTitle))
                continue;
            var albumArtistNames = metadata.AlbumArtists.Select(ArtistNameHelper.Normalize).ToList();
            var trackArtistNames = metadata.Artists.Select(ArtistNameHelper.Normalize).ToList();

            if (albumArtistNames.Count == 0)
            {
                albumArtistNames = trackArtistNames.Count > 0
                    ? new List<string>(trackArtistNames)
                    : new List<string> { Artist.UnknownArtistName };
            }

            var albumKey = AlbumKey(albumTitle, albumArtistNames[0]);
            if (albumDefinitions.TryGetValue(albumKey, out var existing))
            {
                if (existing.Year is null && metadata.Year.HasValue)
                {
                    albumDefinitions[albumKey] = (existing.Title, existing.ArtistNames, metadata.Year);
                }
                continue;
            }

            albumDefinitions[albumKey] = (albumTitle, albumArtistNames, metadata.Year);
        }

        return albumDefinitions;
    }

    private Dictionary<string, Artist> ResolveArtistsForBatch(
        MusicDbContext context,
        IReadOnlyCollection<string> artistNames,
        Dictionary<string, Guid> artistIdCache)
    {
        var artistLookup = new Dictionary<string, Artist>(StringComparer.OrdinalIgnoreCase);
        // The scan cache contains every existing artist; batches are written sequentially.
        foreach (var name in artistNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (artistIdCache.TryGetValue(name, out var id))
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
                artistIdCache.Add(name, artist.Id);
            }
        }

        return artistLookup;
    }

    private Dictionary<string, Genre> ResolveGenresForBatch(
        MusicDbContext context,
        IReadOnlyCollection<string> genreNames,
        Dictionary<string, Guid> genreIdCache)
    {
        var genreLookup = new Dictionary<string, Genre>(StringComparer.OrdinalIgnoreCase);
        var trackedGenres = context.ChangeTracker.Entries<Genre>()
            .Select(entry => entry.Entity)
            .ToDictionary(genre => genre.Id);

        // Reuse genres loaded with existing songs before attaching stubs.
        foreach (var name in genreNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Genre genre;
            if (genreIdCache.TryGetValue(name, out var id))
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
                genreIdCache.Add(name, genre.Id);
            }
            genreLookup.Add(name, genre);
        }

        return genreLookup;
    }

    /// <summary>
    ///     Normalizes a string using shared normalization logic from ArtistNameHelper.
    ///     Returns null if the result is empty.
    /// </summary>
    private static string? NormalizeString(string? s) => ArtistNameHelper.NormalizeStringCore(s);

    private async Task<Dictionary<(string Title, string Artist), Album>> ResolveAlbumsForBatchAsync(
        MusicDbContext context,
        IReadOnlyDictionary<(string Title, string Artist), (string Title, List<string> ArtistNames, int? Year)> albumDefinitions,
        Dictionary<string, Artist> artistLookup,
        Dictionary<(string Title, string Artist), Guid> albumIdCache,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("ResolveAlbumsForBatchAsync entered. Definition count: {Count}", albumDefinitions.Count);
        var albumLookup = new Dictionary<(string Title, string Artist), Album>();

        if (albumDefinitions.Count == 0)
            return albumLookup;

        // Step 1: Check the case-insensitive cache first to partition into known vs unknown
        var cachedEntries = new List<(Guid Id, (string Title, string Artist) Key)>();
        var uncachedKeys = new List<(string Title, string Artist)>();

        foreach (var albumKey in albumDefinitions.Keys)
        {
            if (albumIdCache.TryGetValue(albumKey, out var id))
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

        // Step 3: Create new albums directly — no DB double-check needed.
        // Pre-warm loaded all existing albums into the cache; cache miss = genuinely new album.
        // Sequential consumer means no concurrent batch can create the same album.
        if (uncachedKeys.Count == 0)
            return albumLookup;

        _logger.LogInformation("Creating {Count} new albums.", uncachedKeys.Count);
        foreach (var albumKey in uncachedKeys)
        {
            var definition = albumDefinitions[albumKey];
            var newAlbum = new Album { Title = definition.Title, Year = definition.Year };
            for (int i = 0; i < definition.ArtistNames.Count; i++)
            {
                newAlbum.AlbumArtists.Add(new AlbumArtist { Artist = artistLookup[definition.ArtistNames[i]], Order = i });
            }
            context.Albums.Add(newAlbum);
            albumLookup[albumKey] = newAlbum;
            albumIdCache.TryAdd(albumKey, newAlbum.Id);
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
        for (int i = 0; i < artistNames.Count; i++)
        {
            album.AlbumArtists.Add(new AlbumArtist
            {
                Artist = artistLookup[artistNames[i]],
                Order = i
            });
        }
    }

    /// <summary>
    ///     Adds or updates a song using scan-scoped caches for artists, albums, and genres.
    /// </summary>
    private void AddSongWithDetailsCached(
        MusicDbContext context,
        Guid folderId,
        SongFileMetadata metadata,
        Dictionary<string, Artist> artistLookup,
        Dictionary<(string Title, string Artist), Album> albumLookup,
        Dictionary<string, Genre> genreLookup,
        Song? existingSong = null,
        bool preserveMediaAssets = false)
    {
        var trackArtistNames = metadata.Artists.Select(ArtistNameHelper.Normalize).ToList();
        var albumArtistNames = metadata.AlbumArtists.Select(ArtistNameHelper.Normalize).ToList();

        if (albumArtistNames.Count == 0)
        {
            albumArtistNames = trackArtistNames.Count > 0
                ? new List<string>(trackArtistNames)
                : new List<string> { Artist.UnknownArtistName };
        }

        Album? album = null;
        if (!string.IsNullOrWhiteSpace(metadata.Album))
        {
            var albumTitle = NormalizeString(metadata.Album) ?? string.Empty;
            var albumKey = AlbumKey(albumTitle, albumArtistNames[0]);
            albumLookup.TryGetValue(albumKey, out album);

        }

        var genres = metadata.Genres
            .Select(NormalizeString)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => genreLookup[name!])
            .ToList();

        var directoryPath = _fileSystem.GetDirectoryName(metadata.FilePath) ?? string.Empty;

        var song = existingSong ?? new Song();

        song.FilePath = metadata.FilePath;
        song.DirectoryPath = directoryPath;
        song.Title = metadata.Title;
        song.DurationTicks = metadata.Duration.Ticks;
        if (!preserveMediaAssets)
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
        song.AlbumId = album?.Id;

        var newSongArtists = trackArtistNames
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
        song.MusicBrainzReleaseId = metadata.MusicBrainzReleaseId;
        song.ReplayGainTrackGain = metadata.ReplayGainTrackGain;
        song.ReplayGainTrackPeak = metadata.ReplayGainTrackPeak;

        if (existingSong == null)
        {
            song.DateAddedToLibrary = DateTime.UtcNow;
            context.Songs.Add(song);
        }

        if (album is not null && string.IsNullOrEmpty(album.CoverArtUri) && !string.IsNullOrEmpty(metadata.CoverArtUri))
        {
            album.CoverArtUri = metadata.CoverArtUri;
            // AutoDetectChangesEnabled=false: must explicitly mark the modified property.
            context.Entry(album).Property(a => a.CoverArtUri).IsModified = true;
        }

    }
}
