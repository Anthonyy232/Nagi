using Nagi.Presentation.Lists;

namespace Nagi.WinUI.Pages;

public sealed partial class GenreViewPage : SongListPage
{
    public GenreViewPage()
    {
        InitializeComponent();
        ViewModel = GetViewModel<GenreViewViewModel>();
        InitializeSongList(ViewModel, ColumnHeaderHost, SongsListView);
    }

    public GenreViewViewModel ViewModel { get; }
}
