using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Artwork;
using Nagi.Core.Constants;
using Nagi.Core.Data;
using Nagi.Core.Helpers;
using Nagi.Core.Listening;
using Nagi.Core.Models;
using Nagi.Core.Playback;
using Nagi.Core.Playlists;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Settings;

namespace Nagi.Core.Library;

public sealed class LibraryFolderService : ILibraryFolderService
{
    private readonly IDbContextFactory<MusicDbContext> _contextFactory;
    private readonly IFileSystemService _fileSystem;
    private readonly IMetadataService _metadataService;
    private readonly IImageProcessor _imageProcessor;
    private readonly IReplayGainService _replayGainService;
    private readonly ISettingsService _settingsService;
    private readonly LibraryImporter _importer;
    private readonly ArtworkService _artwork;
    private readonly LibraryNotifier _notifier;
    private readonly LibraryWriteLock _writeLock;
    private readonly ILogger<LibraryFolderService> _logger;

    public LibraryFolderService(
        IDbContextFactory<MusicDbContext> contextFactory,
        IFileSystemService fileSystem,
        IMetadataService metadataService,
        IImageProcessor imageProcessor,
        IReplayGainService replayGainService,
        ISettingsService settingsService,
        LibraryImporter importer,
        ArtworkService artwork,
        LibraryNotifier notifier,
        LibraryWriteLock writeLock,
        ILogger<LibraryFolderService> logger)
    {
        _contextFactory = contextFactory;
        _fileSystem = fileSystem;
        _metadataService = metadataService;
        _imageProcessor = imageProcessor;
        _replayGainService = replayGainService;
        _settingsService = settingsService;
        _importer = importer;
        _artwork = artwork;
        _notifier = notifier;
        _writeLock = writeLock;
        _logger = logger;
    }

    /// <summary>
    ///     How a scan was started, which decides what it may do to the library.
    /// </summary>
    private enum ScanKind
    {
        /// <summary>The user rescanned one folder; it cancels other scans, and a folder that is gone is removed.</summary>
        Manual,

        /// <summary>The folder watcher saw changes; it never cancels another scan or removes a folder.</summary>
        Background,

        /// <summary>Every folder; one that can't be reached is skipped.</summary>
        All,

        /// <summary>Every file of every folder is read again, and a folder that fails stops the scan with an error.</summary>
        Forced
    }

    public async Task<Folder?> AddFolderAsync(string path, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        await using var context = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Canonicalize so that Z:\X and \\server\share\X resolve to the same identity.
        // Folder.Path is NOCASE-collated, but NOCASE only folds ASCII case — it doesn't
        // collapse mapped-drive vs UNC or Unicode equivalence classes.
        var normalizedPath = _fileSystem.NormalizePath(path);

        var roots = await context.Folders.AsNoTracking().Where(f => f.ParentFolderId == null).ToListAsync().ConfigureAwait(false);
        if (roots.FirstOrDefault(f => f.Path.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)) is { } existingFolder)
            return existingFolder;

        // Scans assume each file belongs to one root, so a root inside another would re-add the other's songs.
        if (roots.Any(f => PathCanonicalizer.IsSameOrInside(normalizedPath, f.Path) || PathCanonicalizer.IsSameOrInside(f.Path, normalizedPath)))
            throw new FolderOverlapsException(normalizedPath);

        var folderName = Path.GetFileName(normalizedPath);
        var folder = new Folder
        {
            Path = normalizedPath,
            Name = name ?? (folderName.Length == 0 ? normalizedPath : folderName)
        };
        try
        {
            folder.LastModifiedDate = _fileSystem.GetLastWriteTimeUtc(normalizedPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not get LastWriteTimeUtc for folder {FolderPath}", normalizedPath);
            folder.LastModifiedDate = null;
        }

        context.Folders.Add(folder);
        await context.SaveChangesAsync().ConfigureAwait(false);
        _notifier.Raise(LibraryChangeKind.RootAdded, folder.Id);
        return folder;
    }

    public async Task<bool> RemoveFolderAsync(Guid folderId)
    {
        _writeLock.CancelActiveScan("folder removal");
        await _writeLock.WaitAsync(_writeLock.ShutdownToken).ConfigureAwait(false);
        try
        {
            if (!await IsFolderOnDiskAsync(folderId, _writeLock.ShutdownToken).ConfigureAwait(false))
            {
                _logger.LogWarning("Not removing folder {FolderId}: it isn't a folder on disk in the library.", folderId);
                return false;
            }

            if (!await _importer.RemoveRootAsync(folderId, _writeLock.ShutdownToken).ConfigureAwait(false)) return false;
        }
        finally
        {
            _writeLock.Release();
        }

        _notifier.Raise(LibraryChangeKind.RootRemoved, folderId);
        return true;
    }

