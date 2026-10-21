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
///     Read-only queries over the music library: entity lookups, folder browsing, paging, search,
///     and the song ID lists used to build playback queues.
///     This service is designed to be a singleton and is internally thread-safe.
/// </summary>
public partial class LibraryReader : ILibraryReader, IDisposable
{
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly IFileSystemService _fileSystem;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<LibraryReader> _logger;
    private volatile bool _ignoreArticlesOnSort = true;
    private int _disposeState;

    public LibraryReader(
        IDbContextFactory<MusicDbContext> contextFactory,
        IFileSystemService fileSystem,
        ISettingsService settingsService,
        ILogger<LibraryReader> logger)
    {
        _contextFactory = contextFactory;
        _fileSystem = fileSystem;
        _settingsService = settingsService;
        _logger = logger;
        _settingsService.IgnoreLeadingArticlesOnSortEnabledChanged += OnIgnoreLeadingArticlesOnSortChanged;
        _ = InitializeIgnoreArticlesFlagAsync();
    }

    private async Task InitializeIgnoreArticlesFlagAsync()
    {
        try { _ignoreArticlesOnSort = await _settingsService.GetIgnoreLeadingArticlesOnSortEnabledAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to load IgnoreLeadingArticlesOnSort setting; using default ({Default}).", _ignoreArticlesOnSort); }
    }

    private void OnIgnoreLeadingArticlesOnSortChanged(bool value) => _ignoreArticlesOnSort = value;

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
            .Select(f => new FolderSummary(f, f.Songs.Count))
            .ToListAsync(token).ConfigureAwait(false);
    }

    public Task<IEnumerable<Folder>> GetSubFoldersPagedAsync(Guid parentFolderId, int skip, int take, CancellationToken token = default) =>
        SearchSubFoldersPagedAsync(parentFolderId, string.Empty, skip, take, token);

    public async Task<int> GetSubFolderCountBySearchAsync(Guid parentFolderId, string searchTerm, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return await GetSubFolderCountAsync(parentFolderId, token).ConfigureAwait(false);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var term = LikePatternHelper.CreateContainsPattern(NormalizeString(searchTerm) ?? string.Empty);
        return await context.Folders.AsNoTracking()
            .CountAsync(f => f.ParentFolderId == parentFolderId &&
                             EF.Functions.Like(f.Name, term, LikePatternHelper.EscapeCharacter), token).ConfigureAwait(false);
    }

    public async Task<IEnumerable<Folder>> SearchSubFoldersPagedAsync(Guid parentFolderId, string searchTerm, int skip, int take, CancellationToken token = default)
    {
        if (take <= 0) return Enumerable.Empty<Folder>();
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Folders.AsNoTracking()
            .Where(f => f.ParentFolderId == parentFolderId);

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = LikePatternHelper.CreateContainsPattern(NormalizeString(searchTerm) ?? string.Empty);
            query = query.Where(f => EF.Functions.Like(f.Name, term, LikePatternHelper.EscapeCharacter));
        }

        return await query
            .OrderBy(f => f.Name)
            .ThenBy(f => f.Path)
            .ThenBy(f => f.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(token).ConfigureAwait(false);
    }

    public async Task<int> GetSubFolderCountAsync(Guid parentFolderId, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Folders.CountAsync(f => f.ParentFolderId == parentFolderId, token).ConfigureAwait(false);
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

            var batch = await SongQuery.ExcludeHeavyFields(query)
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
                    Id = s.Id, Title = s.Title, ArtistName = s.ArtistName,
                    DurationTicks = s.DurationTicks, Genres = s.Genres,
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

    public async Task<IEnumerable<Album>> GetTopAlbumsForArtistAsync(Guid artistId, int limit)
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
            .Take(limit)
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

    public async Task<TimeSpan> GetSearchTotalDurationInAlbumAsync(Guid albumId, string searchTerm, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var ticks = await FilterScopedSongs(context.Songs.Where(s => s.AlbumId == albumId), searchTerm)
            .SumAsync(s => s.DurationTicks, token).ConfigureAwait(false);

        return TimeSpan.FromTicks(ticks);
    }

    public async Task<Playlist?> GetPlaylistByIdAsync(Guid playlistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Playlists.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == playlistId).ConfigureAwait(false);
    }

    public async Task<IEnumerable<Playlist>> GetAllPlaylistsAsync(CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.Playlists.AsNoTracking()
            .Include(p => p.PlaylistSongs)
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

        return await SongQuery.ExcludeHeavyFields(query)
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

    private static string? NormalizeString(string? s) => ArtistNameHelper.NormalizeStringCore(s);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;
        _settingsService.IgnoreLeadingArticlesOnSortEnabledChanged -= OnIgnoreLeadingArticlesOnSortChanged;
        GC.SuppressFinalize(this);
    }
}
