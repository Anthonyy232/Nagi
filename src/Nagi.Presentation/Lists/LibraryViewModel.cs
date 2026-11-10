using System.Collections.Generic;
using System.Threading.Tasks;
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
    }

    protected override SongScope Scope => ShowFavoritesOnly ? SongScope.Favorites : SongScope.Library;
}
