using System;
using System.Linq;
using Windows.System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Nagi.WinUI.Models;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.ViewModels;
using System.Threading.Tasks;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.Pages;

public sealed partial class FolderSongViewPage : Page
{
    private readonly ILogger<FolderSongViewPage> _logger;
    private readonly ILibraryReader _libraryReader;
    private bool _isSearchExpanded;
    private bool _isUpdatingSelection;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
    private NowPlayingIndicatorBinder? _nowPlayingBinder;

    public FolderSongViewPage()
    {
        InitializeComponent();
        _dispatcherQueue = this.DispatcherQueue;
        ViewModel = App.Services!.GetRequiredService<IViewModelFactory>().Create<FolderSongListViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<FolderSongViewPage>>();
        _libraryReader = App.Services!.GetRequiredService<ILibraryReader>();
        DataContext = ViewModel;

        Loaded += OnNowPlayingBinderAttach;
        Unloaded += OnNowPlayingBinderDetach;

        Loaded += OnPageLoaded;
    }

    private void OnNowPlayingBinderAttach(object sender, RoutedEventArgs e)
    {
        if (_nowPlayingBinder is not null) return;
        // FolderContentsListView holds FolderContentItem (folders + songs); supply a custom
        // extractor that pulls the underlying Song.Id from song-typed items.
        _nowPlayingBinder = new NowPlayingIndicatorBinder(
            FolderContentsListView,
            ViewModel,
            App.Services!.GetRequiredService<PlayerViewModel>(),
            (Brush)Application.Current.Resources["AppPrimaryColorBrush"],
            songIdExtractor: item => (item as FolderContentItem)?.Song?.Id);
        _nowPlayingBinder.Refresh();
    }

    private void OnNowPlayingBinderDetach(object sender, RoutedEventArgs e)
    {
        _nowPlayingBinder?.Dispose();
        _nowPlayingBinder = null;
    }

    public FolderSongListViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        try
        {
            if (e.Parameter is FolderSongViewNavigationParameter navParam)
            {
                await ViewModel.InitializeAsync(navParam.Title, navParam.FolderId, navParam.DirectoryPath);
                await ViewModel.LoadAvailablePlaylistsAsync();
            }
            else
            {
                var paramType = e.Parameter?.GetType().Name ?? "null";
                _logger.LogWarning(
                    "Received invalid navigation parameter. Expected '{ExpectedType}', got '{ActualType}'. Initializing with fallback state.",
                    nameof(FolderSongViewNavigationParameter), paramType);
                await ViewModel.InitializeAsync(Nagi.WinUI.Resources.Strings.FolderSongViewPage_Default_Title, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize FolderSongViewPage.");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Dispose();
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        VisualStateManager.GoToState(this, "SearchCollapsed", false);
        Loaded -= OnPageLoaded;
    }

    private void OnSearchToggleButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isSearchExpanded)
            CollapseSearch();
        else
            ExpandSearch();
    }

