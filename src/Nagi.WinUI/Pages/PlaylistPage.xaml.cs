using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Lists;

namespace Nagi.WinUI.Pages;

public sealed partial class PlaylistPage : ViewModelPage
{
    public PlaylistPage()
    {
        InitializeComponent();
        ViewModel = GetViewModel<PlaylistViewModel>();
        DataContext = ViewModel;
    }

    public PlaylistViewModel ViewModel { get; }

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

    private void PlaylistsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistViewModelItem clickedPlaylist) ViewModel.OpenPlaylist(clickedPlaylist);
    }
}
