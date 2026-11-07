using Microsoft.Extensions.DependencyInjection;
using Nagi.Presentation.Services.Implementations;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class GenreViewPage : SongListPage
{

    public GenreViewPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<ViewModelFactory>().Create<GenreViewViewModel>();
        DataContext = ViewModel;
        InitializeSongList(ViewModel, ColumnHeaderHost, SongsListView);
    }

    public GenreViewViewModel ViewModel { get; }

}
