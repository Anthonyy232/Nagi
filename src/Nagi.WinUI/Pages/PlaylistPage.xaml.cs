using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Pages;

public sealed partial class PlaylistPage : SearchablePage
{
    public PlaylistPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<PlaylistViewModel>();
        DataContext = ViewModel;
        InitializeSearch(ViewModel, SearchToggleButton, SearchTextBox, Strings.PlaylistPage_SearchButton_Search_ToolTip);
    }

    public PlaylistViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadPlaylistsCommand.ExecuteAsync(null);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.ResetState();
    }

    private void PlaylistsGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistViewModelItem clickedPlaylist)
        {
            ViewModel.NavigateToPlaylistDetail(clickedPlaylist);
        }
    }
}
