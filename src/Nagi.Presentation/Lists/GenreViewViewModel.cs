using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Nagi.Core.Library;
using Nagi.Core.Listening;
using Nagi.Core.Platform;
using Nagi.Core.Playback;
using Nagi.Core.Playlists;
using Nagi.Core.Settings;
using Nagi.Presentation.Common;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Settings;

namespace Nagi.Presentation.Lists;

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

    protected override SongScope? Scope => _genreId != Guid.Empty ? new SongScope.Genre(_genreId) : null;

    protected override Task<bool> OpenDetailsAsync(GenreViewNavigationParameter parameter, CancellationToken token)
    {
        _genreId = parameter.GenreId;
        PageTitle = parameter.GenreName;
        return Task.FromResult(true);
    }
}
