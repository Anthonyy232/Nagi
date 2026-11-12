using Microsoft.UI.Xaml.Controls;
using Nagi.Core.Library;
using Nagi.Presentation.Lists;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Pages;

public sealed partial class FolderSongViewPage : SongListPage
{
    public FolderSongViewPage()
    {
        InitializeComponent();
        ViewModel = GetViewModel<FolderSongListViewModel>();
        InitializeSongList(ViewModel, ColumnHeaderHost, FolderContentsListView);
    }

    public FolderSongListViewModel ViewModel { get; }

    public string EmptyGlyph(bool isServer) => isServer ? "\uE8CE" : "\uE8B7";

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
