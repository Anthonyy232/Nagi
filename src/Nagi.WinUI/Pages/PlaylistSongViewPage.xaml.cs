using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Core.Models;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace Nagi.WinUI.Pages;

public sealed partial class PlaylistSongViewPage : SongListPage
{

    public PlaylistSongViewPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<IViewModelFactory>().Create<PlaylistSongListViewModel>();
        DataContext = ViewModel;
        InitializeSongList(ViewModel, ownsViewModel: true, SearchToggleButton, SearchTextBox,
            Strings.PlaylistSongViewPage_SearchButton_Search_ToolTip, SongsListView);
    }

    public PlaylistSongListViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var navParam = (PlaylistSongViewNavigationParameter)e.Parameter;
        await ViewModel.InitializeAsync(navParam.Title, navParam.PlaylistId, navParam.CoverImageUri);
        await ViewModel.LoadAvailablePlaylistsAsync();
    }

    private void SongsListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        // A reorder raises Remove and Add rather than Move, then DragItemsCompleted once with the moved items.
        if (args.DropResult != DataPackageOperation.Move) return;
        _ = ViewModel.OnSongsReorderedAsync(args.Items.OfType<Song>().ToList());
    }
}
