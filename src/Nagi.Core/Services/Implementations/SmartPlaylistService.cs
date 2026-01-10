using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Manages smart playlist operations including CRUD, rule management, and query execution.
/// </summary>
public class SmartPlaylistService : ISmartPlaylistService
{
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly IFileSystemService _fileSystem;
    private readonly IPathConfiguration _pathConfig;
    private readonly ILogger<SmartPlaylistService> _logger;
    private readonly IImageProcessor _imageProcessor;
    private readonly SmartPlaylistQueryBuilder _queryBuilder;

    /// <inheritdoc />
    public event EventHandler<PlaylistUpdatedEventArgs>? PlaylistUpdated;

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

    /// <summary>
    ///     Creates a projection of Song entities that excludes heavy, rarely-used fields (Lyrics, Comment, Copyright)
    ///     while preserving essential metadata and top-level navigation properties.
    /// </summary>
    private static IQueryable<Song> ExcludeHeavyFields(IQueryable<Song> query)
    {
        return query.Select(s => new Song
        {
            Id = s.Id,
            Title = s.Title,
            AlbumId = s.AlbumId,
            Album = s.Album,
            ArtistId = s.ArtistId,
            Artist = s.Artist,
            Composer = s.Composer,
            FolderId = s.FolderId,
            Folder = s.Folder,
            DurationTicks = s.DurationTicks,
            AlbumArtUriFromTrack = s.AlbumArtUriFromTrack,
            FilePath = s.FilePath,
            DirectoryPath = s.DirectoryPath,
            Year = s.Year,
            TrackNumber = s.TrackNumber,
            TrackCount = s.TrackCount,
            DiscNumber = s.DiscNumber,
            DiscCount = s.DiscCount,
            SampleRate = s.SampleRate,
            Bitrate = s.Bitrate,
            Channels = s.Channels,
            DateAddedToLibrary = s.DateAddedToLibrary,
            FileCreatedDate = s.FileCreatedDate,
            FileModifiedDate = s.FileModifiedDate,
            LightSwatchId = s.LightSwatchId,
            DarkSwatchId = s.DarkSwatchId,
            Rating = s.Rating,
            IsLoved = s.IsLoved,
            PlayCount = s.PlayCount,
            SkipCount = s.SkipCount,
            LastPlayedDate = s.LastPlayedDate,
            // Lyrics = null, -- EXCLUDED (up to 50KB per song)
            LrcFilePath = s.LrcFilePath,
            Bpm = s.Bpm,
            Grouping = s.Grouping,
            // Copyright = null, -- EXCLUDED (up to 1KB per song)
            // Comment = null, -- EXCLUDED (up to 1KB per song)
            Conductor = s.Conductor,
            MusicBrainzTrackId = s.MusicBrainzTrackId,
            MusicBrainzReleaseId = s.MusicBrainzReleaseId
            // Collection navigations (Genres, PlaylistSongs, etc.) are excluded from projections 
            // as EF Core cannot reliably hydrate them in entity-type Select() calls.
            // Use GetSongByIdAsync(id) for full data.
        });
    }

    #region CRUD Operations

