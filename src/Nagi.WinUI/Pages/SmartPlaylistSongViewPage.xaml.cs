using Nagi.Presentation.Lists;

namespace Nagi.WinUI.Pages;

public sealed partial class SmartPlaylistSongViewPage : SongListPage
{
    public SmartPlaylistSongViewPage()
    {
        InitializeComponent();
        ViewModel = GetViewModel<SmartPlaylistSongListViewModel>();
        DataContext = ViewModel;
        InitializeSongList(ViewModel, ColumnHeaderHost, SongsListView);
    }

    public SmartPlaylistSongListViewModel ViewModel { get; }
}
