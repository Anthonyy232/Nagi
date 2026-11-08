using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Artwork;
using Nagi.Core.Data;
using Nagi.Core.Library;
using Nagi.Core.MusicServers;
using Nagi.Core.Platform;

namespace Nagi.Core.Playlists;

public class PlaylistService : IPlaylistService
{
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly PlaylistRules _rules;
    private readonly ILogger<PlaylistService> _logger;

    public PlaylistService(
        IDbContextFactory<MusicDbContext> contextFactory,
        IFileSystemService fileSystem,
        IImageProcessor imageProcessor,
        IPathConfiguration pathConfig,
        ILibraryNotifier libraryNotifier,
        ILogger<PlaylistService> logger)
    {
        _contextFactory = contextFactory;
        _rules = new PlaylistRules(fileSystem, imageProcessor, pathConfig, logger);
        _logger = logger;
        libraryNotifier.Changed += OnLibraryChanged;
    }

    public event EventHandler<PlaylistUpdatedEventArgs>? PlaylistUpdated;

    public event EventHandler? PlaylistsChanged;

    public async Task<IEnumerable<Playlist>> GetAllPlaylistsAsync(CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await context.Playlists.AsNoTracking()
            .Include(p => p.PlaylistSongs)
            .Include(p => p.ServerFolder)
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .ToListAsync(token).ConfigureAwait(false);
    }

