using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Services;
using Nagi.Core.Settings;

namespace Nagi.Presentation.ViewModels;

public partial class LibraryViewModel : SongListViewModelBase
{
    private readonly ILibraryFolderService _libraryFolders;
    private bool _isInitialScanTriggered;

    public LibraryViewModel(
        ILibraryReader libraryReader,
        IListeningService listening,
        ILibraryNotifier libraryNotifier,
        ILibraryFolderService libraryFolders,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        ISettingsService settingsService,
        IUIService uiService,
        ILogger<LibraryViewModel> logger)
        : base(libraryReader, listening, libraryNotifier, playlistService, playbackService, musicNavigationService, dispatcherService, settingsService, uiService, logger)
    {
        _libraryFolders = libraryFolders;
        PageTitle = Strings.LibraryPage_Title;
        _settingsService.SettingChanged += OnSettingChanged;
    }

    private void OnSettingChanged(Setting setting)
    {
        if (setting != CoreSettings.IgnoreLeadingArticlesOnSort) return;
        _dispatcherService.TryEnqueue(() =>
        {
            CurrentPage = 1;
            _ = RefreshOrSortSongsAsync();
        });
    }

    public override void Dispose()
    {
        _settingsService.SettingChanged -= OnSettingChanged;
        base.Dispose();
    }

    protected override Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken cancellationToken = default) =>
        _libraryReader.GetSongsAsync(Scope, SearchTerm, sortOrder, pageNumber, pageSize, cancellationToken);

    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default) =>
        _libraryReader.GetSongIdsAsync(Scope, SearchTerm, sortOrder, token);

    private SongScope Scope => ShowFavoritesOnly ? SongScope.Favorites : SongScope.Library;

    protected override PlaybackContext GetPlaybackContext() =>
        IsSearchActive ? new(PlaybackContextType.Search, null) : new(PlaybackContextType.Library, null);

    public bool HasInitialized { get; private set; }

    public async Task InitializeAsync()
    {
        _logger.LogDebug("Initializing LibraryViewModel");
        CurrentSortOrder = _settingsService.Get(AppSettings.LibrarySortOrder);

        await RefreshOrSortSongsCommand.ExecuteAsync(null);
        HasInitialized = !HasLoadError;

        if (_isInitialScanTriggered) return;
        _isInitialScanTriggered = true;

        _logger.LogDebug("Starting initial background library refresh");
        // The library change refreshes the view when the background scan finishes.
        _ = Task.Run(async () =>
        {
            try
            {
                // Run the dedup pass before refresh so the UI doesn't briefly show duplicate
                // entries from a prior (pre-canonicalization) run.
                await _libraryFolders.DeduplicateLibraryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Library deduplication pass failed during startup.");
            }
            try
            {
                await _libraryFolders.RefreshAllFoldersAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Initial background library refresh failed.");
            }
        });
    }

    protected override bool ShouldReloadOnLibraryChange(LibraryChangeKind kind) =>
        kind != LibraryChangeKind.RootAdded;

    protected override Task RefreshAsync(CancellationToken cancellationToken) =>
        RefreshOrSortSongsAsync(manualToken: cancellationToken);

    protected override Task SaveSortOrderAsync(SongSortOrder sortOrder)
    {
        _settingsService.Set(AppSettings.LibrarySortOrder, sortOrder);
        return Task.CompletedTask;
    }

}
