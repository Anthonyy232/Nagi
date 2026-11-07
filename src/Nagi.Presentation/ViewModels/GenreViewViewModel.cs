using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Core.Services.Data;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Services;
using Nagi.Core.Settings;

namespace Nagi.Presentation.ViewModels;

public partial class GenreViewViewModel : SongListViewModelBase
{
    private Guid _genreId;

    public GenreViewViewModel(
        ILibraryReader libraryReader,
        IListeningService listening,
        ILibraryNotifier libraryNotifier,
        IPlaylistService playlistService,
        IMusicPlaybackService playbackService,
        IMusicNavigationService musicNavigationService,
        IDispatcherService dispatcherService,
        ISettingsService settingsService,
        IUIService uiService,
        ILogger<GenreViewViewModel> logger)
        : base(libraryReader, listening, libraryNotifier, playlistService, playbackService, musicNavigationService, dispatcherService, settingsService, uiService, logger, CommonSortOrders, AppSettings.GenreViewSortOrder)
    {
        GenreName = Strings.GenreView_DefaultName;
    }

    [ObservableProperty] public partial string GenreName { get; set; }

    protected override Task<PagedResult<Song>> LoadSongsPagedAsync(int pageNumber, int pageSize,
        SongSortOrder sortOrder, CancellationToken cancellationToken = default)
    {
        if (_genreId == Guid.Empty) return Task.FromResult(new PagedResult<Song>());

        return _libraryReader.GetSongsAsync(new SongScope.Genre(_genreId), SearchTerm, sortOrder, pageNumber, pageSize,
            cancellationToken);
    }

    protected override Task<List<Guid>> LoadAllSongIdsAsync(SongSortOrder sortOrder, CancellationToken token = default)
    {
        if (_genreId == Guid.Empty) return Task.FromResult(new List<Guid>());

        return _libraryReader.GetSongIdsAsync(new SongScope.Genre(_genreId), SearchTerm, sortOrder, token);
    }

    public async Task LoadGenreDetailsAsync(GenreViewNavigationParameter? navParam)
    {
        if (_isDisposed || navParam is null) return;
        ResetState();

        _logger.LogDebug("Loading details for genre '{GenreName}' ({GenreId})", navParam.GenreName,
            navParam.GenreId);

        _genreId = navParam.GenreId;
        GenreName = navParam.GenreName;
        PageTitle = navParam.GenreName;

        await LoadAsync(CancellationToken.None);
    }

    protected override PlaybackContext GetPlaybackContext() =>
        _genreId != Guid.Empty ? new(PlaybackContextType.Genre, _genreId) : base.GetPlaybackContext();

}
