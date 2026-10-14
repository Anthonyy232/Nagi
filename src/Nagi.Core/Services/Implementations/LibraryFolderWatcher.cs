using Microsoft.Extensions.Logging;
using Nagi.Core.Constants;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Implementations;

public sealed class LibraryFolderWatcher : ILibraryFolderWatcher, IDisposable
{
    private readonly ILibraryReader _libraryReader;
    private readonly ILibraryScanner _libraryScanner;
    private readonly ILogger<LibraryFolderWatcher> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly CancellationToken _shutdownToken;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, WatchedFolder> _folders = new();

    public LibraryFolderWatcher(ILibraryReader libraryReader, ILibraryScanner libraryScanner,
        ILogger<LibraryFolderWatcher> logger)
    {
        _libraryReader = libraryReader;
        _libraryScanner = libraryScanner;
        _logger = logger;
        _shutdownToken = _shutdownCts.Token;
    }

    /// <summary>
    ///     How long the folder must stay quiet before it is refreshed, so a large copy triggers one scan.
    /// </summary>
    internal TimeSpan QuietPeriod { get; init; } = TimeSpan.FromSeconds(3);

    public void Start()
    {
        _libraryScanner.LibraryContentChanged += OnLibraryContentChanged;
        _ = SyncFoldersAsync();
    }

    public void Dispose()
    {
        if (_shutdownToken.IsCancellationRequested) return;
        _libraryScanner.LibraryContentChanged -= OnLibraryContentChanged;
        _shutdownCts.Cancel();
        lock (_lock)
        {
            foreach (var folder in _folders.Values) folder.Dispose();
            _folders.Clear();
        }
        _shutdownCts.Dispose();
    }

    private void OnLibraryContentChanged(object? sender, LibraryContentChangedEventArgs e)
    {
        if (e.ChangeType is LibraryChangeType.FolderAdded or LibraryChangeType.FolderRemoved)
            _ = SyncFoldersAsync();
    }

    private async Task SyncFoldersAsync()
    {
        try
        {
            var roots = (await _libraryReader.GetRootFoldersWithCountsAsync(_shutdownToken).ConfigureAwait(false))
                .Select(summary => summary.Folder)
                .ToList();

            lock (_lock)
            {
                if (_shutdownToken.IsCancellationRequested) return;

                foreach (var id in _folders.Keys.Except(roots.Select(f => f.Id)).ToList())
                {
                    _folders[id].Dispose();
                    _folders.Remove(id);
                }

                foreach (var folder in roots.Where(f => !_folders.ContainsKey(f.Id)))
                {
                    var watched = TryWatch(folder.Id, folder.Path);
                    if (watched is not null) _folders[folder.Id] = watched;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update the watched library folders.");
        }
    }

    private WatchedFolder? TryWatch(Guid folderId, string path)
    {
        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024
            };
            var watched = new WatchedFolder(watcher, new Timer(_ => _ = RefreshAsync(folderId)));

            watcher.Created += (_, e) => OnFileSystemEvent(watched, e.FullPath, includeFolders: true);
            watcher.Deleted += (_, e) => OnFileSystemEvent(watched, e.FullPath, includeFolders: true);
            watcher.Changed += (_, e) => OnFileSystemEvent(watched, e.FullPath, includeFolders: false);
            watcher.Renamed += (_, e) =>
            {
                OnFileSystemEvent(watched, e.OldFullPath, includeFolders: true);
                OnFileSystemEvent(watched, e.FullPath, includeFolders: true);
            };
            // A buffer overflow means events were lost, so refresh to catch up.
            watcher.Error += (_, e) =>
            {
                _logger.LogWarning(e.GetException(), "Watcher for library folder {FolderPath} reported an error.", path);
                watched.ScheduleRefresh(QuietPeriod);
            };

            watcher.EnableRaisingEvents = true;
            _logger.LogDebug("Watching library folder {FolderPath} for changes.", path);
            return watched;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // An unreachable drive or share is picked up again by the next full refresh.
            _logger.LogWarning(ex, "Cannot watch library folder {FolderPath} for changes.", path);
            return null;
        }
    }

    private void OnFileSystemEvent(WatchedFolder folder, string path, bool includeFolders)
    {
        var extension = Path.GetExtension(path);
        // Folder events have no extension; a moved or deleted folder can take songs with it.
        if (FileExtensions.MusicFileExtensions.Contains(extension) || (includeFolders && extension.Length == 0))
            folder.ScheduleRefresh(QuietPeriod);
    }

    private async Task RefreshAsync(Guid folderId)
    {
        try
        {
            _logger.LogDebug("Refreshing library folder {FolderId} after file changes.", folderId);
            await _libraryScanner.RefreshFolderInBackgroundAsync(folderId, _shutdownToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException) when (_shutdownToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background refresh of library folder {FolderId} failed.", folderId);
        }
    }

    private sealed class WatchedFolder(FileSystemWatcher watcher, Timer timer) : IDisposable
    {
        public void ScheduleRefresh(TimeSpan quietPeriod)
        {
            try
            {
                timer.Change(quietPeriod, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            watcher.Dispose();
            timer.Dispose();
        }
    }
}
