using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Settings;

namespace Nagi.Core.Services.Implementations.MusicServers;

public sealed class ServerLibrarySync : IServerLibrarySync
{
    // One sync per server at a time; a second one, such as Sync pressed during a background sync, waits its turn.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _syncLocks = new();
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly IFileSystemService _fileSystem;
    private readonly IImageProcessor _imageProcessor;
    private readonly ISettingsService _settingsService;
    private readonly LibraryImporter _importer;
    private readonly ArtworkService _artwork;
    private readonly PlaylistService _playlists;
    private readonly LibraryNotifier _notifier;
    private readonly LibraryWriteLock _writeLock;
    private readonly ILogger<ServerLibrarySync> _logger;

    public ServerLibrarySync(
        IDbContextFactory<MusicDbContext> contextFactory,
        IFileSystemService fileSystem,
        IImageProcessor imageProcessor,
        ISettingsService settingsService,
        LibraryImporter importer,
        ArtworkService artwork,
        PlaylistService playlists,
        LibraryNotifier notifier,
        LibraryWriteLock writeLock,
        ILogger<ServerLibrarySync> logger)
    {
        _contextFactory = contextFactory;
        _fileSystem = fileSystem;
        _imageProcessor = imageProcessor;
        _settingsService = settingsService;
        _importer = importer;
        _artwork = artwork;
        _playlists = playlists;
        _notifier = notifier;
        _writeLock = writeLock;
        _logger = logger;
    }

