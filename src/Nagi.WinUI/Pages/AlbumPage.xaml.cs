using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class AlbumPage : SearchablePage
{
    public AlbumPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<AlbumViewModel>();
        DataContext = ViewModel;
        InitializeSearch(ViewModel, SearchToggleButton, SearchTextBox, Strings.AlbumPage_SearchButton_Search_ToolTip);
    }

    public AlbumViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync(CancellationToken.None);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // The singleton view model outlives the page, so it is reset rather than disposed.
        ViewModel.ResetState();
    }

    private void AlbumsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumViewModelItem clickedAlbum)
        {
            _ = ViewModel.NavigateToAlbumDetailAsync(clickedAlbum);
        }
    }
}
