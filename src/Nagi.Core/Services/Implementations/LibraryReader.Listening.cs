using Microsoft.EntityFrameworkCore;
using Nagi.Core.Data;
using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

public partial class LibraryReader
{
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

    public async Task<PagedResult<Song>> GetFavoritesPagedAsync(string searchTerm, int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await ReadSongPageAsync(FavoriteQuery(context, searchTerm), pageNumber, pageSize, sortOrder, token).ConfigureAwait(false);
    }

    public async Task<List<Guid>> GetFavoriteSongIdsAsync(string searchTerm, SongSortOrder sortOrder, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await ApplySongSortOrder(FavoriteQuery(context, searchTerm), sortOrder).Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
    }

    private IQueryable<Song> FavoriteQuery(MusicDbContext context, string searchTerm) =>
        (string.IsNullOrWhiteSpace(searchTerm) ? context.Songs : BuildSongSearchQuery(context, NormalizeString(searchTerm) ?? searchTerm))
        .Where(s => s.IsLoved);

    public async Task<IReadOnlyList<RecentListen>> GetRecentListensAsync(string searchTerm, int skip, int take, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var query = context.ListenHistory.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var songs = BuildSongSearchQuery(context, NormalizeString(searchTerm) ?? searchTerm).Select(s => s.Id);
            query = query.Where(h => songs.Contains(h.SongId));
        }
        return await query.OrderByDescending(h => h.ListenTimestampUtc).ThenByDescending(h => h.Id)
            .Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 100))
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