    public Task<bool> RescanFolderForMusicAsync(Guid folderId, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ScanAsync(ScanKind.Manual, folderId, progress, cancellationToken);

    public Task<bool> RefreshFolderInBackgroundAsync(Guid folderId, CancellationToken cancellationToken = default) =>
        ScanAsync(ScanKind.Background, folderId, null, cancellationToken);

    public Task<bool> RefreshAllFoldersAsync(IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ScanAsync(ScanKind.All, null, progress, cancellationToken);

    public Task<bool> ForceRescanMetadataAsync(IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ScanAsync(ScanKind.Forced, null, progress, cancellationToken);

    /// <param name="folderId">The folder to scan, or null for every folder on disk.</param>
    private async Task<bool> ScanAsync(ScanKind kind, Guid? folderId, IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _writeLock.ShutdownToken);
        var token = operationCts.Token;
        if (kind != ScanKind.Background)
        {
            _writeLock.CancelActiveScan($"{kind} scan");
            _writeLock.SetActiveScan(operationCts);
        }

        try
        {
            bool changed;
            if (folderId is { } id)
            {
                changed = await ScanFolderAsync(kind, id, progress, token).ConfigureAwait(false);
            }
            else
            {
                var folders = await GetRootFoldersAsync(token).ConfigureAwait(false);
                if (folders.Count == 0)
                {
                    _logger.LogDebug("No folders found in the library to refresh.");
                    progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_NoFoldersToRefresh, Percentage = 100 });
                    return false;
                }

                changed = false;
                foreach (var (index, folder) in folders.Index())
                {
                    token.ThrowIfCancellationRequested();
                    changed |= await ScanFolderAsync(kind, folder.Id, FolderProgress(progress, index, folders.Count, folder.Name), token)
                        .ConfigureAwait(false);
                }
            }

            token.ThrowIfCancellationRequested();
            if (changed && _settingsService.Get(CoreSettings.VolumeNormalization))
                await RunReplayGainAnalysisAsync(progress, token).ConfigureAwait(false);

            if (folderId is null)
            {
                progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_LibraryRefreshComplete, Percentage = 100 });
                if (changed) _notifier.Raise(LibraryChangeKind.LibraryRescanned);
            }

