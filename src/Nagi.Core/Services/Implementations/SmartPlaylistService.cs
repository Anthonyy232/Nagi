using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Core.Services.Helpers;

namespace Nagi.Core.Services.Implementations;

public class SmartPlaylistService : ISmartPlaylistService
{
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly PlaylistRules _rules;
    private readonly ILogger<SmartPlaylistService> _logger;

    public event EventHandler<PlaylistUpdatedEventArgs>? PlaylistUpdated;

    public event EventHandler? PlaylistsChanged;

    public SmartPlaylistService(
        IDbContextFactory<MusicDbContext> contextFactory,
        IFileSystemService fileSystem,
        IPathConfiguration pathConfig,
        IImageProcessor imageProcessor,
        ILogger<SmartPlaylistService> logger)
    {
        _contextFactory = contextFactory;
        _rules = new PlaylistRules(fileSystem, imageProcessor, pathConfig, logger);
        _logger = logger;
    }

    public async Task<SmartPlaylist> CreateSmartPlaylistAsync(SmartPlaylist playlist) =>
        (await SaveSmartPlaylistAsync(playlist, isNew: true).ConfigureAwait(false))!;

    public Task<SmartPlaylist?> UpdateSmartPlaylistAsync(SmartPlaylist playlist) =>
        SaveSmartPlaylistAsync(playlist, isNew: false);

    private async Task<SmartPlaylist?> SaveSmartPlaylistAsync(SmartPlaylist draft, bool isNew)
    {
        var name = PlaylistRules.NormalizeName(draft.Name);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = isNew ? new SmartPlaylist() : await context.SmartPlaylists
            .Include(sp => sp.Rules).FirstOrDefaultAsync(sp => sp.Id == draft.Id).ConfigureAwait(false);
        if (playlist is null) return null;

        await PlaylistRules.ThrowIfNameTakenAsync(OtherNames(context, playlist.Id), name).ConfigureAwait(false);
        playlist.Name = name;
        playlist.Description = draft.Description;
        playlist.MatchAllRules = draft.MatchAllRules;
        playlist.SortOrder = draft.SortOrder;
        if (!string.Equals(playlist.CoverImageUri, draft.CoverImageUri, StringComparison.Ordinal))
            await SetCoverAsync(playlist, draft.CoverImageUri).ConfigureAwait(false);

        context.SmartPlaylistRules.RemoveRange(playlist.Rules);
        playlist.Rules = ToStoredRules(draft.Rules, playlist.Id);
        if (isNew)
        {
            playlist.DateModified = DateTime.UtcNow;
            context.SmartPlaylists.Add(playlist);
            await context.SaveChangesAsync().ConfigureAwait(false);
            PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            context.SmartPlaylistRules.AddRange(playlist.Rules);
            await SaveChangesAsync(context, playlist).ConfigureAwait(false);
        }
        return playlist;
    }

    public async Task<bool> DeleteSmartPlaylistAsync(Guid smartPlaylistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var rowsAffected = await context.SmartPlaylists
            .Where(sp => sp.Id == smartPlaylistId)
            .ExecuteDeleteAsync().ConfigureAwait(false);

        if (rowsAffected == 0) return false;

        _logger.LogInformation("Deleted smart playlist with ID {Id}", smartPlaylistId);
        _rules.DeleteCoverOfRemovedPlaylist(smartPlaylistId);
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task<SmartPlaylist?> GetSmartPlaylistByIdAsync(Guid smartPlaylistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.SmartPlaylists.AsNoTracking()
            .Include(sp => sp.Rules.OrderBy(r => r.Order))
            .FirstOrDefaultAsync(sp => sp.Id == smartPlaylistId).ConfigureAwait(false);
    }

    public async Task<IEnumerable<SmartPlaylist>> GetAllSmartPlaylistsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.SmartPlaylists.AsNoTracking()
            .Include(sp => sp.Rules.OrderBy(r => r.Order))
            .OrderBy(sp => sp.Name)
            .ThenBy(sp => sp.Id)
            .ToListAsync().ConfigureAwait(false);
    }

    public async Task<bool> RenameSmartPlaylistAsync(Guid smartPlaylistId, string newName)
    {
        var normalizedName = PlaylistRules.NormalizeName(newName);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var smartPlaylist = await context.SmartPlaylists.FindAsync(smartPlaylistId).ConfigureAwait(false);
        if (smartPlaylist is null) return false;

        await PlaylistRules.ThrowIfNameTakenAsync(OtherNames(context, smartPlaylistId), normalizedName).ConfigureAwait(false);
        smartPlaylist.Name = normalizedName;
        await SaveChangesAsync(context, smartPlaylist).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UpdateSmartPlaylistCoverAsync(Guid smartPlaylistId, string? newCoverImageUri)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var smartPlaylist = await context.SmartPlaylists.FindAsync(smartPlaylistId).ConfigureAwait(false);
        if (smartPlaylist is null) return false;

        if (!await SetCoverAsync(smartPlaylist, newCoverImageUri).ConfigureAwait(false)) return false;

        await SaveChangesAsync(context, smartPlaylist).ConfigureAwait(false);
        return true;
    }

