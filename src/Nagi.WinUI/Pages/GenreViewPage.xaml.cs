using Microsoft.Extensions.DependencyInjection;
using Nagi.Presentation.Resources;
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
        InitializeSongList(ViewModel, SearchToggleButton, SearchTextBox,
            Strings.GenreViewPage_SearchButton_Search_ToolTip, ColumnHeaderHost, SongsListView);
    }

    public GenreViewViewModel ViewModel { get; }

}
