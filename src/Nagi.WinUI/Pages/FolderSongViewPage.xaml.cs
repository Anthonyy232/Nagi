using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Core.Models;
using Nagi.Presentation.Models;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.Presentation.ViewModels;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Pages;

public sealed partial class FolderSongViewPage : SongListPage
{

    public FolderSongViewPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<IViewModelFactory>().Create<FolderSongListViewModel>();
        DataContext = ViewModel;
        InitializeSongList(ViewModel, ownsViewModel: true, SearchToggleButton, SearchTextBox,
            Strings.FolderSongViewPage_SearchButton_Search_ToolTip, FolderContentsListView);
    }

    public FolderSongListViewModel ViewModel { get; }

    // Rows are subfolders or songs.
    protected override Song? GetSong(object? item) => (item as FolderContentItem)?.Song;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var navParam = (FolderSongViewNavigationParameter)e.Parameter;
        await ViewModel.InitializeAsync(navParam.Title, navParam.FolderId, navParam.DirectoryPath);
        await ViewModel.LoadAvailablePlaylistsAsync();
    }

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

        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name == "FolderAddToPlaylistSubMenu") is { } addToPlaylistSubMenu)
            PlaylistMenuFlyoutHelper.Populate(addToPlaylistSubMenu, ViewModel.AvailablePlaylists,
                ViewModel.AddSelectedSongsToPlaylistCommand);
    }
}
