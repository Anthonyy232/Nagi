using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Implementations;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class AlbumViewPage : SongListPage
{

    public AlbumViewPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<ViewModelFactory>().Create<AlbumViewViewModel>();
        DataContext = ViewModel;
        InitializeSongList(ViewModel, ownsViewModel: true, SearchToggleButton, SearchTextBox,
            Strings.AlbumViewPage_SearchButton_Search_ToolTip, ColumnHeaderHost, SongsListView, GroupedSongsListView);
    }

    public AlbumViewViewModel ViewModel { get; }

    // Multi-disc albums show the songs in a list grouped under disc headers.
    protected override ListView ActiveSongList => ViewModel.IsGroupedByDisc ? GroupedSongsListView : SongsListView;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var navParam = (AlbumViewNavigationParameter)e.Parameter;
        await ViewModel.LoadAlbumDetailsAsync(navParam.AlbumId);
    }
}