    public async Task<Guid?> GetRandomSmartPlaylistIdAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.SmartPlaylists.OrderBy(_ => EF.Functions.Random())
            .Select(playlist => (Guid?)playlist.Id).FirstOrDefaultAsync().ConfigureAwait(false);
    }

    public async Task<int> GetSmartPlaylistCountAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.SmartPlaylists.CountAsync().ConfigureAwait(false);
    }

    public async Task<bool> SetSortOrderAsync(Guid smartPlaylistId, SmartPlaylistSortOrder sortOrder)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var smartPlaylist = await context.SmartPlaylists.FindAsync(smartPlaylistId).ConfigureAwait(false);
        if (smartPlaylist is null) return false;

        smartPlaylist.SortOrder = sortOrder;
        await SaveChangesAsync(context, smartPlaylist).ConfigureAwait(false);
        return true;
    }

    public async Task<PagedResult<Song>> GetMatchingSongsPagedAsync(Guid smartPlaylistId, int pageNumber, int pageSize, string? searchTerm = null, SongSortOrder? sortOrder = null, CancellationToken token = default)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var smartPlaylist = await context.SmartPlaylists.AsNoTracking()
            .Include(sp => sp.Rules)
            .FirstOrDefaultAsync(sp => sp.Id == smartPlaylistId, token).ConfigureAwait(false);

        if (smartPlaylist is null)
        {
            return new PagedResult<Song>
            {
                Items = new List<Song>(),
                TotalCount = 0,
                PageNumber = pageNumber,
                PageSize = pageSize
            };
        }

        var query = SongQuery.ExcludeHeavyFields(SmartPlaylistQueryBuilder.BuildQuery(context, smartPlaylist, searchTerm, sortOrder));
        var totalCount = await SmartPlaylistQueryBuilder.BuildCountQuery(context, smartPlaylist, searchTerm).CountAsync(token).ConfigureAwait(false);

        var pagedData = await query
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(token).ConfigureAwait(false);

        return new PagedResult<Song>
        {
            Items = pagedData,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<int> GetMatchingSongCountAsync(Guid smartPlaylistId, string? searchTerm = null, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var smartPlaylist = await context.SmartPlaylists.AsNoTracking()
            .Include(sp => sp.Rules)
            .FirstOrDefaultAsync(sp => sp.Id == smartPlaylistId, token).ConfigureAwait(false);

        if (smartPlaylist is null)
            return 0;

        return await SmartPlaylistQueryBuilder.BuildCountQuery(context, smartPlaylist, searchTerm).CountAsync(token).ConfigureAwait(false);
    }

    public async Task<int> GetMatchingSongCountAsync(SmartPlaylist smartPlaylist, string? searchTerm = null, CancellationToken token = default)
    {
        var stored = new SmartPlaylist
        {
            MatchAllRules = smartPlaylist.MatchAllRules,
            Rules = ToStoredRules(smartPlaylist.Rules, smartPlaylist.Id)
        };
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await SmartPlaylistQueryBuilder.BuildCountQuery(context, stored, searchTerm).CountAsync(token).ConfigureAwait(false);
    }

    public async Task<List<Guid>> GetMatchingSongIdsAsync(Guid smartPlaylistId, string? searchTerm = null, SongSortOrder? sortOrder = null, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var smartPlaylist = await context.SmartPlaylists.AsNoTracking()
            .Include(sp => sp.Rules)
            .FirstOrDefaultAsync(sp => sp.Id == smartPlaylistId, token).ConfigureAwait(false);

        if (smartPlaylist is null)
            return new List<Guid>();

        return await SmartPlaylistQueryBuilder.BuildQuery(context, smartPlaylist, searchTerm, sortOrder)
            .Select(s => s.Id)
            .ToListAsync(token).ConfigureAwait(false);
    }

    public async Task<Dictionary<Guid, int>> GetAllMatchingSongCountsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var smartPlaylists = await context.SmartPlaylists.AsNoTracking()
            .Include(sp => sp.Rules)
            .ToListAsync().ConfigureAwait(false);

        var counts = new Dictionary<Guid, int>(smartPlaylists.Count);
        foreach (var smartPlaylist in smartPlaylists)
        {
            counts[smartPlaylist.Id] = await SmartPlaylistQueryBuilder.BuildCountQuery(context, smartPlaylist)
                .CountAsync().ConfigureAwait(false);
        }
        return counts;
    }

    private static List<SmartPlaylistRule> ToStoredRules(IEnumerable<SmartPlaylistRule> rules, Guid smartPlaylistId) =>
        rules.Select((rule, order) => new SmartPlaylistRule
        {
            SmartPlaylistId = smartPlaylistId,
            Field = rule.Field,
            Operator = rule.Operator,
            Value = SmartPlaylistRuleValue.ToStoredValue(rule.Field, rule.Operator, rule.Value),
            SecondValue = SmartPlaylistRuleValue.ToStoredValue(rule.Field, rule.Operator, rule.SecondValue),
            Order = order
        }).ToList();

    /// <summary>
    ///     Saves a change to the playlist, stamping it as modified, and tells listeners.
    /// </summary>
    private async Task SaveChangesAsync(MusicDbContext context, SmartPlaylist playlist)
    {
        playlist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);
        PlaylistUpdated?.Invoke(this, new PlaylistUpdatedEventArgs(playlist.Id, playlist.CoverImageUri, playlist.DateModified));
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static IQueryable<string> OtherNames(MusicDbContext context, Guid smartPlaylistId) =>
        context.SmartPlaylists.Where(sp => sp.Id != smartPlaylistId).Select(sp => sp.Name);

    /// <returns>False when the cover couldn't be replaced and the current one stays.</returns>
    private async Task<bool> SetCoverAsync(SmartPlaylist playlist, string? path)
    {
        var (replaced, cover) = await _rules.ReplaceCoverAsync(playlist.Id, path).ConfigureAwait(false);
        if (replaced) playlist.CoverImageUri = cover;
        return replaced;
    }

    private static void SanitizePaging(ref int pageNumber, ref int pageSize)
    {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 500) pageSize = 500;
    }

}
