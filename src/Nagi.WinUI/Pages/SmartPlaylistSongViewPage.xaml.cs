using Microsoft.Extensions.DependencyInjection;
using Nagi.Presentation.Lists;
using Nagi.Presentation.Services.Implementations;

namespace Nagi.WinUI.Pages;

public sealed partial class SmartPlaylistSongViewPage : SongListPage
{

    public SmartPlaylistSongViewPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<ViewModelFactory>().Create<SmartPlaylistSongListViewModel>();
        DataContext = ViewModel;
        InitializeSongList(ViewModel, ColumnHeaderHost, SongsListView);
    }

    public SmartPlaylistSongListViewModel ViewModel { get; }

}
