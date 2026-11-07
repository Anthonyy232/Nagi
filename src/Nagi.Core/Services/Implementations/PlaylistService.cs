using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Helpers;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Creates, edits, and reorders user playlists. Playlist reads live in <see cref="LibraryReader" />.
/// </summary>
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
        ILogger<PlaylistService> logger)
    {
        _contextFactory = contextFactory;
        _rules = new PlaylistRules(fileSystem, imageProcessor, pathConfig, logger);
        _logger = logger;
    }

    public event EventHandler<PlaylistUpdatedEventArgs>? PlaylistUpdated;

    public event EventHandler? PlaylistsChanged;

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

    public async Task<bool> MovePlaylistSongAsync(Guid playlistId, Guid songId, double newOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var playlistSong = await context.PlaylistSongs
            .Include(ps => ps.Playlist)
            .FirstOrDefaultAsync(ps => ps.PlaylistId == playlistId && ps.SongId == songId && ps.Playlist.ServerFolderId == null)
            .ConfigureAwait(false);

        if (playlistSong is null) return false;

        playlistSong.Order = newOrder;
        _logger.LogDebug("Saving new order {Order} for song {SongId} in playlist {PlaylistId}", newOrder, songId, playlistId);

        await SaveChangesAsync(context, playlistSong.Playlist).ConfigureAwait(false);
        _logger.LogDebug("Successfully moved song {SongId} in playlist {PlaylistId} to order {Order}", songId, playlistId, newOrder);
        return true;
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
