using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Playlists;
using Nagi.Core.Settings;

namespace Nagi.Core.Library;

/// <summary>
///     The lists pages show: songs by scope, artists, albums and sub-folders, each searched and sorted the same way
///     whether or not a search term is given.
/// </summary>
public partial class LibraryReader
{
    public Task<PagedResult<Song>> GetSongsAsync(SongScope scope, string? searchTerm, SongSortOrder sortOrder,
        int pageNumber, int pageSize, CancellationToken token = default)
    {
        Paging.Sanitize(ref pageNumber, ref pageSize);
        return ReadSongsAsync(scope, searchTerm, sortOrder, (pageNumber - 1) * pageSize, pageSize, pageNumber, token);
    }

    public async Task<DirectoryPage> GetDirectoryPageAsync(SongScope.Directory directory, string? searchTerm,
        SongSortOrder sortOrder, int pageNumber, int pageSize, CancellationToken token = default)
    {
        Paging.Sanitize(ref pageNumber, ref pageSize);
        var start = (pageNumber - 1) * pageSize;

        List<Folder> folders = [];
        var folderCount = 0;
        await using (var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false))
        {
            if (await GetDirectoryFolderIdAsync(context, directory, token).ConfigureAwait(false) is { } folderId)
            {
                var subFolders = SubFolders(context, folderId, searchTerm);
                folderCount = await subFolders.CountAsync(token).ConfigureAwait(false);
                if (start < folderCount)
                    folders = await subFolders.OrderBy(f => f.Name).ThenBy(f => f.Path).ThenBy(f => f.Id)
                        .Skip(start).Take(pageSize).ToListAsync(token).ConfigureAwait(false);
            }
        }