    public async Task<Folder> AddAsync(MusicServer server, string name)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        // Server roots need a unique path too, so they get a key that can never be a real file path.
        server.Folder = new Folder { Id = server.FolderId, Name = name, Path = $"nagi-server:{server.FolderId:N}" };
        context.MusicServers.Add(server);
        await context.SaveChangesAsync().ConfigureAwait(false);
        _notifier.Raise(LibraryChangeKind.RootAdded, server.FolderId);
        return server.Folder;
    }

    public async Task<bool> RemoveAsync(Guid folderId)
    {
        var token = _writeLock.ShutdownToken;
        _writeLock.CancelActiveScan("server removal");
        _writeLock.CancelServerSync(folderId);
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!await IsMusicServerFolderAsync(folderId, token).ConfigureAwait(false)
                || !await _importer.RemoveRootAsync(folderId, token).ConfigureAwait(false)) return false;
        }
        finally
        {
            _writeLock.Release();
        }

        _notifier.Raise(LibraryChangeKind.RootRemoved, folderId);
        return true;
    }

    public async Task SyncAsync(Guid folderId, IMusicServerClient client, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _writeLock.ShutdownToken);
        var operationToken = operationCts.Token;
        var syncLock = _syncLocks.GetOrAdd(folderId, _ => new SemaphoreSlim(1, 1));
        await syncLock.WaitAsync(operationToken).ConfigureAwait(false);
        _writeLock.SetServerSync(folderId, operationCts);

        try
        {
            // Removed while this sync waited for the one before it.
            if (!await IsMusicServerFolderAsync(folderId, operationToken).ConfigureAwait(false))
                throw new OperationCanceledException($"Server folder {folderId} was removed.");

            // Reading from the server happens outside the write lock, so a slow server doesn't hold up folder scans.
            var songs = new List<ServerSongMetadata>();
            progress?.Report(FetchingProgress(0));
            await foreach (var page in client.GetSongsAsync(operationToken).ConfigureAwait(false))
            {
                songs.AddRange(page);
                progress?.Report(FetchingProgress(songs.Count));
            }

            var playlists = await client.GetPlaylistsAsync(operationToken).ConfigureAwait(false);

            ServerImport import;
            await _writeLock.WaitAsync(operationToken).ConfigureAwait(false);
            try
            {
                import = await ImportSongsAsync(folderId, songs, progress, operationToken).ConfigureAwait(false);
                await _playlists.SyncServerPlaylistsAsync(folderId, playlists, operationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            var importChanged = import.Added + import.Updated + import.Removed > 0;
            // The songs show up now rather than after their covers, which can take a while to download.
            if (importChanged) _notifier.Raise(LibraryChangeKind.FolderRescanned, folderId);

            var covers = await DownloadCoversAsync(folderId, client, import.SongsNeedingCovers, progress, operationToken)
                .ConfigureAwait(false);
            var coverUpdates = covers;
            // A retagged song can bring its cover to an album that had none, so this runs after any change.
            if (importChanged || covers > 0)
            {
                // Like scans, under the lock: replacing an album cover can delete a file a running scan would reuse.
                await _writeLock.WaitAsync(operationToken).ConfigureAwait(false);
                try
                {
                    var albumCovers = await _artwork.UpdateAlbumCoversAsync(folderId, import.ReplacedCovers, false, operationToken)
                        .ConfigureAwait(false);
                    coverUpdates += albumCovers.Albums + albumCovers.Tracks;
                }
                finally
                {
                    _writeLock.Release();
                }
            }

            if (coverUpdates > 0) _notifier.Raise(LibraryChangeKind.FolderRescanned, folderId);
            if (importChanged) _ = _artwork.StartMetadataBackgroundFetchAsync();

            _logger.LogInformation("Synced server folder {FolderId}: +{Added} ~{Updated} -{Removed}, {Covers} songs got covers, {Playlists} playlists.",
                folderId, import.Added, import.Updated, import.Removed, covers, playlists.Count);
            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_SyncComplete, Percentage = 100 });
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_ScanCancelled, Percentage = 100 });
            throw;
        }
        finally
        {
            _writeLock.ClearServerSync(folderId);
            syncLock.Release();
        }
    }

    private static ScanProgress FetchingProgress(int count) => new()
    {
        StatusText = string.Format(Resources.Strings.Format_FetchingServerSongs, count.ToString("N0")),
        IsIndeterminate = true
    };

    /// <param name="ReplacedCovers">The cached covers of songs whose cover downloads again, to delete once unused.</param>
    private sealed record ServerImport(int Added, int Updated, int Removed, List<ServerSongMetadata> SongsNeedingCovers,
        List<string> ReplacedCovers);

    /// <summary>
    ///     Makes the folder's songs match the server's list: adds new songs, updates the ones whose details changed,
    ///     and removes the ones it no longer lists.
    /// </summary>
    private async Task<ServerImport> ImportSongsAsync(Guid folderId, List<ServerSongMetadata> songs,
        IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        string rootPath;
        Dictionary<string, StoredSong> existing;
        await using (var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            rootPath = await context.Folders.Where(f => f.Id == folderId).Select(f => f.Path)
                           .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                       // Removed while this sync waited for the write lock.
                       ?? throw new OperationCanceledException($"Server folder {folderId} was removed.");
            existing = await context.Songs.Where(s => s.FolderId == folderId)
                .Select(s => new StoredSong(s.FilePath, s.RemoteHash, s.AlbumArtUriFromTrack, s.RemoteCoverArtId))
                .ToDictionaryAsync(s => s.FilePath, StringComparer.OrdinalIgnoreCase, cancellationToken)
                .ConfigureAwait(false);
        }

        var artistSplitCharacters = _settingsService.Get(CoreSettings.ArtistSplitCharacters);
        var genreSplitCharacters = _settingsService.Get(CoreSettings.GenreSplitCharacters);
        foreach (var song in songs)
        {
            // Under the root, so folder browsing lists the song there, and escaped, so an ID with a slash can't make subfolders.
            // The file's extension comes last, so the song is played and shown like a file of its type. Dots in the ID are
            // escaped too, so an ID such as "track.flac" can't pass for an extension.
            var extension = song.FileExtension is { } suffix ? "." + Uri.EscapeDataString(suffix.ToLowerInvariant()) : string.Empty;
            song.FilePath = $"{rootPath}{Path.DirectorySeparatorChar}{Uri.EscapeDataString(song.RemoteId).Replace(".", "%2E")}{extension}";
            // There's no file, so the date the server added the song stands in for its creation date in sorts and smart playlists.
            song.FileCreatedDate = song.AddedUtc;
            // Cleaned up and defaulted like tags read from files, with the user's split characters applied on top of the
            // server's own lists of names. This comes before hashing, so changing the split characters updates the
            // songs on the next sync.
            song.Title = ArtistNameHelper.NormalizeStringCore(song.Title) ?? Song.UnknownTitle;
            song.Album = ArtistNameHelper.NormalizeStringCore(song.Album) ?? Album.UnknownAlbumName;
            var artists = ArtistNameHelper.SplitNames(song.Artists, artistSplitCharacters);
            song.Artists = artists.Count > 0 ? artists : [Artist.UnknownArtistName];
            song.AlbumArtists = ArtistNameHelper.SplitNames(song.AlbumArtists, artistSplitCharacters);
            song.Genres = ArtistNameHelper.SplitNames(song.Genres, genreSplitCharacters);
        }
        // A song can come back twice when the server's list changes between two pages.
        var listed = songs.DistinctBy(song => song.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
        var existingPaths = existing.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toImport = listed.Where(song => !existing.TryGetValue(song.FilePath, out var stored) || stored.RemoteHash != song.ComputeHash())
            .ToList();
        var added = toImport.Count(song => !existingPaths.Contains(song.FilePath));
        if (toImport.Count > 0)
        {
            // The server has no cover or lyrics paths, so a song keeps the ones it already has.
            await using var import = await _importer.BeginAsync(folderId, existingPaths, _ => true, cancellationToken)
                .ConfigureAwait(false);
            var saved = 0;
            foreach (var batch in toImport.Chunk(50))
            {
                await import.WriteBatchAsync(batch, cancellationToken, ApplyServerDetails).ConfigureAwait(false);
                saved += batch.Length;
                progress?.Report(new ScanProgress
                { StatusText = Resources.Strings.Status_AddingServerSongs, Percentage = saved * 100.0 / toImport.Count });
            }
        }

        var listedPaths = listed.Select(song => song.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gone = existingPaths.Where(path => !listedPaths.Contains(path)).ToList();
        if (gone.Count > 0 && (listed.Count == 0 || LibraryImporter.IsLikelyMissedListing(gone.Count, existing.Count, added)))
        {
            _logger.LogError(
                "Sync safety guard tripped for server folder {FolderId}: it listed {ListedCount} songs, so {DeleteCount}/{ExistingCount} would be removed. Keeping them; the server likely has a library offline.",
                folderId, listed.Count, gone.Count, existing.Count);
            gone.Clear();
        }

        var removed = gone.Count > 0 ? await _importer.DeleteSongsAsync(folderId, gone, cancellationToken).ConfigureAwait(false) : 0;
        // Added songs can't leave an artist, album or genre without songs, as with a scan.
        if (toImport.Count > added || removed > 0)
            await _importer.CleanUpOrphansAsync(cancellationToken).ConfigureAwait(false);

        // A cover downloads again when the server changed it, or when it's gone from the cache, as a scan re-reads a
        // song whose cover is gone.
        var coversOnDisk = existing.Values.Select(song => song.AlbumArtUriFromTrack).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        coversOnDisk.RemoveWhere(uri => !_fileSystem.FileExists(uri));
        var songsNeedingCovers = listed.Where(song => song.CoverArtId is not null &&
                !(existing.TryGetValue(song.FilePath, out var stored) && stored.RemoteCoverArtId == song.CoverArtId &&
                  stored.AlbumArtUriFromTrack is { } uri && coversOnDisk.Contains(uri)))
            .ToList();
        var replacedCovers = songsNeedingCovers.Select(song => existing.GetValueOrDefault(song.FilePath)?.AlbumArtUriFromTrack)
            .OfType<string>().ToList();
        return new ServerImport(added, toImport.Count - added, removed, songsNeedingCovers, replacedCovers);
    }

    private sealed record StoredSong(string FilePath, string? RemoteHash, string? AlbumArtUriFromTrack, string? RemoteCoverArtId);

    private static void ApplyServerDetails(Song song, ServerSongMetadata remote, bool isNew)
    {
        song.RemoteId = remote.RemoteId;
        song.RemoteHash = remote.ComputeHash();
        // The server counts plays and keeps favorites from every app the user listens with, so its values win,
        // except for a favorite changed here that the server hasn't been told about yet.
        song.PlayCount = remote.PlayCount;
        song.LastPlayedDate = remote.LastPlayedUtc;
        if (!song.IsLovedSyncPending) song.IsLoved = remote.IsFavorite;
        if (isNew && remote.AddedUtc is { } addedUtc) song.DateAddedToLibrary = addedUtc;
    }

    /// <summary>
    ///     Downloads each needed cover once and gives it to every song that uses it.
    /// </summary>
    /// <returns>The number of songs that got a cover.</returns>
    private async Task<int> DownloadCoversAsync(Guid folderId, IMusicServerClient client,
        List<ServerSongMetadata> songsNeedingCovers, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        const int maxConcurrentDownloads = 4;
        var covers = songsNeedingCovers.GroupBy(song => song.CoverArtId!).ToList();
        var done = 0;
        var updated = 0;
        using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await Parallel.ForEachAsync(covers,
                new ParallelOptions { MaxDegreeOfParallelism = maxConcurrentDownloads, CancellationToken = downloadCts.Token },
                async (cover, token) =>
                {
                    try
                    {
                        Interlocked.Add(ref updated, await ApplyCoverAsync(folderId, client, cover.Key,
                            cover.Select(song => song.FilePath).ToList(), token).ConfigureAwait(false));
                    }
                    catch (MusicServerException ex) when (ex.Error is MusicServerError.Unreachable
                                                              or MusicServerError.SecureConnectionFailed
                                                              or MusicServerError.SignInFailed)
                    {
                        // Every remaining cover would wait for the same failure, so the next sync tries them instead.
                        _logger.LogWarning(ex, "Stopped downloading covers for server folder {FolderId}.", folderId);
                        await downloadCts.CancelAsync().ConfigureAwait(false);
                    }
                    catch (MusicServerException ex)
                    {
                        _logger.LogDebug(ex, "Couldn't download cover {CoverArtId} for server folder {FolderId}.", cover.Key, folderId);
                    }

                    progress?.Report(new ScanProgress
                    {
                        StatusText = Resources.Strings.Status_DownloadingServerCovers,
                        Percentage = Interlocked.Increment(ref done) * 100.0 / covers.Count
                    });
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        return updated;
    }

    private async Task<int> ApplyCoverAsync(Guid folderId, IMusicServerClient client, string coverArtId,
        List<string> filePaths, CancellationToken cancellationToken)
    {
        var image = await client.GetCoverArtAsync(coverArtId, cancellationToken).ConfigureAwait(false);
        if (image is null) return 0;

        var (uri, lightSwatchId, darkSwatchId) = await _imageProcessor.SaveCoverArtAndExtractColorsAsync(image).ConfigureAwait(false);
        if (uri is null) return 0;

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Songs.Where(s => s.FolderId == folderId && filePaths.Contains(s.FilePath))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.AlbumArtUriFromTrack, uri)
                .SetProperty(s => s.LightSwatchId, lightSwatchId)
                .SetProperty(s => s.DarkSwatchId, darkSwatchId)
                .SetProperty(s => s.RemoteCoverArtId, coverArtId), cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> IsMusicServerFolderAsync(Guid folderId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.MusicServers.AnyAsync(s => s.FolderId == folderId, cancellationToken).ConfigureAwait(false);
    }
}