    /// <inheritdoc />
    public async Task<SmartPlaylist?> CreateSmartPlaylistAsync(string name, string? description = null,
        string? coverImageUri = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Smart playlist name cannot be empty.", nameof(name));

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Check for duplicate name (case-insensitive)
        var trimmedName = name.Trim();
        var exists = await context.SmartPlaylists
            .AnyAsync(sp => sp.Name.ToLower() == trimmedName.ToLower()).ConfigureAwait(false);
        
        if (exists)
        {
            _logger.LogWarning("Cannot create smart playlist: name '{Name}' already exists", trimmedName);
            return null;
        }

        var smartPlaylist = new SmartPlaylist
        {
            Name = trimmedName,
            Description = description,
            DateCreated = DateTime.UtcNow,
            DateModified = DateTime.UtcNow
        };

        if (!string.IsNullOrEmpty(coverImageUri) && _fileSystem.FileExists(coverImageUri))
        {
            // Read, process, and save the image
            var cachePath = _pathConfig.PlaylistImageCachePath;
            var originalBytes = await _fileSystem.ReadAllBytesAsync(coverImageUri).ConfigureAwait(false);
            var processedBytes = await _imageProcessor.ProcessImageBytesAsync(originalBytes).ConfigureAwait(false);
            await ImageStorageHelper.SaveImageBytesAsync(_fileSystem, cachePath, smartPlaylist.Id.ToString(), ".custom", processedBytes).ConfigureAwait(false);
            smartPlaylist.CoverImageUri = ImageStorageHelper.FindImage(_fileSystem, cachePath, smartPlaylist.Id.ToString(), ".custom");
        }

        context.SmartPlaylists.Add(smartPlaylist);
        await context.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogInformation("Created smart playlist '{Name}' with ID {Id}", smartPlaylist.Name, smartPlaylist.Id);
        return smartPlaylist;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteSmartPlaylistAsync(Guid smartPlaylistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var rowsAffected = await context.SmartPlaylists
            .Where(sp => sp.Id == smartPlaylistId)
            .ExecuteDeleteAsync().ConfigureAwait(false);

        if (rowsAffected > 0)
            _logger.LogInformation("Deleted smart playlist with ID {Id}", smartPlaylistId);

        return rowsAffected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateSmartPlaylistAsync(SmartPlaylist smartPlaylist)
    {
        ArgumentNullException.ThrowIfNull(smartPlaylist);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        
        // Fetch the existing entity to avoid tracking issues with detached entities
        var existing = await context.SmartPlaylists.FindAsync(smartPlaylist.Id).ConfigureAwait(false);
        if (existing is null) return false;

        // Update properties individually
        existing.Name = smartPlaylist.Name;
        existing.Description = smartPlaylist.Description;
        existing.CoverImageUri = smartPlaylist.CoverImageUri;
        existing.MatchAllRules = smartPlaylist.MatchAllRules;
        existing.SortOrder = smartPlaylist.SortOrder;
        existing.DateModified = DateTime.UtcNow;

        await context.SaveChangesAsync().ConfigureAwait(false);
        PlaylistUpdated?.Invoke(this, new PlaylistUpdatedEventArgs(existing.Id, existing.CoverImageUri));
        return true;
    }

    /// <inheritdoc />
    public async Task<SmartPlaylist?> GetSmartPlaylistByIdAsync(Guid smartPlaylistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.SmartPlaylists.AsNoTracking()
            .Include(sp => sp.Rules.OrderBy(r => r.Order))
            .FirstOrDefaultAsync(sp => sp.Id == smartPlaylistId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SmartPlaylist>> GetAllSmartPlaylistsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.SmartPlaylists.AsNoTracking()
            .Include(sp => sp.Rules.OrderBy(r => r.Order))
            .OrderBy(sp => sp.Name)
            .ThenBy(sp => sp.Id)
            .ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RenameSmartPlaylistAsync(Guid smartPlaylistId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("New smart playlist name cannot be empty.", nameof(newName));

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var smartPlaylist = await context.SmartPlaylists.FindAsync(smartPlaylistId).ConfigureAwait(false);
        if (smartPlaylist is null) return false;

        var trimmedName = newName.Trim();
        
        // Check if the new name already exists (case-insensitive), excluding the current playlist
        var nameExists = await context.SmartPlaylists
            .AnyAsync(sp => sp.Id != smartPlaylistId && sp.Name.ToLower() == trimmedName.ToLower()).ConfigureAwait(false);
        
        if (nameExists)
        {
            _logger.LogWarning("Cannot rename smart playlist: name '{Name}' already exists", trimmedName);
            return false;
        }

        smartPlaylist.Name = trimmedName;
        smartPlaylist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);
        PlaylistUpdated?.Invoke(this, new PlaylistUpdatedEventArgs(smartPlaylist.Id, smartPlaylist.CoverImageUri));
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateSmartPlaylistCoverAsync(Guid smartPlaylistId, string? newCoverImageUri)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var smartPlaylist = await context.SmartPlaylists.FindAsync(smartPlaylistId).ConfigureAwait(false);
        if (smartPlaylist is null) return false;

        if (!string.IsNullOrEmpty(newCoverImageUri) && _fileSystem.FileExists(newCoverImageUri))
        {
            // Read, process, and save the image
            var cachePath = _pathConfig.PlaylistImageCachePath;
            var originalBytes = await _fileSystem.ReadAllBytesAsync(newCoverImageUri).ConfigureAwait(false);
            var processedBytes = await _imageProcessor.ProcessImageBytesAsync(originalBytes).ConfigureAwait(false);
            await ImageStorageHelper.SaveImageBytesAsync(_fileSystem, cachePath, smartPlaylistId.ToString(), ".custom", processedBytes).ConfigureAwait(false);
            
            var newPath = ImageStorageHelper.FindImage(_fileSystem, cachePath, smartPlaylistId.ToString(), ".custom");
            smartPlaylist.CoverImageUri = newPath;
        }
        else
        {
            // Remove custom image if setting to null/empty
            var cachePath = _pathConfig.PlaylistImageCachePath;
            ImageStorageHelper.DeleteImage(_fileSystem, cachePath, smartPlaylistId.ToString(), ".custom");
            smartPlaylist.CoverImageUri = null;
        }

        smartPlaylist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);
        PlaylistUpdated?.Invoke(this, new PlaylistUpdatedEventArgs(smartPlaylist.Id, smartPlaylist.CoverImageUri));
        return true;
    }

    #endregion

    #region Configuration

    /// <inheritdoc />
    public async Task<bool> SetMatchAllRulesAsync(Guid smartPlaylistId, bool matchAll)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var smartPlaylist = await context.SmartPlaylists.FindAsync(smartPlaylistId).ConfigureAwait(false);
        if (smartPlaylist is null) return false;

        smartPlaylist.MatchAllRules = matchAll;
        smartPlaylist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
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

    #endregion

    #region Rule Management

    /// <inheritdoc />
    public async Task<SmartPlaylistRule?> AddRuleAsync(Guid smartPlaylistId, SmartPlaylistField field,
        SmartPlaylistOperator op, string? value, string? secondValue = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var smartPlaylist = await context.SmartPlaylists
            .Include(sp => sp.Rules)
            .FirstOrDefaultAsync(sp => sp.Id == smartPlaylistId).ConfigureAwait(false);

        if (smartPlaylist is null) return null;

        var maxOrder = smartPlaylist.Rules.Any() ? smartPlaylist.Rules.Max(r => r.Order) : -1;

        var rule = new SmartPlaylistRule
        {
            SmartPlaylistId = smartPlaylistId,
            Field = field,
            Operator = op,
            Value = value,
            SecondValue = secondValue,
            Order = maxOrder + 1
        };

        context.SmartPlaylistRules.Add(rule);
        smartPlaylist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogDebug("Added rule to smart playlist {Id}: {Field} {Operator} {Value}",
            smartPlaylistId, field, op, value);

        return rule;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateRuleAsync(Guid ruleId, SmartPlaylistField field, SmartPlaylistOperator op,
        string? value, string? secondValue = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var rule = await context.SmartPlaylistRules
            .Include(r => r.SmartPlaylist)
            .FirstOrDefaultAsync(r => r.Id == ruleId).ConfigureAwait(false);

        if (rule is null) return false;

        rule.Field = field;
        rule.Operator = op;
        rule.Value = value;
        rule.SecondValue = secondValue;
        rule.SmartPlaylist.DateModified = DateTime.UtcNow;

        await context.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveRuleAsync(Guid ruleId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var rule = await context.SmartPlaylistRules
            .Include(r => r.SmartPlaylist)
            .FirstOrDefaultAsync(r => r.Id == ruleId).ConfigureAwait(false);

        if (rule is null) return false;

        var smartPlaylistId = rule.SmartPlaylistId;
        context.SmartPlaylistRules.Remove(rule);
        rule.SmartPlaylist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);

        // Reindex remaining rules
        await ReindexRulesAsync(context, smartPlaylistId).ConfigureAwait(false);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ReorderRulesAsync(Guid smartPlaylistId, IEnumerable<Guid> orderedRuleIds)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var smartPlaylist = await context.SmartPlaylists.FindAsync(smartPlaylistId).ConfigureAwait(false);
        if (smartPlaylist is null) return false;

        var rules = await context.SmartPlaylistRules
            .Where(r => r.SmartPlaylistId == smartPlaylistId)
            .ToListAsync().ConfigureAwait(false);

        var ruleMap = rules.ToDictionary(r => r.Id);
        var newOrderList = orderedRuleIds.ToList();

        for (var i = 0; i < newOrderList.Count; i++)
        {
            if (ruleMap.TryGetValue(newOrderList[i], out var rule))
                rule.Order = i;
        }

        smartPlaylist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    private static async Task ReindexRulesAsync(MusicDbContext context, Guid smartPlaylistId)
    {
        var rules = await context.SmartPlaylistRules
            .Where(r => r.SmartPlaylistId == smartPlaylistId)
            .OrderBy(r => r.Order)
            .ToListAsync().ConfigureAwait(false);

        for (var i = 0; i < rules.Count; i++)
            rules[i].Order = i;

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ReplaceAllRulesAsync(Guid smartPlaylistId, IEnumerable<SmartPlaylistRule> newRules)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var smartPlaylist = await context.SmartPlaylists
            .Include(sp => sp.Rules)
            .FirstOrDefaultAsync(sp => sp.Id == smartPlaylistId).ConfigureAwait(false);

        if (smartPlaylist is null) return false;

        // Remove all existing rules
        context.SmartPlaylistRules.RemoveRange(smartPlaylist.Rules);

        // Add new rules with proper order and playlist ID
        var order = 0;
        foreach (var rule in newRules)
        {
            rule.Id = Guid.NewGuid();
            rule.SmartPlaylistId = smartPlaylistId;
            rule.Order = order++;
            context.SmartPlaylistRules.Add(rule);
        }

        smartPlaylist.DateModified = DateTime.UtcNow;
        await context.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogDebug("Replaced all rules for smart playlist {Id} with {Count} new rules", smartPlaylistId, order);
        return true;
    }

    #endregion

    #region Query Execution

    /// <inheritdoc />
    public async Task<IEnumerable<Song>> GetMatchingSongsAsync(Guid smartPlaylistId, string? searchTerm = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var smartPlaylist = await context.SmartPlaylists.AsNoTracking()
            .Include(sp => sp.Rules)
            .FirstOrDefaultAsync(sp => sp.Id == smartPlaylistId).ConfigureAwait(false);

        if (smartPlaylist is null)
            return Enumerable.Empty<Song>();

        return await ExcludeHeavyFields(_queryBuilder.BuildQuery(context, smartPlaylist, searchTerm)).ToListAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PagedResult<Song>> GetMatchingSongsPagedAsync(Guid smartPlaylistId, int pageNumber, int pageSize, string? searchTerm = null)
    {
        SanitizePaging(ref pageNumber, ref pageSize);

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var smartPlaylist = await context.SmartPlaylists.AsNoTracking()
            .Include(sp => sp.Rules)
            .FirstOrDefaultAsync(sp => sp.Id == smartPlaylistId).ConfigureAwait(false);

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

        var query = ExcludeHeavyFields(_queryBuilder.BuildQuery(context, smartPlaylist, searchTerm));
        var totalCount = await _queryBuilder.BuildCountQuery(context, smartPlaylist, searchTerm).CountAsync().ConfigureAwait(false);

        var pagedData = await query
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync().ConfigureAwait(false);

        return new PagedResult<Song>
        {
            Items = pagedData,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    /// <inheritdoc />
    public async Task<int> GetMatchingSongCountAsync(Guid smartPlaylistId, string? searchTerm = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var smartPlaylist = await context.SmartPlaylists.AsNoTracking()
            .Include(sp => sp.Rules)
            .FirstOrDefaultAsync(sp => sp.Id == smartPlaylistId).ConfigureAwait(false);

        if (smartPlaylist is null)
            return 0;

        return await _queryBuilder.BuildCountQuery(context, smartPlaylist, searchTerm).CountAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> GetMatchingSongCountAsync(SmartPlaylist smartPlaylist, string? searchTerm = null)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await _queryBuilder.BuildCountQuery(context, smartPlaylist, searchTerm).CountAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetMatchingSongIdsAsync(Guid smartPlaylistId)
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var smartPlaylist = await context.SmartPlaylists.AsNoTracking()
            .Include(sp => sp.Rules)
            .FirstOrDefaultAsync(sp => sp.Id == smartPlaylistId).ConfigureAwait(false);

        if (smartPlaylist is null)
            return new List<Guid>();

        return await _queryBuilder.BuildQuery(context, smartPlaylist)
            .Select(s => s.Id)
            .ToListAsync().ConfigureAwait(false);
    }

    #endregion

    /// <inheritdoc />
    public async Task<Dictionary<Guid, int>> GetAllMatchingSongCountsAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var smartPlaylists = await context.SmartPlaylists.AsNoTracking()
            .Include(sp => sp.Rules)
            .ToListAsync().ConfigureAwait(false);

        if (smartPlaylists.Count == 0)
            return new Dictionary<Guid, int>();

        // Parallelize count queries for better performance with many smart playlists
        // Each task gets its own DbContext since EF Core contexts are not thread-safe
        var countTasks = smartPlaylists.Select(async smartPlaylist =>
        {
            await using var countContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);
            var count = await _queryBuilder.BuildCountQuery(countContext, smartPlaylist).CountAsync().ConfigureAwait(false);
            return (smartPlaylist.Id, count);
        });

        var results = await Task.WhenAll(countTasks).ConfigureAwait(false);
        return results.ToDictionary(r => r.Id, r => r.count);
    }

    #region Helpers

    private static void SanitizePaging(ref int pageNumber, ref int pageSize)
    {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 500) pageSize = 500;
    }

    #endregion
}