        var songs = await ReadSongsAsync(directory, searchTerm, sortOrder, Math.Max(0, start - folderCount),
            pageSize - folders.Count, pageNumber, token).ConfigureAwait(false);
        return new DirectoryPage
        {
            Items = songs.Items,
            Folders = folders,
            FolderCount = folderCount,
            TotalCount = folderCount + songs.TotalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<List<Guid>> GetSongIdsAsync(SongScope scope, string? searchTerm, SongSortOrder sortOrder,
        CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        if (scope is SongScope.Playlist playlist && sortOrder == SongSortOrder.PlaylistOrder)
            return await PlaylistEntries(context, playlist, searchTerm)
                .OrderBy(ps => ps.Order).ThenBy(ps => ps.SongId)
                .Select(ps => ps.SongId)
                .ToListAsync(token).ConfigureAwait(false);

        return await SortSongs(SongsIn(context, scope, searchTerm), sortOrder).Select(s => s.Id)
            .ToListAsync(token).ConfigureAwait(false);
    }

    public async Task<TimeSpan> GetSongsDurationAsync(SongScope scope, string? searchTerm, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var ticks = await SongsIn(context, scope, searchTerm).SumAsync(s => s.DurationTicks, token).ConfigureAwait(false);
        return TimeSpan.FromTicks(ticks);
    }

    public async Task<PagedResult<Artist>> GetArtistsAsync(string? searchTerm, ArtistSortOrder sortOrder, int pageNumber,
        int pageSize, CancellationToken token = default)
    {
        Paging.Sanitize(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var query = context.Artists.AsNoTracking();
        if (SearchPattern(searchTerm) is { } term)
            query = query.Where(a => EF.Functions.Like(a.Name, term, LikePatternHelper.EscapeCharacter));

        var totalCount = await query.CountAsync(token).ConfigureAwait(false);
        var items = await SortArtists(context, query, sortOrder)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync(token).ConfigureAwait(false);
        return new PagedResult<Artist> { Items = items, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    public async Task<PagedResult<Album>> GetAlbumsAsync(string? searchTerm, AlbumSortOrder sortOrder, int pageNumber,
        int pageSize, CancellationToken token = default)
    {
        Paging.Sanitize(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var query = context.Albums.AsNoTracking();
        if (SearchPattern(searchTerm) is { } term)
            query = query.Where(al => EF.Functions.Like(al.Title, term, LikePatternHelper.EscapeCharacter)
                                      || EF.Functions.Like(al.ArtistName, term, LikePatternHelper.EscapeCharacter)
                                      || al.AlbumArtists.Any(aa => EF.Functions.Like(aa.Artist.Name, term, LikePatternHelper.EscapeCharacter)));

        var totalCount = await query.CountAsync(token).ConfigureAwait(false);
        var items = await SortAlbums(query.Include(al => al.AlbumArtists).ThenInclude(aa => aa.Artist), sortOrder)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).AsSplitQuery().ToListAsync(token).ConfigureAwait(false);
        return new PagedResult<Album> { Items = items, TotalCount = totalCount, PageNumber = pageNumber, PageSize = pageSize };
    }

    // A root folder's own path is matched as the user added it; any directory under it by its normalized path.
    private async Task<Guid?> GetDirectoryFolderIdAsync(MusicDbContext context, SongScope.Directory directory,
        CancellationToken token)
    {
        var rootPath = await context.Folders.AsNoTracking().Where(f => f.Id == directory.RootFolderId)
            .Select(f => f.Path).FirstOrDefaultAsync(token).ConfigureAwait(false);
        if (rootPath is not null && string.Equals(Path.TrimEndingDirectorySeparator(rootPath),
                Path.TrimEndingDirectorySeparator(directory.Path), StringComparison.OrdinalIgnoreCase))
            return directory.RootFolderId;

        var normalizedPath = _fileSystem.NormalizePath(directory.Path);
        if (string.IsNullOrEmpty(normalizedPath)) return null;
        return await context.Folders.AsNoTracking().Where(f => f.Path == normalizedPath)
            .Select(f => (Guid?)f.Id).FirstOrDefaultAsync(token).ConfigureAwait(false);
    }

    private async Task<PagedResult<Song>> ReadSongsAsync(SongScope scope, string? searchTerm, SongSortOrder sortOrder,
        int skip, int take, int pageNumber, CancellationToken token)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var songs = SongsIn(context, scope, searchTerm);
        var totalCount = await songs.CountAsync(token).ConfigureAwait(false);

        List<Song> items = [];
        if (take > 0 && totalCount > 0)
        {
            if (scope is SongScope.Playlist playlist && sortOrder == SongSortOrder.PlaylistOrder)
            {
                var rows = await PlaylistEntries(context, playlist, searchTerm)
                    .Join(SongQuery.ExcludeHeavyFields(context, context.Songs.AsNoTracking()),
                        entry => entry.SongId, song => song.Id,
                        (entry, song) => new { Song = song, entry.Order })
                    .OrderBy(row => row.Order).ThenBy(row => row.Song.Id)
                    .Skip(skip).Take(take)
                    .AsSplitQuery()
                    .ToListAsync(token).ConfigureAwait(false);
                foreach (var row in rows) row.Song.Order = row.Order;
                items = rows.Select(row => row.Song).ToList();
            }
            else
            {
                items = await SortSongs(SongQuery.ExcludeHeavyFields(context, songs), sortOrder)
                    .Skip(skip).Take(take).AsSplitQuery().ToListAsync(token).ConfigureAwait(false);
            }
        }

        return new PagedResult<Song> { Items = items, TotalCount = totalCount, PageNumber = pageNumber, PageSize = take };
    }

    private static IQueryable<Song> SongsIn(MusicDbContext context, SongScope scope, string? searchTerm)
    {
        var songs = context.Songs.AsNoTracking();
        songs = scope switch
        {
            SongScope.LibraryScope => songs,
            SongScope.FavoritesScope => songs.Where(s => s.IsLoved),
            SongScope.Album album => songs.Where(s => s.AlbumId == album.AlbumId),
            SongScope.Artist artist => songs.Where(s => s.SongArtists.Any(sa => sa.ArtistId == artist.ArtistId)),
            SongScope.Genre genre => songs.Where(s => s.Genres.Any(g => g.Id == genre.GenreId)),
            SongScope.Playlist playlist => context.PlaylistSongs.Where(ps => ps.PlaylistId == playlist.PlaylistId)
                .Select(ps => ps.Song),
            SongScope.Folder folder => songs.Where(s => s.FolderId == folder.RootFolderId),
            SongScope.Directory directory => InDirectory(songs, directory.RootFolderId, directory.Path, false),
            SongScope.DirectoryTree tree => InDirectory(songs, tree.RootFolderId, tree.Path, true),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
        };
        return SongQuery.Matching(context, songs, searchTerm);
    }

    private static IQueryable<PlaylistSong> PlaylistEntries(MusicDbContext context, SongScope.Playlist playlist,
        string? searchTerm)
    {
        var entries = context.PlaylistSongs.AsNoTracking().Where(ps => ps.PlaylistId == playlist.PlaylistId);
        if (string.IsNullOrWhiteSpace(searchTerm)) return entries;
        var matching = SongQuery.Matching(context, context.Songs, searchTerm).Select(s => s.Id);
        return entries.Where(ps => matching.Contains(ps.SongId));
    }

    private static IQueryable<Song> InDirectory(IQueryable<Song> songs, Guid rootFolderId, string directoryPath,
        bool includeSubdirectories)
    {
        var path = PathCanonicalizer.NormalizeDirectoryPath(directoryPath);
        if (!includeSubdirectories)
            return songs.Where(s => s.FolderId == rootFolderId && s.DirectoryPath == path);

        // Match subdirectories by "path + separator" so that "Music/Rock" doesn't match "Music/Rockabilly".
        var childPrefix = path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return songs.Where(s => s.FolderId == rootFolderId &&
                                (s.DirectoryPath == path || s.DirectoryPath.StartsWith(childPrefix)));
    }

    private static IQueryable<Folder> SubFolders(MusicDbContext context, Guid parentFolderId, string? searchTerm)
    {
        var folders = context.Folders.AsNoTracking().Where(f => f.ParentFolderId == parentFolderId);
        return SearchPattern(searchTerm) is { } term
            ? folders.Where(f => EF.Functions.Like(f.Name, term, LikePatternHelper.EscapeCharacter))
            : folders;
    }

    private static string? SearchPattern(string? searchTerm) =>
        TextNormalizer.Normalize(searchTerm) is { } term ? LikePatternHelper.CreateContainsPattern(term) : null;

    private bool IgnoreArticles => _settingsService.Get(CoreSettings.IgnoreLeadingArticlesOnSort);

    private IOrderedQueryable<Song> SortSongs(IQueryable<Song> query, SongSortOrder sortOrder) =>
        SongQuery.Sort(query, sortOrder, IgnoreArticles);

    private IOrderedQueryable<Artist> SortArtists(MusicDbContext context, IQueryable<Artist> query, ArtistSortOrder sortOrder)
    {
        Expression<Func<Artist, string>> name = IgnoreArticles ? a => a.SortName : a => a.Name;
        Expression<Func<Artist, int>> songCount = a => context.SongArtists.Count(sa => sa.ArtistId == a.Id);
        return sortOrder switch
        {
            ArtistSortOrder.NameDesc => query.OrderByDescending(name).ThenByDescending(a => a.Id),
            ArtistSortOrder.SongCountDesc => query.OrderByDescending(songCount).ThenByDescending(name).ThenByDescending(a => a.Id),
            ArtistSortOrder.SongCountAsc => query.OrderBy(songCount).ThenBy(name).ThenBy(a => a.Id),
            _ => query.OrderBy(name).ThenBy(a => a.Id)
        };
    }

    private IOrderedQueryable<Album> SortAlbums(IQueryable<Album> query, AlbumSortOrder sortOrder)
    {
        var ignoreArticles = IgnoreArticles;
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
}
