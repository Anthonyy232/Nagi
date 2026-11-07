using Microsoft.EntityFrameworkCore;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Helpers;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Read-only queries over the music library. This service is designed to be a singleton and is internally
///     thread-safe.
/// </summary>
public partial class LibraryReader : ILibraryReader
{
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly IFileSystemService _fileSystem;
    private readonly ISettingsService _settingsService;

    public LibraryReader(
        IDbContextFactory<MusicDbContext> contextFactory,
        IFileSystemService fileSystem,
        ISettingsService settingsService)
    {
        _contextFactory = contextFactory;
        _fileSystem = fileSystem;
        _settingsService = settingsService;
    }

    public async Task<Folder?> GetFolderByIdAsync(Guid folderId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == folderId).ConfigureAwait(false);
    }

    public async Task<Folder?> GetFolderByPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var normalizedPath = _fileSystem.NormalizePath(path);
        if (string.IsNullOrEmpty(normalizedPath)) return null;

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Path == normalizedPath).ConfigureAwait(false);
    }

    public async Task<bool> HasAnyFolderAsync(CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Folders.AnyAsync(token).ConfigureAwait(false);
    }

    public async Task<IEnumerable<FolderSummary>> GetRootFoldersWithCountsAsync(CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await context.Folders.AsNoTracking()
            .Where(f => f.ParentFolderId == null)
            .Select(f => new FolderSummary(f, f.Songs.Count, f.MusicServer != null))
            .ToListAsync(token).ConfigureAwait(false);
    }

    private static Task<Guid?> GetRandomEntityIdAsync<T>(IQueryable<T> query) where T : class =>
        query.OrderBy(_ => EF.Functions.Random())
            .Select(e => (Guid?)EF.Property<Guid>(e, "Id"))
            .FirstOrDefaultAsync();

    public async Task<Song?> GetSongByIdAsync(Guid songId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Songs.AsNoTracking()
            .Include(s => s.SongArtists).ThenInclude(sa => sa.Artist)
            .Include(s => s.Album).ThenInclude(a => a!.AlbumArtists).ThenInclude(aa => aa.Artist)
            .Include(s => s.Folder)
            .Include(s => s.Genres)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == songId).ConfigureAwait(false);
    }

    public async Task<Song?> GetSongByFilePathAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Songs.AsNoTracking()
            .Include(s => s.Album)
            .Include(s => s.Folder)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.FilePath == filePath).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<Guid, Song>> GetSongsByIdsAsync(IEnumerable<Guid> songIds)
    {
        var result = new Dictionary<Guid, Song>();
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        foreach (var chunk in songIds.Distinct().Chunk(500))
        {

            var query = context.Songs.AsNoTracking()
                .Where(s => chunk.Contains(s.Id))
                .Include(s => s.Album);

            var batch = await SongQuery.ExcludeHeavyFields(context, query)
                .AsSplitQuery()
                .ToListAsync().ConfigureAwait(false);

            foreach (var song in batch)
            {
                result[song.Id] = song;
            }
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, Song>> GetQueueSongsAsync(IEnumerable<Guid> songIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, Song>();
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        foreach (var chunk in songIds.Distinct().Chunk(500))
        {
            var songs = await context.Songs.AsNoTracking().Where(s => chunk.Contains(s.Id))
                .Select(s => new Song
                {
                    Id = s.Id,
                    Title = s.Title,
                    ArtistName = s.ArtistName,
                    DurationTicks = s.DurationTicks,
                    Genres = s.Genres,
                    SongArtists = s.SongArtists.Select(sa => new SongArtist
                    {
                        Order = sa.Order,
                        Artist = new Artist { Id = sa.Artist.Id, Name = sa.Artist.Name }
                    }).ToList()
                }).AsSplitQuery().ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var song in songs) result[song.Id] = song;
        }
        return result;
    }

    public async Task<TimeSpan> GetSongsDurationAsync(IEnumerable<Guid> songIds,
        CancellationToken cancellationToken = default)
    {
        long ticks = 0;
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        foreach (var chunk in songIds.Distinct().Chunk(500))
            ticks += await context.Songs.Where(s => chunk.Contains(s.Id))
                .SumAsync(s => s.DurationTicks, cancellationToken).ConfigureAwait(false);
        return TimeSpan.FromTicks(ticks);
    }

    public async Task<Artist?> GetArtistByNameAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var normalizedName = ArtistNameHelper.Normalize(name);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Artists.AsNoTracking().FirstOrDefaultAsync(a => a.Name == normalizedName).ConfigureAwait(false);
    }

    public async Task<IEnumerable<Album>> GetAlbumsForArtistAsync(Guid artistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var albumIds = context.AlbumArtists
            .Where(aa => aa.ArtistId == artistId)
            .Select(aa => (Guid?)aa.AlbumId)
            .Union(context.SongArtists.Where(sa => sa.ArtistId == artistId).Select(sa => sa.Song.AlbumId));

        return await context.Albums.AsNoTracking()
            .Where(album => albumIds.Contains(album.Id))
            .OrderByDescending(album => album.Songs.Sum(s => s.PlayCount))
            .ThenByDescending(album => album.Songs.Count)
            .ThenBy(album => album.Id)
            .ToListAsync().ConfigureAwait(false);
    }

    public async Task<IEnumerable<Artist>> GetArtistsForSongAsync(Guid songId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.SongArtists.AsNoTracking()
            .Where(sa => sa.SongId == songId)
            .OrderBy(sa => sa.Order)
            .Select(sa => new Artist
            {
                Id = sa.Artist.Id,
                Name = sa.Artist.Name
            })
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public async Task<Album?> GetAlbumByIdAsync(Guid albumId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Albums.AsNoTracking()
            .Include(al => al.AlbumArtists).ThenInclude(aa => aa.Artist)
            .AsSplitQuery()
            .FirstOrDefaultAsync(al => al.Id == albumId).ConfigureAwait(false);
    }


    public async Task<IEnumerable<Playlist>> GetAllPlaylistsAsync(CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Playlists.AsNoTracking()
            .Include(p => p.PlaylistSongs)
            .Include(p => p.ServerFolder)
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .ToListAsync(token).ConfigureAwait(false);
    }

    public async Task<IEnumerable<Song>> GetSongsInPlaylistOrderedAsync(Guid playlistId, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId)
            .OrderBy(ps => ps.Order).ThenBy(ps => ps.SongId)
            .Select(ps => ps.Song!);

        return await SongQuery.ExcludeHeavyFields(context, query)
            .AsSplitQuery()
            .ToListAsync(token).ConfigureAwait(false);
    }

    public async Task<IEnumerable<GenreSummary>> GetGenreSummariesAsync(CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await context.Genres.AsNoTracking()
            .OrderBy(g => g.Name).ThenBy(g => g.Id)
            .Select(g => new GenreSummary(g.Id, g.Name, g.Songs.Count))
            .ToListAsync(token).ConfigureAwait(false);
    }

    public async Task<Guid?> GetRandomAlbumIdAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await GetRandomEntityIdAsync(context.Albums.AsNoTracking()).ConfigureAwait(false);
    }

    public async Task<Guid?> GetRandomArtistIdAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await GetRandomEntityIdAsync(context.Artists.AsNoTracking()).ConfigureAwait(false);
    }

    public async Task<Guid?> GetRandomGenreIdAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await GetRandomEntityIdAsync(context.Genres.AsNoTracking()).ConfigureAwait(false);
    }

    public async Task<Guid?> GetRandomPlaylistIdAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await GetRandomEntityIdAsync(context.Playlists.AsNoTracking()).ConfigureAwait(false);
    }

    public async Task<int> GetPlaylistCountAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Playlists.CountAsync().ConfigureAwait(false);
    }

    public async Task<Guid?> GetRandomFolderIdAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Only select folders that actually contain songs to avoid playing empty directory structures
        var query = context.Folders.AsNoTracking().Where(f => f.Songs.Any());
        return await GetRandomEntityIdAsync(query).ConfigureAwait(false);
    }


    public async Task<IReadOnlyList<MixCandidate>> GetMixCandidatesAsync(IEnumerable<Guid> songIds, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var result = new List<MixCandidate>();
        foreach (var ids in songIds.Distinct().Chunk(500))
            result.AddRange(await context.Songs.AsNoTracking().Where(s => ids.Contains(s.Id))
                .Select(s => new MixCandidate(s.Id, s.PrimaryArtistName, s.DurationTicks, s.PlayCount, s.LastPlayedDate))
                .ToListAsync(token).ConfigureAwait(false));
        return result;
    }

    public async Task<IReadOnlyList<RecentListen>> GetRecentListensAsync(string searchTerm, int skip, int take, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var query = context.ListenHistory.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var songs = SongQuery.Matching(context, context.Songs, searchTerm).Select(s => s.Id);
            query = query.Where(h => songs.Contains(h.SongId));
        }
        return await query.OrderByDescending(h => h.ListenTimestampUtc).ThenByDescending(h => h.Id)
            .Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, RecentListen.MaxPageSize))
            .Select(h => new RecentListen(h.Id, h.SongId, h.Song.Title, h.Song.ArtistName, h.ListenTimestampUtc))
            .ToListAsync(token).ConfigureAwait(false);
    }

    public async Task<RediscoverySuggestion?> GetRediscoverySuggestionAsync(CancellationToken token = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-90);
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await context.Songs.AsNoTracking().Where(s => s.PlayCount >= 3 && s.LastPlayedDate < cutoff)
            .OrderByDescending(s => s.IsLoved).ThenByDescending(s => s.PlayCount).ThenBy(s => s.LastPlayedDate).ThenBy(s => s.Id)
            .Select(s => new RediscoverySuggestion(s.Id, s.Title, s.ArtistName, s.LastPlayedDate!.Value))
            .FirstOrDefaultAsync(token).ConfigureAwait(false);
    }
}
