using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.Services;

namespace Nagi.Presentation.ViewModels;

public partial class GenreViewViewModel : DetailSongListViewModelBase<GenreViewNavigationParameter>
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
        PageTitle = Strings.GenreView_DefaultName;
    }

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

    protected override Task<bool> OpenDetailsAsync(GenreViewNavigationParameter parameter, CancellationToken token)
    {
        _genreId = parameter.GenreId;
        PageTitle = parameter.GenreName;
        return Task.FromResult(true);
    }

    protected override PlaybackContext GetPlaybackContext() =>
        _genreId != Guid.Empty ? new(PlaybackContextType.Genre, _genreId) : base.GetPlaybackContext();

}
