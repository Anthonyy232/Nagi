using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Music servers: their library roots, and mirroring their songs and covers into the library.
/// </summary>
public partial class LibraryService
{
    // One sync per server at a time; a second one, such as Sync pressed during a background sync, waits its turn.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _serverSyncLocks = new();

    // Removing a server cancels its sync, which would otherwise hold the removal up until the import finished.
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _serverSyncs = new();

    public async Task<Folder> AddMusicServerAsync(MusicServer server, string name)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        // Server roots need a unique path too, so they get a key that can never be a real file path.
        server.Folder = new Folder { Id = server.FolderId, Name = name, Path = $"nagi-server:{server.FolderId:N}" };
        context.MusicServers.Add(server);
        await context.SaveChangesAsync().ConfigureAwait(false);
        LibraryContentChanged?.Invoke(this, new LibraryContentChangedEventArgs(LibraryChangeType.FolderAdded, server.FolderId));
        return server.Folder;
    }

    public async Task<bool> SyncServerFolderAsync(Guid folderId, IMusicServerClient client,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        var operationToken = operationCts.Token;
        var syncLock = _serverSyncLocks.GetOrAdd(folderId, _ => new SemaphoreSlim(1, 1));
        await syncLock.WaitAsync(operationToken).ConfigureAwait(false);
        _serverSyncs[folderId] = operationCts;

        try
        {
            // Removed while this sync waited for the one before it.
            if (!await IsMusicServerFolderAsync(folderId, operationToken).ConfigureAwait(false))
                throw new OperationCanceledException($"Server folder {folderId} was removed.");

            // Reading from the server happens outside the scan lock, so a slow server doesn't hold up folder scans.
            var songs = new List<ServerSongMetadata>();
            progress?.Report(FetchingProgress(0));
            await foreach (var page in client.GetSongsAsync(operationToken).ConfigureAwait(false))
            {
                songs.AddRange(page);
                progress?.Report(FetchingProgress(songs.Count));
            }

            var playlists = await client.GetPlaylistsAsync(operationToken).ConfigureAwait(false);

            ServerImport import;
            bool playlistsChanged;
            await _scanSemaphore.WaitAsync(operationToken).ConfigureAwait(false);
            try
            {
                import = await ImportServerSongsAsync(folderId, songs, progress, operationToken).ConfigureAwait(false);
                playlistsChanged = await ImportServerPlaylistsAsync(folderId, playlists, operationToken).ConfigureAwait(false);
            }
            finally
            {
                _scanSemaphore.Release();
            }

            var importChanged = import.Added + import.Updated + import.Removed > 0;
            // The songs show up now rather than after their covers, which can take a while to download.
            if (importChanged || playlistsChanged)
                LibraryContentChanged?.Invoke(this, new LibraryContentChangedEventArgs(LibraryChangeType.FolderRescanned, folderId));

            var covers = await DownloadServerCoversAsync(folderId, client, import.SongsNeedingCovers, progress, operationToken)
                .ConfigureAwait(false);
            var coverUpdates = covers;
            // A retagged song can bring its cover to an album that had none, so this runs after any change.
            if (importChanged || covers > 0)
            {
                // Like scans, under the lock: replacing an album cover can delete a file a running scan would reuse.
                await _scanSemaphore.WaitAsync(operationToken).ConfigureAwait(false);
                try
                {
                    coverUpdates += await SyncAlbumCoversWithTracksAsync(folderId, operationToken).ConfigureAwait(false);
                    coverUpdates += await ApplyAlbumCoversToTracksWithoutArtAsync(folderId, operationToken).ConfigureAwait(false);
                    await using var context = await _contextFactory.CreateDbContextAsync(operationToken).ConfigureAwait(false);
                    await DeleteUnreferencedCoverArtAsync(context, import.ReplacedCovers, operationToken).ConfigureAwait(false);
                }
                finally
                {
                    _scanSemaphore.Release();
                }
            }

            if (coverUpdates > 0)
                LibraryContentChanged?.Invoke(this, new LibraryContentChangedEventArgs(LibraryChangeType.FolderRescanned, folderId));
            if (importChanged)
                _ = StartMetadataBackgroundFetchAsync();

            _logger.LogInformation("Synced server folder {FolderId}: +{Added} ~{Updated} -{Removed}, {Covers} songs got covers, {Playlists} playlists.",
                folderId, import.Added, import.Updated, import.Removed, covers, playlists.Count);
            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_SyncComplete, Percentage = 100 });
            return importChanged || playlistsChanged || coverUpdates > 0;
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_ScanCancelled, Percentage = 100 });
            throw;
        }
        finally
        {
            _serverSyncs.TryRemove(folderId, out _);
            syncLock.Release();
        }
    }

    private void CancelServerSync(Guid folderId)
    {
        if (!_serverSyncs.TryGetValue(folderId, out var sync)) return;
        try
        {
            sync.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The sync finished while it was being cancelled.
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
    private async Task<ServerImport> ImportServerSongsAsync(Guid folderId, List<ServerSongMetadata> songs,
        IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        context.ChangeTracker.AutoDetectChangesEnabled = false;

        var rootPath = await context.Folders.Where(f => f.Id == folderId).Select(f => f.Path)
                           .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                       // Removed while this sync waited for the scan lock.
                       ?? throw new OperationCanceledException($"Server folder {folderId} was removed.");
        var artistSplitCharacters = await _settingsService.GetArtistSplitCharactersAsync().ConfigureAwait(false);
        var genreSplitCharacters = await _settingsService.GetGenreSplitCharactersAsync().ConfigureAwait(false);
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

        var existing = await context.Songs.Where(s => s.FolderId == folderId)
            .Select(s => new { s.FilePath, s.RemoteHash, s.AlbumArtUriFromTrack, s.RemoteCoverArtId })
            .ToDictionaryAsync(s => s.FilePath, StringComparer.OrdinalIgnoreCase, cancellationToken)
            .ConfigureAwait(false);
        var existingPaths = existing.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toImport = listed.Where(song => !existing.TryGetValue(song.FilePath, out var stored) || stored.RemoteHash != song.ComputeHash())
            .ToList();
        var added = toImport.Count(song => !existingPaths.Contains(song.FilePath));
        if (toImport.Count > 0)
        {
            const int batchSize = 50;
            var caches = await LoadImportCachesAsync(context, cancellationToken).ConfigureAwait(false);
            var saved = 0;
            foreach (var batch in toImport.Chunk(batchSize))
            {
                // The server has no cover or lyrics paths, so a song keeps the ones it already has.
                saved += await ProcessSingleBatchAsync(folderId, batch, context, caches,
                    filePathsBeingUpdated: existingPaths, unchangedFiles: existingPaths,
                    replacedCovers: new HashSet<string>(), cancellationToken).ConfigureAwait(false);
                progress?.Report(new ScanProgress
                { StatusText = Resources.Strings.Status_AddingServerSongs, Percentage = saved * 100.0 / toImport.Count });
            }
        }

        var listedPaths = listed.Select(song => song.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gone = existingPaths.Where(path => !listedPaths.Contains(path)).ToList();
        if (gone.Count > 0 && (listed.Count == 0 || IsLikelyMissedListing(gone.Count, existing.Count, added)))
        {
            _logger.LogError(
                "Sync safety guard tripped for server folder {FolderId}: it listed {ListedCount} songs, so {DeleteCount}/{ExistingCount} would be removed. Keeping them; the server likely has a library offline.",
                folderId, listed.Count, gone.Count, existing.Count);
            gone.Clear();
        }

        var removed = gone.Count > 0 ? await DeleteSongsAsync(folderId, gone, cancellationToken).ConfigureAwait(false) : 0;
        // Added songs can't leave an artist, album or genre without songs, as with a scan.
        if (toImport.Count > added || removed > 0)
            DeleteOrphanedArtistImages(await CleanUpOrphanedEntitiesAsync(context, cancellationToken).ConfigureAwait(false));

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

    /// <summary>
    ///     Makes the server's playlists in the library match its list. Songs the library doesn't have from the server are
    ///     left out, and a song listed twice is kept once, where it first appears.
    /// </summary>
    /// <returns>True if a playlist was added, changed or removed.</returns>
    private async Task<bool> ImportServerPlaylistsAsync(Guid folderId, IReadOnlyList<ServerPlaylist> playlists,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var songIds = await context.Songs.Where(s => s.FolderId == folderId && s.RemoteId != null)
            .ToDictionaryAsync(s => s.RemoteId!, s => s.Id, cancellationToken).ConfigureAwait(false);
        var stored = await context.Playlists.Include(p => p.PlaylistSongs).Where(p => p.ServerFolderId == folderId)
            .ToDictionaryAsync(p => p.RemoteId!, cancellationToken).ConfigureAwait(false);

        var changed = false;
        foreach (var listed in playlists)
        {
            stored.Remove(listed.RemoteId, out var playlist);
            var playlistChanged = playlist?.Name != listed.Name;
            if (playlist is null)
            {
                playlist = new Playlist
                {
                    ServerFolderId = folderId,
                    RemoteId = listed.RemoteId,
                    DateCreated = listed.CreatedUtc ?? DateTime.UtcNow
                };
                context.Playlists.Add(playlist);
            }

            playlist.Name = listed.Name;
            var entries = playlist.PlaylistSongs.ToDictionary(ps => ps.SongId);
            var order = 0;
            foreach (var songId in listed.SongIds.Select(id => songIds.GetValueOrDefault(id)).Where(id => id != Guid.Empty).Distinct())
            {
                order++;
                if (entries.Remove(songId, out var entry))
                {
                    if (entry.Order == order) continue;
                }
                else
                {
                    entry = new PlaylistSong { SongId = songId };
                    playlist.PlaylistSongs.Add(entry);
                }

                entry.Order = order;
                playlistChanged = true;
            }

            foreach (var gone in entries.Values) playlist.PlaylistSongs.Remove(gone);
            if (!playlistChanged && entries.Count == 0) continue;
            playlist.DateModified = DateTime.UtcNow;
            changed = true;
        }

        context.Playlists.RemoveRange(stored.Values);
        changed |= stored.Count > 0;
        if (changed) await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    /// <summary>
    ///     Downloads each needed cover once and gives it to every song that uses it.
    /// </summary>
    /// <returns>The number of songs that got a cover.</returns>
    private async Task<int> DownloadServerCoversAsync(Guid folderId, IMusicServerClient client,
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
                        Interlocked.Add(ref updated, await ApplyServerCoverAsync(folderId, client, cover.Key,
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

    private async Task<int> ApplyServerCoverAsync(Guid folderId, IMusicServerClient client, string coverArtId,
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
