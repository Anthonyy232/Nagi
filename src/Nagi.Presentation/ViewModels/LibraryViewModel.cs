using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Listening;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Playlists;
using Nagi.Core.Settings;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Services;

namespace Nagi.Presentation.ViewModels;

public partial class LibraryViewModel : SongListViewModelBase
{

    public LibraryViewModel(
        ILibraryReader libraryReader,
        IListeningService listening,
        ILibraryNotifier libraryNotifier,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        ISettingsService settingsService,
        IUIService uiService,
        ILogger<LibraryViewModel> logger)
        : base(libraryReader, listening, libraryNotifier, playlistService, playbackService, musicNavigationService, dispatcherService, settingsService, uiService, logger, CommonSortOrders, AppSettings.LibrarySortOrder)
    {
        PageTitle = Strings.LibraryPage_Title;
        _settingsService.SettingChanged += OnSettingChanged;
    }

    private void OnSettingChanged(Setting setting)
    {
        if (setting != CoreSettings.IgnoreLeadingArticlesOnSort) return;
        _dispatcherService.TryEnqueue(() =>
        {
            CurrentPage = 1;
            _ = LoadAsync(CancellationToken.None);
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
        await LoadAsync(CancellationToken.None);
        HasInitialized = !HasLoadError;
    }

    protected override bool ShouldReloadOnLibraryChange(LibraryChangeKind kind) =>
        kind != LibraryChangeKind.RootAdded;

}
