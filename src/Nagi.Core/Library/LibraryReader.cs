using Microsoft.EntityFrameworkCore;
using Nagi.Core.Data;
using Nagi.Core.Platform;
using Nagi.Core.Settings;

namespace Nagi.Core.Library;

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
        return await context.Folders.AsNoTracking().Include(f => f.MusicServer)
            .FirstOrDefaultAsync(f => f.Id == folderId).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<Folder>> GetRootFoldersOnDiskAsync(CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await context.Folders.AsNoTracking().OnDisk()
            .Where(f => f.ParentFolderId == null)
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
        foreach (var chunk in songIds.Distinct().Chunk(MusicDbContext.MaxParametersPerQuery))
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
        foreach (var chunk in songIds.Distinct().Chunk(MusicDbContext.MaxParametersPerQuery))
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
        foreach (var chunk in songIds.Distinct().Chunk(MusicDbContext.MaxParametersPerQuery))
            ticks += await context.Songs.Where(s => chunk.Contains(s.Id))
                .SumAsync(s => s.DurationTicks, cancellationToken).ConfigureAwait(false);
        return TimeSpan.FromTicks(ticks);
    }

    public async Task<Artist?> GetArtistByNameAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var normalizedName = TextNormalizer.Normalize(name);
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

    public async Task<Guid?> GetRandomFolderIdAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Only select folders that actually contain songs to avoid playing empty directory structures
        var query = context.Folders.AsNoTracking().Where(f => f.Songs.Any());
        return await GetRandomEntityIdAsync(query).ConfigureAwait(false);
    }
}
