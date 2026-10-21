using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Helpers;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Paged loading, scoped search, and song ID queries.
/// </summary>
public partial class LibraryReader
{
    public async Task<PagedResult<Song>> GetAllSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder = SongSortOrder.TitleAsc, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await ReadSongPageAsync(context.Songs, pageNumber, pageSize, sortOrder, token).ConfigureAwait(false);
    }

    public async Task<PagedResult<Song>> SearchSongsPagedAsync(string searchTerm, int pageNumber, int pageSize, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var query = string.IsNullOrWhiteSpace(searchTerm)
            ? context.Songs
            : BuildSongSearchQuery(context, NormalizeString(searchTerm) ?? searchTerm);
        return await ReadSongPageAsync(query, pageNumber, pageSize, SongSortOrder.TitleAsc, token).ConfigureAwait(false);
    }

    public async Task<PagedResult<Song>> GetSongsByAlbumIdPagedAsync(Guid albumId, int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await ReadSongPageAsync(context.Songs.Where(s => s.AlbumId == albumId), pageNumber, pageSize, sortOrder, token).ConfigureAwait(false);
    }

    public async Task<PagedResult<Song>> GetSongsByArtistIdPagedAsync(Guid artistId, int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await ReadSongPageAsync(context.Songs.Where(s => s.SongArtists.Any(sa => sa.ArtistId == artistId)), pageNumber, pageSize, sortOrder, token).ConfigureAwait(false);
    }

    public async Task<PagedResult<Song>> GetSongsByGenreIdPagedAsync(Guid genreId, int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await ReadSongPageAsync(context.Songs.Where(s => s.Genres.Any(g => g.Id == genreId)), pageNumber, pageSize, sortOrder, token).ConfigureAwait(false);
    }

    public async Task<PagedResult<Song>> GetSongsByPlaylistPagedAsync(Guid playlistId, int pageNumber, int pageSize, SongSortOrder sortOrder, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId);

        return await ReadPlaylistPageAsync(context, query, pageNumber, pageSize, sortOrder, token).ConfigureAwait(false);
    }

    private async Task<PagedResult<Song>> ReadPlaylistPageAsync(MusicDbContext context, IQueryable<PlaylistSong> query,
        int pageNumber, int pageSize, SongSortOrder sortOrder, CancellationToken token)
    {
        if (sortOrder == SongSortOrder.PlaylistOrder)
        {
            var projectedQuery = query
                .Join(SongQuery.ExcludeHeavyFields(context.Songs.AsNoTracking()),
                    entry => entry.SongId, song => song.Id,
                    (entry, song) => new { Song = song, entry.Order })
                .OrderBy(row => row.Order).ThenBy(row => row.Song.Id);

            var totalCount = await query.CountAsync(token).ConfigureAwait(false);

            var pagedResults = await projectedQuery
                .Skip((pageNumber - 1) * pageSize).Take(pageSize)
                .AsSplitQuery()
                .ToListAsync(token).ConfigureAwait(false);

            foreach (var result in pagedResults)
            {
                result.Song.Order = result.Order;
            }

            var pagedSongs = pagedResults.Select(r => r.Song).ToList();

            return new PagedResult<Song>
            { Items = pagedSongs, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
        }
        return await ReadSongPageAsync(query.Select(ps => ps.Song), pageNumber, pageSize, sortOrder, token).ConfigureAwait(false);
    }

    public async Task<PagedResult<Artist>> GetAllArtistsPagedAsync(int pageNumber, int pageSize,
        ArtistSortOrder sortOrder = ArtistSortOrder.NameAsc, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Artists.AsNoTracking();
        var totalCount = await query.CountAsync(token).ConfigureAwait(false);
        var ignoreArticles = _ignoreArticlesOnSort;

        // Apply sort order - for SongCountDesc, we need to join with songs and count
        IOrderedQueryable<Artist> orderedQuery = sortOrder switch
        {
            ArtistSortOrder.NameDesc => ignoreArticles
                ? query.OrderByDescending(a => a.SortName).ThenByDescending(a => a.Id)
                : query.OrderByDescending(a => a.Name).ThenByDescending(a => a.Id),
            ArtistSortOrder.SongCountDesc => ignoreArticles
                ? query.OrderByDescending(a => context.SongArtists.Count(sa => sa.ArtistId == a.Id))
                    .ThenByDescending(a => a.SortName).ThenByDescending(a => a.Id)
                : query.OrderByDescending(a => context.SongArtists.Count(sa => sa.ArtistId == a.Id))
                    .ThenByDescending(a => a.Name).ThenByDescending(a => a.Id),
            ArtistSortOrder.SongCountAsc => ignoreArticles
                ? query.OrderBy(a => context.SongArtists.Count(sa => sa.ArtistId == a.Id))
                    .ThenBy(a => a.SortName).ThenBy(a => a.Id)
                : query.OrderBy(a => context.SongArtists.Count(sa => sa.ArtistId == a.Id))
                    .ThenBy(a => a.Name).ThenBy(a => a.Id),
            _ => ignoreArticles
                ? query.OrderBy(a => a.SortName).ThenBy(a => a.Id)
                : query.OrderBy(a => a.Name).ThenBy(a => a.Id)
        };

        var pagedData = await orderedQuery
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync(token).ConfigureAwait(false);

        return new PagedResult<Artist>
        { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    public async Task<PagedResult<Artist>> SearchArtistsPagedAsync(string searchTerm, int pageNumber, int pageSize, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        var normalizedTerm = NormalizeString(searchTerm) ?? string.Empty;
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = string.IsNullOrWhiteSpace(searchTerm)
            ? context.Artists
            : BuildArtistSearchQuery(context, normalizedTerm);
        var totalCount = await query.CountAsync(token).ConfigureAwait(false);
        var sortedQuery = _ignoreArticlesOnSort
            ? query.AsNoTracking().OrderBy(a => a.SortName).ThenBy(a => a.Id)
            : query.AsNoTracking().OrderBy(a => a.Name).ThenBy(a => a.Id);
        var pagedData = await sortedQuery
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync(token).ConfigureAwait(false);

        return new PagedResult<Artist>
        { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    public async Task<PagedResult<Album>> GetAllAlbumsPagedAsync(int pageNumber, int pageSize,
        AlbumSortOrder sortOrder = AlbumSortOrder.ArtistAsc, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Albums.AsNoTracking().Include(al => al.AlbumArtists).ThenInclude(aa => aa.Artist);
        var totalCount = await query.CountAsync(token).ConfigureAwait(false);
        var orderedQuery = ApplyAlbumSortOrder(query, sortOrder);

        var pagedData = await orderedQuery
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync(token).ConfigureAwait(false);

        return new PagedResult<Album>
        { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    public async Task<PagedResult<Album>> SearchAlbumsPagedAsync(string searchTerm, int pageNumber, int pageSize, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var query = string.IsNullOrWhiteSpace(searchTerm)
            ? context.Albums
            : BuildAlbumSearchQuery(context, NormalizeString(searchTerm) ?? string.Empty);
        var totalCount = await query.CountAsync(token).ConfigureAwait(false);
        var pagedData = await ApplyAlbumSortOrder(query.AsNoTracking().Include(al => al.AlbumArtists).ThenInclude(aa => aa.Artist),
                AlbumSortOrder.ArtistAsc)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync(token).ConfigureAwait(false);
        return new PagedResult<Album>
        { Items = pagedData, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    public async Task<List<Guid>> GetAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await ApplySongSortOrder(context.Songs.AsNoTracking(), sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    public async Task<List<Guid>> SearchAllSongIdsAsync(string searchTerm, SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(searchTerm)) return await GetAllSongIdsAsync(sortOrder, token).ConfigureAwait(false);

        var normalizedTerm = NormalizeString(searchTerm) ?? string.Empty;
        var query = BuildSongSearchQuery(context, normalizedTerm);
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    public async Task<List<Guid>> GetAllSongIdsByFolderIdAsync(Guid folderId, SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.FolderId == folderId);
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    public async Task<List<Guid>> GetAllSongIdsInDirectoryRecursiveAsync(Guid folderId, string directoryPath,
        SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var normalizedPath = PathCanonicalizer.NormalizeDirectoryPath(directoryPath);

        // Match subdirectories by "path + separator" so that "Music/Rock" doesn't match "Music/Rockabilly".
        var childPrefix = normalizedPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var query = context.Songs.AsNoTracking()
            .Where(s => s.FolderId == folderId &&
                        (s.DirectoryPath == normalizedPath || s.DirectoryPath.StartsWith(childPrefix)));

        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    public Task<PagedResult<Song>> GetSongsInDirectoryOffsetAsync(Guid folderId, string directoryPath,
        int skip, int take, SongSortOrder sortOrder, CancellationToken token = default)
    {
        return SearchSongsInFolderOffsetAsync(folderId, directoryPath, string.Empty, skip, take, sortOrder, token);
    }

    public async Task<PagedResult<Song>> SearchSongsInFolderOffsetAsync(Guid folderId, string directoryPath, string searchTerm, int skip, int take, SongSortOrder sortOrder, CancellationToken token = default)
    {
        skip = Math.Max(0, skip);
        take = Math.Max(0, take);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var normalizedPath = PathCanonicalizer.NormalizeDirectoryPath(directoryPath);
        var baseQuery = context.Songs.AsNoTracking()
            .Where(s => s.FolderId == folderId && s.DirectoryPath == normalizedPath);

        baseQuery = FilterScopedSongs(baseQuery, searchTerm);

        var totalCount = await baseQuery.CountAsync(token).ConfigureAwait(false);
        IEnumerable<Song> items = Enumerable.Empty<Song>();
        if (take > 0 && totalCount > 0)
        {
            var queryWithIncludes = baseQuery
                .Include(s => s.SongArtists).ThenInclude(sa => sa.Artist)
                .Include(s => s.Album).ThenInclude(a => a!.AlbumArtists).ThenInclude(aa => aa.Artist);

            items = await ApplySongSortOrder(SongQuery.ExcludeHeavyFields(queryWithIncludes), sortOrder)
                .Skip(skip).Take(take).AsSplitQuery().ToListAsync(token).ConfigureAwait(false);
        }

        return new PagedResult<Song> { Items = items, TotalCount = totalCount, PageNumber = 0, PageSize = take };
    }

    public async Task<List<Guid>> GetAllSongIdsByArtistIdAsync(Guid artistId, SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.SongArtists.Any(sa => sa.ArtistId == artistId));
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    public async Task<List<Guid>> GetAllSongIdsByAlbumIdAsync(Guid albumId, SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.AlbumId == albumId);
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    public async Task<List<Guid>> GetAllSongIdsByPlaylistIdAsync(Guid playlistId, SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId);

        if (sortOrder == SongSortOrder.PlaylistOrder)
        {
            return await query.OrderBy(ps => ps.Order).ThenBy(ps => ps.SongId).Select(ps => ps.SongId).ToListAsync(token).ConfigureAwait(false);
        }

        var songQuery = query.Select(ps => ps.Song!);
        return await ApplySongSortOrder(songQuery, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    public async Task<List<Guid>> GetAllSongIdsByGenreIdAsync(Guid genreId, SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.Genres.Any(g => g.Id == genreId));
        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    public async Task<PagedResult<Song>> SearchSongsInAlbumPagedAsync(Guid albumId, string searchTerm, int pageNumber,
        int pageSize, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var baseQuery = context.Songs.AsNoTracking()
            .Where(s => s.AlbumId == albumId);

        baseQuery = FilterScopedSongs(baseQuery, searchTerm);

        return await ReadSongPageAsync(baseQuery.Include(s => s.Album).ThenInclude(a => a!.AlbumArtists).ThenInclude(aa => aa.Artist),
            pageNumber, pageSize, SongSortOrder.TrackNumberAsc, token).ConfigureAwait(false);
    }

    public async Task<PagedResult<Song>> SearchSongsInArtistPagedAsync(Guid artistId, string searchTerm, int pageNumber,
        int pageSize, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var baseQuery = context.Songs.AsNoTracking()
            .Where(s => s.SongArtists.Any(sa => sa.ArtistId == artistId));

        baseQuery = FilterScopedSongs(baseQuery, searchTerm);

        return await ReadSongPageAsync(baseQuery.Include(s => s.Album).ThenInclude(a => a!.AlbumArtists).ThenInclude(aa => aa.Artist),
            pageNumber, pageSize, SongSortOrder.AlbumAsc, token).ConfigureAwait(false);
    }

    public async Task<PagedResult<Song>> SearchSongsInPlaylistPagedAsync(Guid playlistId, string searchTerm,
        int pageNumber, int pageSize, SongSortOrder sortOrder, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var query = context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId);

        query = FilterPlaylistSongs(query, searchTerm);
        return await ReadPlaylistPageAsync(context, query, pageNumber, pageSize, sortOrder, token).ConfigureAwait(false);
    }

    public async Task<PagedResult<Song>> SearchSongsInGenrePagedAsync(Guid genreId, string searchTerm, int pageNumber,
        int pageSize, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var baseQuery = context.Songs.AsNoTracking()
            .Where(s => s.Genres.Any(g => g.Id == genreId));

        baseQuery = FilterScopedSongs(baseQuery, searchTerm);

        return await ReadSongPageAsync(baseQuery.Include(s => s.Album).ThenInclude(a => a!.AlbumArtists).ThenInclude(aa => aa.Artist),
            pageNumber, pageSize, SongSortOrder.TitleAsc, token).ConfigureAwait(false);
    }

    public async Task<List<Guid>> SearchAllSongIdsInFolderAsync(Guid folderId, string directoryPath, string searchTerm,
        SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var normalizedPath = PathCanonicalizer.NormalizeDirectoryPath(directoryPath);
        var query = context.Songs.AsNoTracking()
            .Where(s => s.FolderId == folderId && s.DirectoryPath == normalizedPath);

        query = FilterScopedSongs(query, searchTerm);

        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    public async Task<List<Guid>> SearchAllSongIdsInArtistAsync(Guid artistId, string searchTerm,
        SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.SongArtists.Any(sa => sa.ArtistId == artistId));

        query = FilterScopedSongs(query, searchTerm);

        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    public async Task<List<Guid>> SearchAllSongIdsInAlbumAsync(Guid albumId, string searchTerm, SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.AlbumId == albumId);

        query = FilterScopedSongs(query, searchTerm);

        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    public async Task<List<Guid>> SearchAllSongIdsInPlaylistAsync(Guid playlistId, string searchTerm, SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.PlaylistSongs.AsNoTracking()
            .Where(ps => ps.PlaylistId == playlistId);

        query = FilterPlaylistSongs(query, searchTerm);
        if (sortOrder == SongSortOrder.PlaylistOrder)
            return await query.OrderBy(ps => ps.Order).ThenBy(ps => ps.SongId)
                .Select(ps => ps.SongId).ToListAsync(token).ConfigureAwait(false);

        var songQuery = query.Select(ps => ps.Song!);

        return await ApplySongSortOrder(songQuery, sortOrder)
            .Select(s => s.Id)
            .ToListAsync(token).ConfigureAwait(false);
    }

    public async Task<List<Guid>> SearchAllSongIdsInGenreAsync(Guid genreId, string searchTerm, SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var query = context.Songs.AsNoTracking().Where(s => s.Genres.Any(g => g.Id == genreId));

        query = FilterScopedSongs(query, searchTerm);

        return await ApplySongSortOrder(query, sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    private async Task<PagedResult<Song>> ReadSongPageAsync(IQueryable<Song> query, int pageNumber,
        int pageSize, SongSortOrder sortOrder, CancellationToken token)
    {
        var totalCount = await query.CountAsync(token).ConfigureAwait(false);
        var items = totalCount == 0 ? [] : await ApplySongSortOrder(SongQuery.ExcludeHeavyFields(query.AsNoTracking()), sortOrder)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync(token).ConfigureAwait(false);
        return new PagedResult<Song>
        { Items = items, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    private static IQueryable<Song> FilterScopedSongs(IQueryable<Song> query, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm)) return query;
        var term = LikePatternHelper.CreateContainsPattern(NormalizeString(searchTerm) ?? string.Empty);
        return query.Where(s => EF.Functions.Like(s.Title, term, LikePatternHelper.EscapeCharacter)
                                || EF.Functions.Like(s.ArtistName, term, LikePatternHelper.EscapeCharacter)
                                || (s.Album != null && (EF.Functions.Like(s.Album.Title, term, LikePatternHelper.EscapeCharacter)
                                    || EF.Functions.Like(s.Album.ArtistName, term, LikePatternHelper.EscapeCharacter))));
    }

    private IOrderedQueryable<Album> ApplyAlbumSortOrder(IQueryable<Album> query, AlbumSortOrder sortOrder)
    {
        var ignoreArticles = _ignoreArticlesOnSort;
        Expression<Func<Album, string>> title = ignoreArticles ? a => a.SortTitle : a => a.Title;
        Expression<Func<Album, string>> artist = ignoreArticles ? a => a.PrimaryArtistSortName : a => a.PrimaryArtistName;
        return sortOrder switch
        {
            AlbumSortOrder.ArtistDesc => query.OrderByDescending(artist).ThenByDescending(title).ThenByDescending(a => a.Id),
            AlbumSortOrder.AlbumTitleAsc => query.OrderBy(title).ThenBy(a => a.Id),
            AlbumSortOrder.AlbumTitleDesc => query.OrderByDescending(title).ThenByDescending(a => a.Id),
            AlbumSortOrder.YearDesc => query.OrderByDescending(a => a.Year ?? 0).ThenByDescending(artist).ThenByDescending(title).ThenByDescending(a => a.Id),
            AlbumSortOrder.YearAsc => query.OrderBy(a => a.Year ?? int.MaxValue).ThenBy(artist).ThenBy(title).ThenBy(a => a.Id),
            AlbumSortOrder.SongCountDesc => query.OrderByDescending(a => a.Songs.Count).ThenByDescending(title).ThenByDescending(a => a.Id),
            AlbumSortOrder.SongCountAsc => query.OrderBy(a => a.Songs.Count).ThenBy(title).ThenBy(a => a.Id),
            _ => query.OrderBy(artist).ThenBy(title).ThenBy(a => a.Id)
        };
    }

    private IOrderedQueryable<Song> ApplySongSortOrder(IQueryable<Song> query, SongSortOrder sortOrder) =>
        SongQuery.Sort(query, sortOrder, _ignoreArticlesOnSort);

    private static IQueryable<PlaylistSong> FilterPlaylistSongs(IQueryable<PlaylistSong> query, string searchTerm)
    {
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = LikePatternHelper.CreateContainsPattern(NormalizeString(searchTerm) ?? string.Empty);
            query = query.Where(ps => (EF.Functions.Like(ps.Song.Title, term, LikePatternHelper.EscapeCharacter)
                                       || EF.Functions.Like(ps.Song.ArtistName, term, LikePatternHelper.EscapeCharacter)
                                       || ps.Song.SongArtists.Any(sa => EF.Functions.Like(sa.Artist.Name, term, LikePatternHelper.EscapeCharacter))
                                       || (ps.Song.Album != null && (EF.Functions.Like(ps.Song.Album.Title, term, LikePatternHelper.EscapeCharacter) || EF.Functions.Like(ps.Song.Album.ArtistName, term, LikePatternHelper.EscapeCharacter)))));
        }

        return query;
    }

    private IQueryable<Song> BuildSongSearchQuery(MusicDbContext context, string searchTerm)
    {
        var term = LikePatternHelper.CreateContainsPattern(searchTerm);
        var artistIds = context.Artists
            .Where(artist => EF.Functions.Like(artist.Name, term, LikePatternHelper.EscapeCharacter))
            .Select(artist => artist.Id);
        var songArtistIds = context.SongArtists.Where(sa => artistIds.Contains(sa.ArtistId)).Select(sa => sa.SongId);
        var albumIds = context.Albums
            .Where(album => EF.Functions.Like(album.Title, term, LikePatternHelper.EscapeCharacter)
                            || EF.Functions.Like(album.ArtistName, term, LikePatternHelper.EscapeCharacter))
            .Select(album => (Guid?)album.Id)
            .Union(context.AlbumArtists.Where(aa => artistIds.Contains(aa.ArtistId)).Select(aa => (Guid?)aa.AlbumId));
        var genreSongIds = context.Genres
            .Where(genre => EF.Functions.Like(genre.Name, term, LikePatternHelper.EscapeCharacter))
            .SelectMany(genre => genre.Songs).Select(song => song.Id);

        return context.Songs
            .Where(s =>
                EF.Functions.Like(s.Title, term, LikePatternHelper.EscapeCharacter)
                || EF.Functions.Like(s.ArtistName, term, LikePatternHelper.EscapeCharacter)
                || songArtistIds.Contains(s.Id)
                || albumIds.Contains(s.AlbumId)
                || (s.Year != null && EF.Functions.Like(s.Year.ToString(), term, LikePatternHelper.EscapeCharacter))
                || genreSongIds.Contains(s.Id)
            );
    }

    private IQueryable<Artist> BuildArtistSearchQuery(MusicDbContext context, string searchTerm)
    {
        var term = LikePatternHelper.CreateContainsPattern(searchTerm);
        return context.Artists.Where(a => EF.Functions.Like(a.Name, term, LikePatternHelper.EscapeCharacter));
    }

    private IQueryable<Album> BuildAlbumSearchQuery(MusicDbContext context, string searchTerm)
    {
        var term = LikePatternHelper.CreateContainsPattern(searchTerm);
        return context.Albums
            .Where(al => EF.Functions.Like(al.Title, term, LikePatternHelper.EscapeCharacter)
                         || EF.Functions.Like(al.ArtistName, term, LikePatternHelper.EscapeCharacter)
                         || al.AlbumArtists.Any(aa => EF.Functions.Like(aa.Artist.Name, term, LikePatternHelper.EscapeCharacter)));
    }

    private static void SanitizePaging(ref int pageNumber, ref int pageSize)
    {
        pageNumber = Math.Max(1, pageNumber);
        pageSize = Math.Max(1, pageSize);
    }
}
