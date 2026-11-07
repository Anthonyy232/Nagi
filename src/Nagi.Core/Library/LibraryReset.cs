using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.MusicServers;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Implementations;

namespace Nagi.Core.Library;

public sealed class LibraryReset : ILibraryReset
{
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly IFileSystemService _fileSystem;
    private readonly IPathConfiguration _pathConfig;
    private readonly IMusicServerService _musicServers;
    private readonly ArtworkService _artwork;
    private readonly LibraryNotifier _notifier;
    private readonly LibraryWriteLock _writeLock;
    private readonly ILogger<LibraryReset> _logger;

    public LibraryReset(
        IDbContextFactory<MusicDbContext> contextFactory,
        IFileSystemService fileSystem,
        IPathConfiguration pathConfig,
        IMusicServerService musicServers,
        ArtworkService artwork,
        LibraryNotifier notifier,
        LibraryWriteLock writeLock,
        ILogger<LibraryReset> logger)
    {
        _contextFactory = contextFactory;
        _fileSystem = fileSystem;
        _pathConfig = pathConfig;
        _musicServers = musicServers;
        _artwork = artwork;
        _notifier = notifier;
        _writeLock = writeLock;
        _logger = logger;
    }

    public async Task ClearAllLibraryDataAsync()
    {
        _writeLock.CancelActiveScan("library data reset");
        _musicServers.CancelSyncs();
        await _writeLock.WaitAsync(_writeLock.ShutdownToken).ConfigureAwait(false);
        try
        {
            await _artwork.RunWithoutMetadataFetchAsync(ClearAsync).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        _notifier.Raise(LibraryChangeKind.LibraryCleared);
    }

    private async Task ClearAsync()
    {
        _logger.LogInformation("Starting to clear all library data and cache files.");
        List<Guid> serverFolderIds;
        await using (var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false))
        {
            await using var transaction = await context.Database.BeginTransactionAsync().ConfigureAwait(false);
            try
            {
                serverFolderIds = await context.MusicServers.Select(s => s.FolderId).ToListAsync().ConfigureAwait(false);
                // Every table of the model, so a new one can't be left behind. With the foreign keys checked at commit,
                // when every table is empty, the order doesn't matter.
                var tables = context.Model.GetEntityTypes().Select(entity => entity.GetTableName()).OfType<string>().Distinct();
                await context.Database.ExecuteSqlRawAsync(string.Concat(
                        ["PRAGMA defer_foreign_keys = ON;", .. tables.Select(table => $"DELETE FROM \"{table}\";")]))
                    .ConfigureAwait(false);
                await transaction.CommitAsync().ConfigureAwait(false);
                _logger.LogInformation("Successfully deleted all data from the database.");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync().ConfigureAwait(false);
                _logger.LogError(ex, "Database reset failed and was rolled back.");
                throw;
            }
        }

        // A server's sign-in lives outside the database.
        _musicServers.ForgetSignIns(serverFolderIds);

        try
        {
            foreach (var path in new[] { _pathConfig.AlbumArtCachePath, _pathConfig.ArtistImageCachePath,
                         _pathConfig.PlaylistImageCachePath, _pathConfig.LrcCachePath })
            {
                if (_fileSystem.DirectoryExists(path)) _fileSystem.DeleteDirectory(path, true);
                _fileSystem.CreateDirectory(path);
            }
            _logger.LogInformation("Successfully cleared and recreated cache directories.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear and recreate cache directories during library reset.");
        }
    }
}