            if (changed) _ = _artwork.StartMetadataBackgroundFetchAsync();
            return changed;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Library scan ({Kind}) was cancelled.", kind);
            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_ScanCancelled, Percentage = 100 });
            return false;
        }
        catch (Exception ex) when (kind != ScanKind.Forced)
        {
            _logger.LogError(ex, "Library scan ({Kind}) failed.", kind);
            return false;
        }
        finally
        {
            if (kind != ScanKind.Background) _writeLock.ClearActiveScan(operationCts);
        }
    }

    private static IProgress<ScanProgress>? FolderProgress(IProgress<ScanProgress>? progress, int folderIndex, int folderCount,
        string folderName)
    {
        if (progress is null) return null;
        return new Progress<ScanProgress>(p => progress.Report(new ScanProgress
        {
            StatusText = folderCount > 1
                ? string.Format(Resources.Strings.Format_ScanFolderProgress, folderIndex + 1, folderCount, folderName, p.StatusText)
                : p.StatusText,
            Percentage = (folderIndex + p.Percentage / 100.0) / folderCount * 100.0,
            IsIndeterminate = p.IsIndeterminate
        }));
    }

    private async Task<bool> ScanFolderAsync(ScanKind kind, Guid folderId, IProgress<ScanProgress>? progress,
        CancellationToken token)
    {
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ScanFolderCoreAsync(kind, folderId, progress, token), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogCritical(ex, "FATAL: Rescan for folder ID {FolderId} failed.", folderId);
            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_ScanFailed, Percentage = 100 });
            if (kind == ScanKind.Forced) throw;
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <returns>True if the library changed.</returns>
    private async Task<bool> ScanFolderCoreAsync(ScanKind kind, Guid folderId, IProgress<ScanProgress>? progress,
        CancellationToken token)
    {
        Folder? folder;
        await using (var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false))
            folder = await context.Folders.AsNoTracking().OnDisk()
                .FirstOrDefaultAsync(f => f.Id == folderId, token).ConfigureAwait(false);
        if (folder is null)
            return Fail(new InvalidOperationException($"Folder {folderId} isn't a folder on disk in the library."),
                string.Format(Resources.Strings.Format_NotFound, Resources.Strings.Label_Folder));

        _logger.LogInformation("Scanning folder '{FolderName}' (path={FolderPath}, network={IsNetwork}, kind={Kind}).",
            folder.Name, folder.Path, _fileSystem.IsNetworkPath(folder.Path), kind);
        token.ThrowIfCancellationRequested();

        if (!_fileSystem.DirectoryExists(folder.Path))
        {
            // A NAS that isn't mounted yet or a removable drive that isn't inserted must not lose its songs, so only a
            // rescan the user asked for treats a missing folder as gone.
            if (kind != ScanKind.Manual)
                return Fail(new DirectoryNotFoundException($"Library folder was not found: {folder.Path}"), Resources.Strings.Status_ScanFailed);

            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_ScanFailed, Percentage = 100 });
            _logger.LogWarning("Folder path '{FolderPath}' no longer exists. Removing folder {FolderId} from library.",
                folder.Path, folder.Id);
            if (!await _importer.RemoveRootAsync(folderId, token).ConfigureAwait(false)) return false;
            _notifier.Raise(LibraryChangeKind.RootRemoved, folderId);
            return true;
        }

        progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_PreparingScanCaches, IsIndeterminate = true });
        var changes = await AnalyzeFolderChangesAsync(folderId, folder.Path, kind == ScanKind.Forced, token).ConfigureAwait(false);
        if (changes.Added.Count > 0 || changes.Updated.Count > 0 || changes.Removed.Count > 0)
            _logger.LogInformation("Changes detected: +{New} ~{Updated} -{Removed}",
                changes.Added.Count, changes.Updated.Count, changes.Removed.Count);
        token.ThrowIfCancellationRequested();

        var removed = changes.Removed;
        if (removed.Count > 0 && LibraryImporter.IsLikelyMissedListing(removed.Count, changes.ExistingCount, changes.Added.Count))
        {
            _logger.LogError(
                "Scan safety guard tripped for folder {FolderId}: proposed to delete {DeleteCount}/{ExistingCount} songs while only adding {AddCount}. Skipping delete pass; likely a transient enumeration failure (NAS blip / unmounted drive).",
                folderId, removed.Count, changes.ExistingCount, changes.Added.Count);
            removed = [];
        }

        if (removed.Count > 0)
        {
            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_CleaningUpLibrary, IsIndeterminate = true });
            await _importer.DeleteSongsAsync(folderId, removed, token).ConfigureAwait(false);
        }

        // Before the folder image check, so it can replace the images that are gone.
        var clearedArtistImages = await _artwork.ClearMissingArtistImagesAsync(folderId, token).ConfigureAwait(false);

        var filesToRead = changes.Added.Concat(changes.Updated).ToList();
        var import = filesToRead.Count > 0
            ? await ImportFilesAsync(folderId, folder.Path, filesToRead, changes, progress, token).ConfigureAwait(false)
            : new FileImport(0, [], []);
        token.ThrowIfCancellationRequested();

        // Reading a file looks for its lyrics and folder art, so only songs that weren't read need a look for files
        // the user put beside them since.
        var lookForMediaFiles = changes.Untouched > 0 || changes.KeepMediaAssets.Count > 0;
        var lrcTask = lookForMediaFiles ? UpdateMissingLrcPathsAsync(folderId, token) : Task.FromResult(0);
        var coverArtTask = lookForMediaFiles ? UpdateMissingCoverArtAsync(folderId, folder.Path, token) : Task.FromResult(0);
        var artistImageTask = _artwork.UpdateMissingArtistImagesFromFoldersAsync(folderId, token);
        await Task.WhenAll(lrcTask, coverArtTask, artistImageTask,
            EnsureSubFoldersExistAsync(folderId, folder.Path, import.Directories, token)).ConfigureAwait(false);
        // Adding songs to an album leaves it the cover it has.
        var onlyAdded = changes.Added.Count > 0 && changes.Updated.Count == 0 && removed.Count == 0;
        var covers = await _artwork.UpdateAlbumCoversAsync(folderId, import.ReplacedCovers, onlyAdded, token).ConfigureAwait(false);

        if (removed.Count > 0 || changes.Updated.Count > 0)
        {
            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_Finalizing, IsIndeterminate = true });
            await _importer.CleanUpOrphansAsync(token).ConfigureAwait(false);
        }

        var lrcUpdates = lrcTask.Result;
        var coverArtUpdates = coverArtTask.Result + covers.Tracks;
        var artistImageUpdates = artistImageTask.Result;
        progress?.Report(new ScanProgress
        {
            StatusText = ScanSummary(import.SongCount, lrcUpdates, coverArtUpdates, artistImageUpdates),
            Percentage = 100
        });

        var changed = removed.Count > 0 || import.SongCount > 0 || lrcUpdates > 0 || coverArtUpdates > 0 ||
                      artistImageUpdates > 0 || covers.Albums > 0 || clearedArtistImages > 0;
        if (changed) _notifier.Raise(LibraryChangeKind.FolderRescanned, folderId);
        return changed;

        // A forced scan reports every folder that fails; the others skip it.
        bool Fail(Exception error, string status)
        {
            _logger.LogWarning("Cannot scan folder {FolderId}: {Reason}", folderId, error.Message);
            progress?.Report(new ScanProgress { StatusText = status, Percentage = 100 });
            return kind == ScanKind.Forced ? throw error : false;
        }
    }

    private static string ScanSummary(int songsRead, int lrcUpdates, int coverArtUpdates, int artistImageUpdates)
    {
        if (songsRead > 0)
            return FormatCount(songsRead, Resources.Strings.Format_ScanAddedSongs_Singular, Resources.Strings.Format_ScanAddedSongs_Plural);

        var updates = new List<string>();
        if (lrcUpdates > 0)
            updates.Add(FormatCount(lrcUpdates, Resources.Strings.Format_ScanFoundLyrics_Singular, Resources.Strings.Format_ScanFoundLyrics_Plural));
        if (coverArtUpdates > 0)
            updates.Add(FormatCount(coverArtUpdates, Resources.Strings.Format_ScanFoundCoverArt_Singular, Resources.Strings.Format_ScanFoundCoverArt_Plural));
        if (artistImageUpdates > 0)
            updates.Add(FormatCount(artistImageUpdates, Resources.Strings.Format_ScanFoundArtistImages_Singular, Resources.Strings.Format_ScanFoundArtistImages_Plural));

        return updates.Count > 0
            ? string.Join(" ", [Resources.Strings.Status_ScanComplete, .. updates])
            : Resources.Strings.Status_ScanCompleteUpToDate;
    }

    private static string FormatCount(int count, string singular, string plural) =>
        string.Format(count == 1 ? singular : plural, count.ToString("N0"));

    private async Task RunReplayGainAnalysisAsync(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Volume normalization is enabled. Starting ReplayGain analysis.");

        try
        {
            await _replayGainService.ScanLibraryAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("ReplayGain analysis was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReplayGain analysis failed.");
            progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_NormalizationFailed, Percentage = 100 });
        }
    }

    private async Task<List<Folder>> GetRootFoldersAsync(CancellationToken token)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        return await context.Folders.AsNoTracking().OnDisk()
            .Where(f => f.ParentFolderId == null)
            .OrderBy(f => f.Name)
            .ThenBy(f => f.Path)
            .ThenBy(f => f.Id)
            .ToListAsync(token).ConfigureAwait(false);
    }

    private async Task<bool> IsFolderOnDiskAsync(Guid folderId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.Folders.OnDisk().AnyAsync(f => f.Id == folderId, cancellationToken).ConfigureAwait(false);
    }

    /// <param name="Added">Files on disk that aren't in the library.</param>
    /// <param name="Updated">
    ///     Songs to read again: their file changed or their cached cover is missing, or a forced scan reads them all.
    /// </param>
    /// <param name="KeepMediaAssets">
    ///     The updates a forced scan reads although they didn't change on disk, so they keep their art and lyrics.
    /// </param>
    /// <param name="Removed">Songs whose file is gone.</param>
    /// <param name="Untouched">The number of songs that are read neither now nor in the import.</param>
    /// <param name="ExistingCount">The number of songs the folder had before the scan.</param>
    private sealed record FolderChanges(List<string> Added, List<string> Updated, HashSet<string> KeepMediaAssets,
        List<string> Removed, int Untouched, int ExistingCount);

    private async Task<FolderChanges> AnalyzeFolderChangesAsync(Guid folderId, string folderPath, bool readAllFiles,
        CancellationToken cancellationToken)
    {
        await using var analysisContext = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var dbFiles = await analysisContext.Songs
            .AsNoTracking()
            .Where(s => s.FolderId == folderId)
            .Select(s => new { s.FilePath, s.FileModifiedDate, s.AlbumArtUriFromTrack })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Covers are named by their content, so re-reading a song recreates a deleted cover at its old path.
        var missingCovers = dbFiles.Select(s => s.AlbumArtUriFromTrack).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        missingCovers.RemoveWhere(_fileSystem.FileExists);
        if (missingCovers.Count > 0)
            _logger.LogInformation("Re-reading songs for {Count} missing cached covers.", missingCovers.Count);

        var diskFileMap = _fileSystem.EnumerateFilesWithLastWriteTime(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(x => FileExtensions.MusicFileExtensions.Contains(Path.GetExtension(x.Path)))
            .ToDictionary(x => x.Path, x => x.LastWriteTimeUtc, StringComparer.OrdinalIgnoreCase);

        cancellationToken.ThrowIfCancellationRequested();

        var dbFileMap = dbFiles.ToDictionary(s => s.FilePath, StringComparer.OrdinalIgnoreCase);

        var added = new List<string>();
        var updated = new List<string>();
        var keepMediaAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var untouched = 0;
        foreach (var (path, diskModTime) in diskFileMap)
        {
            if (!dbFileMap.TryGetValue(path, out var dbFile))
            {
                added.Add(path);
            }
            else if (diskModTime != dbFile.FileModifiedDate ||
                     (dbFile.AlbumArtUriFromTrack is { } cover && missingCovers.Contains(cover)))
            {
                updated.Add(path);
            }
            else if (readAllFiles)
            {
                updated.Add(path);
                keepMediaAssets.Add(path);
            }
            else
            {
                untouched++;
            }
        }

        var removed = dbFileMap.Keys.Where(path => !diskFileMap.ContainsKey(path)).ToList();
        return new FolderChanges(added, updated, keepMediaAssets, removed, untouched, dbFiles.Count);
    }

    /// <param name="SongCount">The number of songs added or updated.</param>
    /// <param name="Directories">The directories the songs are in.</param>
    /// <param name="ReplacedCovers">Covers the updated songs used before, to delete once unused.</param>
    private sealed record FileImport(int SongCount, HashSet<string> Directories, IReadOnlyCollection<string> ReplacedCovers);

    /// <summary>
    ///     Reads the files in parallel and writes their songs in batches as they come, so writing starts at once and
    ///     only a few batches are held in memory.
    /// </summary>
    private async Task<FileImport> ImportFilesAsync(Guid folderId, string baseFolderPath, List<string> filesToProcess,
        FolderChanges changes, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        const int channelCapacity = 100; // ~2 batches of buffer for backpressure
        var channel = Channel.CreateBounded<SongFileMetadata>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        using var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pipelineToken = pipelineCts.Token;

        var totalFiles = filesToProcess.Count;
        var processedCount = 0;
        const int progressReportingBatchSize = 25;
        var discoveredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_PreparingScanCaches, Percentage = 0 });

        await using var import = await _importer.BeginAsync(folderId, changes.Updated.ToHashSet(StringComparer.OrdinalIgnoreCase),
            changes.KeepMediaAssets.Contains, cancellationToken).ConfigureAwait(false);

        progress?.Report(new ScanProgress { StatusText = Resources.Strings.Status_ReadingSongDetails, Percentage = 0 });

        var totalSaved = 0;
        var isNetworkFolder = _fileSystem.IsNetworkPath(baseFolderPath);
        var failureSummary = new ConcurrentDictionary<ExtractionFailure, int>();
        var producerTask = Task.Run(async () =>
        {
            try
            {
                // Limit concurrent file opens on network storage to reduce transient I/O failures.
                var degreeOfParallelism = isNetworkFolder
                    ? Math.Min(4, Environment.ProcessorCount)
                    : Environment.ProcessorCount;

                await Parallel.ForEachAsync(
                    filesToProcess,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = degreeOfParallelism,
                        CancellationToken = pipelineToken
                    },
                    async (filePath, ct) =>
                    {
                        try
                        {
                            var includeMediaAssets = !changes.KeepMediaAssets.Contains(filePath);
                            var metadata = await _metadataService.ExtractMetadataAsync(filePath, baseFolderPath, includeMediaAssets)
                                .WaitAsync(ct).ConfigureAwait(false);

                            // Timeouts and file-access errors are often a network share stalling for a moment, so they get
                            // one more try after a pause long enough for a brief stall to pass; other failures are permanent.
                            if (metadata.Failure is ExtractionFailure.Timeout or ExtractionFailure.FileAccess)
                            {
                                await Task.Delay(500, ct).ConfigureAwait(false);
                                metadata = await _metadataService.ExtractMetadataAsync(filePath, baseFolderPath, includeMediaAssets)
                                    .WaitAsync(ct).ConfigureAwait(false);
                            }

                            if (metadata.Failure is not { } failure)
                            {
                                await channel.Writer.WriteAsync(metadata, ct).ConfigureAwait(false);
                            }
                            else
                            {
                                failureSummary.AddOrUpdate(failure, 1, (_, n) => n + 1);
                                _logger.LogDebug("Failed to extract metadata ({Failure}) from file: {FilePath}", failure, filePath);
                            }
                        }
                        finally
                        {
                            var currentCount = Interlocked.Increment(ref processedCount);

                            if (currentCount % progressReportingBatchSize == 0 || currentCount == totalFiles)
                            {
                                progress?.Report(new ScanProgress
                                {
                                    StatusText = Resources.Strings.Status_ReadingSongDetails,
                                    Percentage = (currentCount + Volatile.Read(ref totalSaved)) / (double)(totalFiles * 2) * 100,
                                });
                            }
                        }
                    }).ConfigureAwait(false);

                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
                throw;
            }
        }, pipelineToken);

        var consumerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var batch in channel.Reader.ReadAllAsync(pipelineToken).Chunk(50).ConfigureAwait(false))
                {
                    foreach (var metadata in batch)
                    {
                        var dir = Path.GetDirectoryName(metadata.FilePath);
                        if (dir != null) discoveredDirectories.Add(dir);
                    }
                    progress?.Report(new ScanProgress
                    {
                        StatusText = Resources.Strings.Status_ReadingSongDetails,
                        Percentage = (Volatile.Read(ref processedCount) + totalSaved) / (double)(totalFiles * 2) * 100,
                    });

                    await import.WriteBatchAsync(batch, pipelineToken).ConfigureAwait(false);
                    totalSaved += batch.Length;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                pipelineCts.Cancel();
                channel.Writer.TryComplete(ex);
                _logger.LogError(ex, "Consumer failed in streaming metadata extraction for folder {FolderId}", folderId);
                throw;
            }
        }, pipelineToken);

        await Task.WhenAll(producerTask, consumerTask).ConfigureAwait(false);

        if (!failureSummary.IsEmpty)
        {
            var breakdown = string.Join(", ", failureSummary.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"));
            _logger.LogWarning("Metadata extraction completed with {TotalFailures} failures ({Breakdown}). Individual paths logged at Debug level.",
                failureSummary.Values.Sum(), breakdown);
        }

        return new FileImport(totalSaved, discoveredDirectories, import.ReplacedCovers);
    }

    public async Task RefreshOnStartupAsync()
    {
        try
        {
            // Merged first, so the scan doesn't read a folder the library holds twice.
            await DeduplicateLibraryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Library deduplication pass failed during startup.");
        }

        await RefreshAllFoldersAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Collapses library rows that represent the same physical folder or file under different path
    ///     representations (e.g., mapped drive vs UNC), keeping playlist entries and listening history.
    /// </summary>
    /// <returns>The number of duplicate rows removed.</returns>
    internal async Task<int> DeduplicateLibraryAsync(CancellationToken cancellationToken = default)
    {
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _writeLock.ShutdownToken);
        cancellationToken = operationCts.Token;
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int foldersCollapsed, songsCollapsed;
            await using (var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                foldersCollapsed = await CollapseDuplicateFoldersAsync(context, cancellationToken).ConfigureAwait(false);
                songsCollapsed = await CollapseDuplicateSongsAsync(context, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            var removed = foldersCollapsed + songsCollapsed;
            if (removed > 0)
            {
                await _importer.CleanUpOrphansAsync(cancellationToken).ConfigureAwait(false);
                _notifier.Raise(LibraryChangeKind.LibraryRescanned);
                _logger.LogInformation("Library deduplication merged {FolderDupes} folder duplicates and {SongDupes} song duplicates.",
                    foldersCollapsed, songsCollapsed);
            }

            return removed;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<int> CollapseDuplicateFoldersAsync(MusicDbContext context, CancellationToken ct)
    {
        var folders = await context.Folders.AsNoTracking().OnDisk().ToListAsync(ct).ConfigureAwait(false);
        var groups = folders
            .GroupBy(f => _fileSystem.NormalizePath(f.Path), StringComparer.OrdinalIgnoreCase);

        var removed = 0;
        foreach (var group in groups)
        {
            var survivor = group
                .OrderBy(f => f.ParentFolderId.HasValue)
                .ThenBy(f => f.LastModifiedDate ?? DateTime.MaxValue)
                .ThenBy(f => f.Id)
                .First();

            foreach (var dup in group.Where(f => f.Id != survivor.Id))
            {
                await context.Folders
                    .Where(f => f.ParentFolderId == dup.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(f => f.ParentFolderId, survivor.Id), ct)
                    .ConfigureAwait(false);

                await context.Songs
                    .Where(s => s.FolderId == dup.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.FolderId, survivor.Id), ct)
                    .ConfigureAwait(false);

                await context.ListenHistory
                    .Where(history => history.ContextType == PlaybackContextType.Folder && history.ContextId == dup.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(history => history.ContextId, survivor.Id), ct)
                    .ConfigureAwait(false);

                removed += await context.Folders.Where(f => f.Id == dup.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }

            // Remove aliases before renaming: the canonical spelling may belong to a deleted row.
            if (survivor.Path != group.Key)
                await context.Folders.Where(f => f.Id == survivor.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(f => f.Path, group.Key), ct).ConfigureAwait(false);
        }

        return removed;
    }

    private async Task<int> CollapseDuplicateSongsAsync(MusicDbContext context, CancellationToken ct)
    {
        var songs = await context.Songs
            .AsNoTracking()
            .OnDisk()
            .Select(s => new
            {
                s.Id,
                s.FilePath,
                s.PlayCount,
                s.SkipCount,
                s.TotalListenTimeTicks,
                s.IsLoved,
                s.Rating,
                s.DateAddedToLibrary,
                s.LastPlayedDate
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var removed = 0;
        foreach (var group in songs.GroupBy(s => _fileSystem.NormalizePath(s.FilePath), StringComparer.OrdinalIgnoreCase))
        {
            var survivor = group
                .OrderByDescending(s => s.PlayCount)
                .ThenByDescending(s => s.IsLoved)
                .ThenByDescending(s => s.DateAddedToLibrary ?? DateTime.MinValue)
                .First();
            var duplicateIds = group.Where(s => s.Id != survivor.Id).Select(s => s.Id).ToArray();
            if (duplicateIds.Length > 0)
            {
                var ids = group.Select(s => s.Id).ToArray();
                var playlistEntries = await context.PlaylistSongs.AsNoTracking()
                    .Where(entry => ids.Contains(entry.SongId)).ToListAsync(ct).ConfigureAwait(false);
                await context.PlaylistSongs.Where(entry => ids.Contains(entry.SongId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                context.PlaylistSongs.AddRange(playlistEntries.GroupBy(entry => entry.PlaylistId)
                    .Select(playlist => new PlaylistSong
                    {
                        PlaylistId = playlist.Key,
                        SongId = survivor.Id,
                        Order = playlist.Min(entry => entry.Order)
                    }));
                await context.SaveChangesAsync(ct).ConfigureAwait(false);
                context.ChangeTracker.Clear();

                await context.ListenHistory.Where(history => duplicateIds.Contains(history.SongId))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(history => history.SongId, survivor.Id), ct)
                    .ConfigureAwait(false);
                removed += await context.Songs.Where(s => duplicateIds.Contains(s.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }

            if (duplicateIds.Length > 0 || survivor.FilePath != group.Key)
            {
                var directory = Path.GetDirectoryName(group.Key) ?? string.Empty;
                var playCount = group.Sum(s => s.PlayCount);
                var skipCount = group.Sum(s => s.SkipCount);
                var listenTicks = group.Sum(s => s.TotalListenTimeTicks);
                var loved = group.Any(s => s.IsLoved);
                var rating = group.Max(s => s.Rating);
                var added = group.Min(s => s.DateAddedToLibrary);
                var lastPlayed = group.Max(s => s.LastPlayedDate);
                await context.Songs.Where(s => s.Id == survivor.Id).ExecuteUpdateAsync(setters => setters
                    .SetProperty(s => s.FilePath, group.Key)
                    .SetProperty(s => s.DirectoryPath, directory)
                    .SetProperty(s => s.PlayCount, playCount)
                    .SetProperty(s => s.SkipCount, skipCount)
                    .SetProperty(s => s.TotalListenTimeTicks, listenTicks)
                    .SetProperty(s => s.IsLoved, loved)
                    .SetProperty(s => s.Rating, rating)
                    .SetProperty(s => s.DateAddedToLibrary, added)
                    .SetProperty(s => s.LastPlayedDate, lastPlayed), ct).ConfigureAwait(false);
            }
        }

        return removed;
    }

    private async Task EnsureSubFoldersExistAsync(Guid rootFolderId, string rootFolderPath,
        HashSet<string> discoveredDirectories, CancellationToken cancellationToken)
    {
        if (discoveredDirectories.Count == 0) return;

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var folders = await context.Folders.ToListAsync(cancellationToken).ConfigureAwait(false);
        var foldersByPath = folders.ToDictionary(f => PathCanonicalizer.NormalizeDirectoryPath(f.Path), StringComparer.OrdinalIgnoreCase);
        var normalizedRootPath = PathCanonicalizer.NormalizeDirectoryPath(rootFolderPath);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Include empty intermediate directories so the browser can reach nested albums.
        foreach (var directory in discoveredDirectories)
        {
            var currentPath = PathCanonicalizer.NormalizeDirectoryPath(directory);
            while (!string.IsNullOrEmpty(currentPath) &&
                   !currentPath.Equals(normalizedRootPath, StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(currentPath);
                var parent = Path.GetDirectoryName(currentPath);
                if (string.IsNullOrEmpty(parent)) break;
                currentPath = PathCanonicalizer.NormalizeDirectoryPath(parent);
            }
        }

        // Parents are shorter than their descendants and must be inserted first.
        foreach (var path in paths.OrderBy(p => p.Length))
        {
            var parentPath = Path.GetDirectoryName(path);
            var parentId = !string.IsNullOrEmpty(parentPath) &&
                           foldersByPath.TryGetValue(PathCanonicalizer.NormalizeDirectoryPath(parentPath), out var parent)
                ? parent.Id
                : rootFolderId;

            if (foldersByPath.TryGetValue(path, out var existing))
            {
                existing.ParentFolderId = parentId;
                continue;
            }

            var folder = new Folder
            {
                Name = Path.GetFileName(path),
                Path = path,
                ParentFolderId = parentId,
                LastModifiedDate = DateTime.UtcNow
            };
            context.Folders.Add(folder);
            foldersByPath.Add(path, folder);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Gives songs without lyrics the .lrc or .txt file the user put beside them since the song was read.
    /// </summary>
    /// <returns>The number of songs updated.</returns>
    private async Task<int> UpdateMissingLrcPathsAsync(Guid folderId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var songsWithoutLrc = await context.Songs
            .Where(s => s.FolderId == folderId && s.LrcFilePath == null)
            .Select(s => new { s.Id, s.FilePath })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var finder = new MediaFileFinder(_fileSystem, _logger);
        var lrcPaths = new Dictionary<Guid, string>();
        foreach (var song in songsWithoutLrc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (finder.FindSidecarLyrics(song.FilePath) is { } lrcPath) lrcPaths[song.Id] = lrcPath;
        }

        var updated = await UpdateSongsAsync(context, lrcPaths, (song, lrcPath) => song.LrcFilePath = lrcPath, cancellationToken)
            .ConfigureAwait(false);
        if (updated > 0) _logger.LogInformation("Updated {Count} songs with newly discovered LRC files.", updated);
        return updated;
    }

    /// <summary>
    ///     Gives songs without art the cover art file (cover.jpg, folder.png and the like) the user put in their
    ///     directory or above it since the song was read.
    /// </summary>
    /// <returns>The number of songs updated.</returns>
    private async Task<int> UpdateMissingCoverArtAsync(Guid folderId, string baseFolderPath, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var songsWithoutCoverArt = await context.Songs
            .Where(s => s.FolderId == folderId && s.AlbumArtUriFromTrack == null)
            .Select(s => new { s.Id, s.FilePath })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var finder = new MediaFileFinder(_fileSystem, _logger);
        // Songs in one directory share their art, which is read and stored once.
        var savedArt = new Dictionary<string, (string? Uri, string? LightSwatchId, string? DarkSwatchId)>(StringComparer.OrdinalIgnoreCase);
        var covers = new Dictionary<Guid, (string? Uri, string? LightSwatchId, string? DarkSwatchId)>();
        foreach (var song in songsWithoutCoverArt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (finder.FindCoverArt(song.FilePath, baseFolderPath) is not { } coverArtPath) continue;

            if (!savedArt.TryGetValue(coverArtPath, out var cover))
            {
                cover = await SaveCoverArtAsync(coverArtPath).ConfigureAwait(false);
                savedArt.Add(coverArtPath, cover);
            }

            if (!string.IsNullOrEmpty(cover.Uri)) covers[song.Id] = cover;
        }

        var updated = await UpdateSongsAsync(context, covers, (song, cover) =>
        {
            song.AlbumArtUriFromTrack = cover.Uri;
            song.LightSwatchId = cover.LightSwatchId;
            song.DarkSwatchId = cover.DarkSwatchId;
        }, cancellationToken).ConfigureAwait(false);
        if (updated > 0) _logger.LogInformation("Updated {Count} songs with newly discovered cover art files.", updated);
        return updated;
    }

    private async Task<(string? Uri, string? LightSwatchId, string? DarkSwatchId)> SaveCoverArtAsync(string coverArtPath)
    {
        try
        {
            var imageBytes = await _fileSystem.ReadAllBytesAsync(coverArtPath).ConfigureAwait(false);
            if (imageBytes.Length > 0) return await _imageProcessor.SaveCoverArtAndExtractColorsAsync(imageBytes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process cover art file {CoverArtPath}.", coverArtPath);
        }

        return default;
    }

    private static async Task<int> UpdateSongsAsync<T>(MusicDbContext context, Dictionary<Guid, T> values,
        Action<Song, T> update, CancellationToken cancellationToken)
    {
        var updated = 0;
        foreach (var ids in values.Keys.Chunk(MusicDbContext.MaxParametersPerQuery))
        {
            var songs = await context.Songs.Where(s => ids.Contains(s.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var song in songs) update(song, values[song.Id]);
            updated += songs.Count;
        }

        if (updated > 0) await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }
}