    public async Task<Playlist?> GetPlaylistByIdAsync(Guid playlistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Playlists.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playlistId).ConfigureAwait(false);
    }

    public async Task<IEnumerable<Song>> GetSongsInPlaylistOrderedAsync(Guid playlistId, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var query = context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId)
            .OrderBy(ps => ps.Order).ThenBy(ps => ps.SongId)
            .Select(ps => ps.Song!);

        return await SongQuery.ExcludeHeavyFields(context, query)
            .AsSplitQuery()
            .ToListAsync(token).ConfigureAwait(false);
    }

    public async Task<int> GetPlaylistCountAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Playlists.CountAsync().ConfigureAwait(false);
    }

    public async Task<Guid?> GetRandomPlaylistIdAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Playlists.OrderBy(_ => EF.Functions.Random()).Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync().ConfigureAwait(false);
    }

    // Removing songs from the library removes them from their playlists, and removing a server removes its playlists.
    private void OnLibraryChanged(object? sender, LibraryChange change)
    {
        if (change.AddsOrRemovesSongs)
            PlaylistsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     Makes the server's playlists in the library match its list. Songs the library doesn't have from the server are
    ///     left out, and a song listed twice is kept once, where it first appears.
    /// </summary>
    /// <returns>True if a playlist was added, changed or removed.</returns>
    internal async Task<bool> SyncServerPlaylistsAsync(Guid folderId, IReadOnlyList<ServerPlaylist> playlists,
        CancellationToken cancellationToken)
    {
        bool changed;
        await using (var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var songIds = await context.Songs.Where(s => s.FolderId == folderId && s.RemoteId != null)
                .ToDictionaryAsync(s => s.RemoteId!, s => s.Id, cancellationToken).ConfigureAwait(false);
            var stored = await context.Playlists.Include(p => p.PlaylistSongs).Where(p => p.ServerFolderId == folderId)
                .ToDictionaryAsync(p => p.RemoteId!, cancellationToken).ConfigureAwait(false);

            changed = false;
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
        }

        if (changed) PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    public async Task<Playlist> CreatePlaylistAsync(string name, string? description = null,
        string? coverImageUri = null)
    {
        var normalizedName = PlaylistRules.NormalizeName(name);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        await PlaylistRules.ThrowIfNameTakenAsync(OwnNames(context, exceptId: null), normalizedName).ConfigureAwait(false);
        var playlist = new Playlist
        {
            Name = normalizedName,
            Description = description,
            DateCreated = DateTime.UtcNow,
            DateModified = DateTime.UtcNow
        };

        if (!string.IsNullOrEmpty(coverImageUri))
            (_, playlist.CoverImageUri) = await _rules.ReplaceCoverAsync(playlist.Id, coverImageUri).ConfigureAwait(false);

        context.Playlists.Add(playlist);
        await context.SaveChangesAsync().ConfigureAwait(false);
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        return playlist;
    }

    public async Task<string> FindFreeNameAsync(string name)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await PlaylistRules.FindFreeNameAsync(OwnNames(context, exceptId: null), name).ConfigureAwait(false);
    }

    public async Task<bool> DeletePlaylistAsync(Guid playlistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var playlist = await FindOwnPlaylistAsync(context, playlistId).ConfigureAwait(false);
        if (playlist is null) return false;

        context.Playlists.Remove(playlist);
        await context.SaveChangesAsync().ConfigureAwait(false);
        _rules.DeleteCoverOfRemovedPlaylist(playlistId);
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task<bool> RenamePlaylistAsync(Guid playlistId, string newName)
    {
        var normalizedName = PlaylistRules.NormalizeName(newName);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = await FindOwnPlaylistAsync(context, playlistId).ConfigureAwait(false);
        if (playlist is null) return false;

        await PlaylistRules.ThrowIfNameTakenAsync(OwnNames(context, playlistId), normalizedName).ConfigureAwait(false);
        playlist.Name = normalizedName;
        await SaveChangesAsync(context, playlist).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UpdatePlaylistCoverAsync(Guid playlistId, string? newCoverImageUri)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = await FindOwnPlaylistAsync(context, playlistId).ConfigureAwait(false);
        if (playlist is null) return false;

        var (replaced, cover) = await _rules.ReplaceCoverAsync(playlistId, newCoverImageUri).ConfigureAwait(false);
        if (!replaced) return false;

        playlist.CoverImageUri = cover;
        await SaveChangesAsync(context, playlist).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> AddSongsToPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds)
    {
        var uniqueSongIds = songIds.Distinct().ToArray();
        if (uniqueSongIds.Length == 0) return false;

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = await FindOwnPlaylistAsync(context, playlistId).ConfigureAwait(false);
        if (playlist is null) return false;

        var existingSongIds = await context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId)
            .Select(ps => ps.SongId)
            .ToHashSetAsync().ConfigureAwait(false);

        var songIdsToAdd = uniqueSongIds.Except(existingSongIds).ToList();
        if (songIdsToAdd.Count == 0) return true;

        var maxOrder = await context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId)
            .MaxAsync(ps => (double?)ps.Order).ConfigureAwait(false) ?? 0;

        var nextOrder = Math.Floor(maxOrder) + 1.0;

        var playlistSongsToAdd = songIdsToAdd.Select((songId, index) => new PlaylistSong
        {
            PlaylistId = playlistId,
            SongId = songId,
            Order = nextOrder + index
        });

        context.PlaylistSongs.AddRange(playlistSongsToAdd);
        await SaveChangesAsync(context, playlist).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RemoveSongsFromPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds)
    {
        var uniqueSongIds = songIds.Distinct().ToArray();
        if (uniqueSongIds.Length == 0) return false;

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = await FindOwnPlaylistAsync(context, playlistId).ConfigureAwait(false);
        if (playlist is null) return false;

        await context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId && uniqueSongIds.Contains(ps.SongId))
            .ExecuteDeleteAsync().ConfigureAwait(false);

        await SaveChangesAsync(context, playlist).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UpdatePlaylistOrderAsync(Guid playlistId, IEnumerable<Guid> orderedSongIds)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = await FindOwnPlaylistAsync(context, playlistId).ConfigureAwait(false);
        if (playlist is null) return false;

        var playlistSongs = await context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId)
            .ToListAsync().ConfigureAwait(false);

        var playlistSongMap = playlistSongs.ToDictionary(ps => ps.SongId);
        var orderedSongIdList = orderedSongIds.ToList();
        var updated = false;

        for (var i = 0; i < orderedSongIdList.Count; i++)
        {
            var songId = orderedSongIdList[i];
            if (playlistSongMap.TryGetValue(songId, out var playlistSong))
            {
                var newOrder = (double)i + 1.0;
                if (playlistSong.Order != newOrder)
                {
                    playlistSong.Order = newOrder;
                    updated = true;
                }
            }
        }

        if (updated)
        {
            await SaveChangesAsync(context, playlist).ConfigureAwait(false);
            _logger.LogInformation("Successfully normalized order for playlist {PlaylistId}", playlistId);
        }
        else
        {
            _logger.LogDebug("Normalization for playlist {PlaylistId} resulted in no changes.", playlistId);
        }

        return updated;
    }

    public async Task<bool> MoveSongAsync(Guid playlistId, Guid songId, Guid? previousSongId, Guid? nextSongId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = await FindOwnPlaylistAsync(context, playlistId).ConfigureAwait(false);
        if (playlist is null) return false;

        Guid[] ids = [songId, .. new[] { previousSongId, nextSongId }.OfType<Guid>()];
        var entries = await context.PlaylistSongs.Where(ps => ps.PlaylistId == playlistId && ids.Contains(ps.SongId))
            .ToDictionaryAsync(ps => ps.SongId).ConfigureAwait(false);
        if (!entries.TryGetValue(songId, out var moved)) return false;
        var previous = previousSongId is { } previousId ? entries.GetValueOrDefault(previousId)?.Order : null;
        var next = nextSongId is { } nextId ? entries.GetValueOrDefault(nextId)?.Order : null;

        if (OrderBetween(previous, next) is { } order)
        {
            moved.Order = order;
        }
        else
        {
            // No position is left between the neighbours, so the playlist is numbered again with the song in its new place.
            var others = await context.PlaylistSongs.Where(ps => ps.PlaylistId == playlistId && ps.SongId != songId)
                .OrderBy(ps => ps.Order).ThenBy(ps => ps.SongId).ToListAsync().ConfigureAwait(false);
            var index = nextSongId is { } before ? others.FindIndex(ps => ps.SongId == before) : -1;
            others.Insert(index < 0 ? others.Count : index, moved);
            for (var i = 0; i < others.Count; i++) others[i].Order = i + 1;
            _logger.LogDebug("Renumbered playlist {PlaylistId} to make room for song {SongId}.", playlistId, songId);
        }

        await SaveChangesAsync(context, playlist).ConfigureAwait(false);
        return true;
    }

    /// <returns>A position strictly between the two, or null when there is none.</returns>
    private static double? OrderBetween(double? previous, double? next)
    {
        var order = (previous, next) switch
        {
            (null, null) => 1,
            (null, { } n) => n > 0.5 ? n / 2 : n - 1,
            ({ } p, null) => Math.Floor(p) + 1,
            ({ } p, { } n) => p + (n - p) / 2
        };
        var tooClose = previous is { } low && next is { } high && high - low < 1e-9;
        return double.IsFinite(order) && !tooClose && order > (previous ?? double.NegativeInfinity) &&
               order < (next ?? double.PositiveInfinity)
            ? order
            : null;
    }

    /// <summary>
    ///     Saves a change to the playlist or its songs, stamping it as modified, and tells listeners.
    /// </summary>
    private async Task SaveChangesAsync(MusicDbContext context, Playlist playlist)
    {
        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);
        PlaylistUpdated?.Invoke(this, new PlaylistUpdatedEventArgs(playlist.Id, playlist.CoverImageUri, playlist.DateModified));
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <returns>The playlist, or null when it doesn't exist or comes from a music server.</returns>
    private static Task<Playlist?> FindOwnPlaylistAsync(MusicDbContext context, Guid playlistId) =>
        context.Playlists.FirstOrDefaultAsync(p => p.Id == playlistId && p.ServerFolderId == null);

    // The unique name index covers only Nagi's own playlists.
    private static IQueryable<string> OwnNames(MusicDbContext context, Guid? exceptId) =>
        context.Playlists.Where(p => p.Id != exceptId && p.ServerFolderId == null).Select(p => p.Name);
}
