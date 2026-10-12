using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Creates, edits, and reorders user playlists. Playlist reads live in <see cref="LibraryReader" />.
///     This service is designed to be a singleton and is internally thread-safe.
/// </summary>
public class PlaylistService : IPlaylistService
{
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly IFileSystemService _fileSystem;
    private readonly IImageProcessor _imageProcessor;
    private readonly IPathConfiguration _pathConfig;
    private readonly ILogger<PlaylistService> _logger;

    public PlaylistService(
        IDbContextFactory<MusicDbContext> contextFactory,
        IFileSystemService fileSystem,
        IImageProcessor imageProcessor,
        IPathConfiguration pathConfig,
        ILogger<PlaylistService> logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _imageProcessor = imageProcessor ?? throw new ArgumentNullException(nameof(imageProcessor));
        _pathConfig = pathConfig ?? throw new ArgumentNullException(nameof(pathConfig));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public event EventHandler<PlaylistUpdatedEventArgs>? PlaylistUpdated;

    /// <inheritdoc />
    public event EventHandler? PlaylistsChanged;

    /// <inheritdoc />
    public async Task<Playlist?> CreatePlaylistAsync(string name, string? description = null,
        string? coverImageUri = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(Resources.Strings.Error_PlaylistNameEmpty, nameof(name));

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = new Playlist
        {
            Name = NormalizeString(name) ?? name,
            Description = description,
            DateCreated = DateTime.UtcNow,
            DateModified = DateTime.UtcNow
        };

        if (!string.IsNullOrEmpty(coverImageUri) && _fileSystem.FileExists(coverImageUri))
        {
            var cachePath = _pathConfig.PlaylistImageCachePath;
            var originalBytes = await _fileSystem.ReadAllBytesAsync(coverImageUri).ConfigureAwait(false);
            var processedBytes = await _imageProcessor.ProcessImageBytesAsync(originalBytes).ConfigureAwait(false);
            playlist.CoverImageUri = await ImageStorageHelper.SaveImageBytesAsync(_fileSystem, cachePath,
                playlist.Id.ToString(), ".custom", processedBytes).ConfigureAwait(false);
        }

        context.Playlists.Add(playlist);
        await context.SaveChangesAsync().ConfigureAwait(false);
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        return playlist;
    }

    /// <inheritdoc />
    public async Task<bool> DeletePlaylistAsync(Guid playlistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var playlist = await context.Playlists.FindAsync(playlistId).ConfigureAwait(false);
        if (playlist is null) return false;

        context.Playlists.Remove(playlist);
        await context.SaveChangesAsync().ConfigureAwait(false);
        try
        {
            ImageStorageHelper.DeleteImage(_fileSystem, _pathConfig.PlaylistImageCachePath, playlistId.ToString(), ".custom");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete cached cover for deleted playlist {PlaylistId}.", playlistId);
        }
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RenamePlaylistAsync(Guid playlistId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException(Resources.Strings.Error_PlaylistNameEmpty, nameof(newName));

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = await context.Playlists.FindAsync(playlistId).ConfigureAwait(false);
        if (playlist is null) return false;

        playlist.Name = NormalizeString(newName) ?? newName;
        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);
        PlaylistUpdated?.Invoke(this, new PlaylistUpdatedEventArgs(playlist.Id, playlist.CoverImageUri, playlist.DateModified));
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> UpdatePlaylistCoverAsync(Guid playlistId, string? newCoverImageUri)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = await context.Playlists.FindAsync(playlistId).ConfigureAwait(false);
        if (playlist is null) return false;

        if (!string.IsNullOrEmpty(newCoverImageUri))
        {
            if (!_fileSystem.FileExists(newCoverImageUri)) return false;
            var cachePath = _pathConfig.PlaylistImageCachePath;
            var originalBytes = await _fileSystem.ReadAllBytesAsync(newCoverImageUri).ConfigureAwait(false);
            var processedBytes = await _imageProcessor.ProcessImageBytesAsync(originalBytes).ConfigureAwait(false);
            var newPath = await ImageStorageHelper.SaveImageBytesAsync(_fileSystem, cachePath,
                playlistId.ToString(), ".custom", processedBytes).ConfigureAwait(false);
            if (newPath is null) return false;
            playlist.CoverImageUri = newPath;
        }
        else
        {
            var cachePath = _pathConfig.PlaylistImageCachePath;
            ImageStorageHelper.DeleteImage(_fileSystem, cachePath, playlistId.ToString(), ".custom");
            playlist.CoverImageUri = null;
        }

        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);
        PlaylistUpdated?.Invoke(this, new PlaylistUpdatedEventArgs(playlist.Id, playlist.CoverImageUri, playlist.DateModified));
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> AddSongsToPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds)
    {
        var uniqueSongIds = songIds.Distinct().ToArray();
        if (uniqueSongIds.Length == 0) return false;

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = await context.Playlists.FindAsync(playlistId).ConfigureAwait(false);
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
        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveSongsFromPlaylistAsync(Guid playlistId, IEnumerable<Guid> songIds)
    {
        var uniqueSongIds = songIds.Distinct().ToArray();
        if (uniqueSongIds.Length == 0) return false;

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = await context.Playlists.FindAsync(playlistId).ConfigureAwait(false);
        if (playlist is null) return false;

        var rowsDeleted = await context.PlaylistSongs
            .Where(ps => ps.PlaylistId == playlistId && uniqueSongIds.Contains(ps.SongId))
            .ExecuteDeleteAsync().ConfigureAwait(false);

        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);
        if (rowsDeleted > 0)
        {
            PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        }
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> UpdatePlaylistOrderAsync(Guid playlistId, IEnumerable<Guid> orderedSongIds)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = await context.Playlists.FindAsync(playlistId).ConfigureAwait(false);
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
            playlist.DateModified = DateTime.UtcNow;
            await context.SaveChangesAsync().ConfigureAwait(false);
            _logger.LogInformation("Successfully normalized order for playlist {PlaylistId}", playlistId);
        }
        else
        {
            _logger.LogDebug("Normalization for playlist {PlaylistId} resulted in no changes.", playlistId);
        }

        return updated;
    }

    /// <inheritdoc />
    public async Task<bool> MovePlaylistSongAsync(Guid playlistId, Guid songId, double newOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var playlistSong = await context.PlaylistSongs
            .Include(ps => ps.Playlist)
            .FirstOrDefaultAsync(ps => ps.PlaylistId == playlistId && ps.SongId == songId)
            .ConfigureAwait(false);

        if (playlistSong is null) return false;

        playlistSong.Order = newOrder;
        _logger.LogDebug("Saving new order {Order} for song {SongId} in playlist {PlaylistId}", newOrder, songId, playlistId);

        playlistSong.Playlist.DateModified = DateTime.UtcNow;

        await context.SaveChangesAsync().ConfigureAwait(false);
        _logger.LogDebug("Successfully moved song {SongId} in playlist {PlaylistId} to order {Order}", songId, playlistId, newOrder);
        return true;
    }

    private static string? NormalizeString(string? s) => ArtistNameHelper.NormalizeStringCore(s);
}
