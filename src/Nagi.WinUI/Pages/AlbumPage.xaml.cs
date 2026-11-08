using System.Threading;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Lists;

namespace Nagi.WinUI.Pages;

public sealed partial class AlbumPage : ViewModelPage
{
    public AlbumPage()
    {
        InitializeComponent();
        ViewModel = GetViewModel<AlbumViewModel>();
        DataContext = ViewModel;
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
        ViewModel.ResetState();
    }

    private void AlbumsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AlbumViewModelItem clickedAlbum) ViewModel.OpenAlbum(clickedAlbum);
    }
}