    private void OnSearchTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            CollapseSearch();
            e.Handled = true;
        }
    }

    private void ExpandSearch()
    {
        if (_isSearchExpanded) return;

        _isSearchExpanded = true;
        ToolTipService.SetToolTip(SearchToggleButton, Nagi.WinUI.Resources.Strings.FolderSongViewPage_SearchButton_Close_ToolTip);
        VisualStateManager.GoToState(this, "SearchExpanded", true);
    }

    private void OnSearchExpansionCompleted(object sender, object e)
    {
        if (IsLoaded && _isSearchExpanded) SearchTextBox.Focus(FocusState.Programmatic);
    }

    private void OnBreadcrumbItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is BreadcrumbItem breadcrumb)
            ViewModel.NavigateToBreadcrumbCommand.Execute(breadcrumb);
    }

    private void CollapseSearch()
    {
        if (!_isSearchExpanded) return;

        _isSearchExpanded = false;
        ToolTipService.SetToolTip(SearchToggleButton, Nagi.WinUI.Resources.Strings.FolderSongViewPage_SearchButton_Search_ToolTip);
        VisualStateManager.GoToState(this, "SearchCollapsed", true);
        ViewModel.SearchTerm = string.Empty;
    }

    private async void FolderContentsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        try
        {
            if (e.ClickedItem is FolderContentItem contentItem)
                if (contentItem.IsFolder && contentItem.Folder != null)
                    await ViewModel.NavigateToSubfolderCommand.ExecuteAsync(contentItem.Folder);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling folder content item click");
        }
    }

    private void FolderContentsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection || sender is not ListView listView) return;

        foreach (var item in e.AddedItems.OfType<FolderContentItem>())
            ViewModel.SelectionState.Select(item.Id);

        foreach (var item in e.RemovedItems.OfType<FolderContentItem>())
            ViewModel.SelectionState.Deselect(item.Id);

        var selectedSongs = listView.SelectedItems
            .OfType<FolderContentItem>()
            .Where(item => item.IsSong && item.Song != null)
            .Select(item => item.Song!)
            .ToList();

        ViewModel.OnSongsSelectionChanged(selectedSongs);
    }

    private void OnSongsListViewContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not FolderContentItem contentItem) return;

        // Ensure the visual selection state matches our logical state as containers are reused.
        try
        {
            _isUpdatingSelection = true;
            args.ItemContainer.IsSelected = ViewModel.SelectionState.IsSelected(contentItem.Id);
        }
        finally
        {
            _isUpdatingSelection = false;
        }
    }

    private void OnSelectAllAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Don't hijack selection if a text input control is focused.
        var focused = FocusManager.GetFocusedElement(this.XamlRoot);
        if (focused is TextBox or PasswordBox or RichEditBox) return;

        _logger.LogDebug("Ctrl+A invoked. Selecting all songs in folder.");
        ViewModel.SelectAllCommand.Execute(null);

        // Sync the current visible items (only song items will be visually selected in the list via ContainerContentChanging if we use logical state)
        // But for visual feedback, we call SelectAll on the list view.
        try
        {
            _isUpdatingSelection = true;
            FolderContentsListView.SelectAll();
        }
        finally
        {
            _isUpdatingSelection = false;
        }

        args.Handled = true;
    }

    private async void FolderContentsListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        try
        {
            if (e.OriginalSource is FrameworkElement { DataContext: FolderContentItem contentItem })
            {
                if (contentItem.IsFolder && contentItem.Folder != null)
                    await ViewModel.NavigateToSubfolderCommand.ExecuteAsync(contentItem.Folder);
                else if (contentItem.IsSong && contentItem.Song != null)
                    ViewModel.PlaySongCommand.Execute(contentItem.Song);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling folder content double tap");
        }
    }

    private void FolderItemMenuFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menuFlyout) return;

        if (menuFlyout.Target?.DataContext is FolderContentItem { IsFolder: true, Folder: not null } contentItem)
        {
            if (!FolderContentsListView.SelectedItems.Contains(contentItem))
                FolderContentsListView.SelectedItem = contentItem;
        }

        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name == "FolderAddToPlaylistSubMenu") is { } addToPlaylistSubMenu)
            PlaylistMenuFlyoutHelper.Populate(addToPlaylistSubMenu, ViewModel.AvailablePlaylists,
                ViewModel.AddSelectedSongsToPlaylistCommand, Nagi.WinUI.Resources.Strings.FolderSongViewPage_PlaylistMenu_NoPlaylists);
    }

    private void SongItemMenuFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menuFlyout) return;

        if (menuFlyout.Target?.DataContext is FolderContentItem { IsSong: true, Song: not null } contentItem)
        {
            if (!FolderContentsListView.SelectedItems.Contains(contentItem))
                FolderContentsListView.SelectedItem = contentItem;
        }

        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name == "AddToPlaylistSubMenu") is { } addToPlaylistSubMenu)
            PlaylistMenuFlyoutHelper.Populate(addToPlaylistSubMenu, ViewModel.AvailablePlaylists,
                ViewModel.AddSelectedSongsToPlaylistCommand, Nagi.WinUI.Resources.Strings.FolderSongViewPage_PlaylistMenu_NoPlaylists);

        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name == "GoToArtistSubMenu") is { } goToArtistSubMenu
            && menuFlyout.Target?.DataContext is FolderContentItem { IsSong: true, Song: not null } songContentItem)
        {
            ArtistMenuFlyoutHelper.PopulateSubMenu(goToArtistSubMenu, songContentItem.Song, ViewModel.GoToArtistCommand, _libraryReader, _dispatcherQueue, _logger);
        }
    }

}
