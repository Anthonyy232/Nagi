using Microsoft.Extensions.DependencyInjection;
using Nagi.Presentation.Services.Implementations;
using Nagi.Presentation.ViewModels;

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
