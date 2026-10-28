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
    private readonly IFileSystemService _fileSystem;
    private readonly IPathConfiguration _pathConfig;
    private readonly ILogger<SmartPlaylistService> _logger;
    private readonly IImageProcessor _imageProcessor;
    private readonly SmartPlaylistQueryBuilder _queryBuilder;

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
        _fileSystem = fileSystem;
        _pathConfig = pathConfig;
        _imageProcessor = imageProcessor;
        _logger = logger;
        _queryBuilder = new SmartPlaylistQueryBuilder();
    }

    public async Task<SmartPlaylist> CreateSmartPlaylistAsync(SmartPlaylist playlist) =>
        (await SaveSmartPlaylistAsync(playlist, isNew: true).ConfigureAwait(false))!;

    public Task<SmartPlaylist?> UpdateSmartPlaylistAsync(SmartPlaylist playlist) =>
        SaveSmartPlaylistAsync(playlist, isNew: false);

    private async Task<SmartPlaylist?> SaveSmartPlaylistAsync(SmartPlaylist draft, bool isNew)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Name);
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var playlist = isNew ? new SmartPlaylist() : await context.SmartPlaylists
            .Include(sp => sp.Rules).FirstOrDefaultAsync(sp => sp.Id == draft.Id).ConfigureAwait(false);
        if (playlist is null) return null;

        playlist.Name = ArtistNameHelper.NormalizeStringCore(draft.Name) ?? draft.Name;
        playlist.Description = draft.Description;
        playlist.MatchAllRules = draft.MatchAllRules;
        playlist.SortOrder = draft.SortOrder;
        playlist.DateModified = DateTime.UtcNow;
        if (!string.Equals(playlist.CoverImageUri, draft.CoverImageUri, StringComparison.Ordinal))
            await SetCoverAsync(playlist, draft.CoverImageUri).ConfigureAwait(false);

        context.SmartPlaylistRules.RemoveRange(playlist.Rules);
        playlist.Rules = draft.Rules.Select((rule, order) => new SmartPlaylistRule
        {
            SmartPlaylistId = playlist.Id,
            Field = rule.Field,
            Operator = rule.Operator,
            Value = ArtistNameHelper.NormalizeStringCore(SmartPlaylistRuleValue.ToStoredValue(rule.Field, rule.Operator, rule.Value)),
            SecondValue = ArtistNameHelper.NormalizeStringCore(SmartPlaylistRuleValue.ToStoredValue(rule.Field, rule.Operator, rule.SecondValue)),
            Order = order
        }).ToList();
        if (isNew)
            context.SmartPlaylists.Add(playlist);
        else
            context.SmartPlaylistRules.AddRange(playlist.Rules);

        await context.SaveChangesAsync().ConfigureAwait(false);
        if (!isNew)
            PlaylistUpdated?.Invoke(this, new PlaylistUpdatedEventArgs(playlist.Id, playlist.CoverImageUri, playlist.DateModified));
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
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
        try
        {
            ImageStorageHelper.DeleteImage(_fileSystem, _pathConfig.PlaylistImageCachePath, smartPlaylistId.ToString(), ".custom");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete cached cover for deleted smart playlist {Id}.", smartPlaylistId);
        }
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
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("New smart playlist name cannot be empty.", nameof(newName));

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var smartPlaylist = await context.SmartPlaylists.FindAsync(smartPlaylistId).ConfigureAwait(false);
        if (smartPlaylist is null) return false;

        var normalizedName = ArtistNameHelper.NormalizeStringCore(newName) ?? newName;

        var nameExists = await context.SmartPlaylists
            .AnyAsync(sp => sp.Id != smartPlaylistId && sp.Name == normalizedName).ConfigureAwait(false);

        if (nameExists)
        {
            _logger.LogWarning("Cannot rename smart playlist: name '{Name}' already exists", normalizedName);
            return false;
        }

        smartPlaylist.Name = normalizedName;
        smartPlaylist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);
        PlaylistUpdated?.Invoke(this, new PlaylistUpdatedEventArgs(smartPlaylist.Id, smartPlaylist.CoverImageUri, smartPlaylist.DateModified));
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task<bool> UpdateSmartPlaylistCoverAsync(Guid smartPlaylistId, string? newCoverImageUri)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var smartPlaylist = await context.SmartPlaylists.FindAsync(smartPlaylistId).ConfigureAwait(false);
        if (smartPlaylist is null) return false;

        await SetCoverAsync(smartPlaylist, newCoverImageUri).ConfigureAwait(false);

        smartPlaylist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);
        PlaylistUpdated?.Invoke(this, new PlaylistUpdatedEventArgs(smartPlaylist.Id, smartPlaylist.CoverImageUri, smartPlaylist.DateModified));
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
        smartPlaylist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);
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

        var query = SongQuery.ExcludeHeavyFields(_queryBuilder.BuildQuery(context, smartPlaylist, searchTerm, sortOrder));
        var totalCount = await _queryBuilder.BuildCountQuery(context, smartPlaylist, searchTerm).CountAsync(token).ConfigureAwait(false);

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

        return await _queryBuilder.BuildCountQuery(context, smartPlaylist, searchTerm).CountAsync(token).ConfigureAwait(false);
    }

    public async Task<int> GetMatchingSongCountAsync(SmartPlaylist smartPlaylist, string? searchTerm = null, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await _queryBuilder.BuildCountQuery(context, smartPlaylist, searchTerm).CountAsync(token).ConfigureAwait(false);
    }

    public async Task<List<Guid>> GetMatchingSongIdsAsync(Guid smartPlaylistId, string? searchTerm = null, SongSortOrder? sortOrder = null, CancellationToken token = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var smartPlaylist = await context.SmartPlaylists.AsNoTracking()
            .Include(sp => sp.Rules)
            .FirstOrDefaultAsync(sp => sp.Id == smartPlaylistId, token).ConfigureAwait(false);

        if (smartPlaylist is null)
            return new List<Guid>();

        return await _queryBuilder.BuildQuery(context, smartPlaylist, searchTerm, sortOrder)
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
            counts[smartPlaylist.Id] = await _queryBuilder.BuildCountQuery(context, smartPlaylist)
                .CountAsync().ConfigureAwait(false);
        }
        return counts;
    }

    private async Task SetCoverAsync(SmartPlaylist playlist, string? path)
    {
        var cachePath = _pathConfig.PlaylistImageCachePath;
        if (string.IsNullOrEmpty(path))
        {
            ImageStorageHelper.DeleteImage(_fileSystem, cachePath, playlist.Id.ToString(), ".custom");
            playlist.CoverImageUri = null;
            return;
        }

        var originalBytes = await _fileSystem.ReadAllBytesAsync(path).ConfigureAwait(false);
        var processedBytes = await _imageProcessor.ProcessImageBytesAsync(originalBytes).ConfigureAwait(false);
        var newPath = await ImageStorageHelper.SaveImageBytesAsync(_fileSystem, cachePath,
            playlist.Id.ToString(), ".custom", processedBytes).ConfigureAwait(false);
        if (newPath is not null) playlist.CoverImageUri = newPath;
    }

    private static void SanitizePaging(ref int pageNumber, ref int pageSize)
    {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 500) pageSize = 500;
    }

}
