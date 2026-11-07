using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nagi.Core.Library;
using Nagi.Presentation.Lists;
using Nagi.Presentation;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Pages;

public sealed partial class FolderSongViewPage : SongListPage
{

    public FolderSongViewPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<ViewModelFactory>().Create<FolderSongListViewModel>();
        DataContext = ViewModel;
        InitializeSongList(ViewModel, ColumnHeaderHost, FolderContentsListView);
    }

    public FolderSongListViewModel ViewModel { get; }

    public string EmptyGlyph(bool isServer) => isServer ? "" : "";

    // Rows are subfolders or songs.
    protected override Song? GetSong(object? item) => (item as FolderContentItem)?.Song;


    private void OnBreadcrumbItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is BreadcrumbItem breadcrumb)
            ViewModel.NavigateToBreadcrumbCommand.Execute(breadcrumb);
    }

    private async void FolderContentsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FolderContentItem { Folder: { } folder })
            await ViewModel.NavigateToSubfolderCommand.ExecuteAsync(folder);
    }

    private async void FolderContentsListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: FolderContentItem contentItem }) return;
        if (contentItem.Folder is { } folder)
            await ViewModel.NavigateToSubfolderCommand.ExecuteAsync(folder);
        else
            SongsListView_DoubleTapped(sender, e);
    }

    private void FolderItemMenuFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menuFlyout) return;

        if (menuFlyout.Target?.DataContext is FolderContentItem { IsFolder: true } contentItem
            && !FolderContentsListView.SelectedItems.Contains(contentItem))
            FolderContentsListView.SelectedItem = contentItem;

        // The folder menu's one submenu is Add to playlist.
        PlaylistMenuFlyoutHelper.Populate(menuFlyout.Items.OfType<MenuFlyoutSubItem>().Single(), ViewModel.AvailablePlaylists,
            ViewModel.AddSelectedSongsToPlaylistCommand);
    }
}
