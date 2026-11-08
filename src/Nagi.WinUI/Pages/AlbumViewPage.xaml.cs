using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Lists;

namespace Nagi.WinUI.Pages;

public sealed partial class AlbumViewPage : SongListPage
{
    public AlbumViewPage()
    {
        InitializeComponent();
        ViewModel = GetViewModel<AlbumViewViewModel>();
        DataContext = ViewModel;
        InitializeSongList(ViewModel, ColumnHeaderHost, SongsListView, GroupedSongsListView);
    }

    public AlbumViewViewModel ViewModel { get; }

    // Multi-disc albums show the songs in a list grouped under disc headers.
    protected override ListView ActiveSongList => ViewModel.IsGroupedByDisc ? GroupedSongsListView : SongsListView;
}
