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
using Nagi.Core.Models;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.ViewModels;
using System.Threading.Tasks;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.Pages;

public class DiscGroupTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? SongTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        return item is DiscHeader ? HeaderTemplate : SongTemplate;
    }
}

public class DiscGroupStyleSelector : StyleSelector
{
    public Style? HeaderStyle { get; set; }
    public Style? SongStyle { get; set; }

    protected override Style? SelectStyleCore(object item, DependencyObject container)
    {
        return item is DiscHeader ? HeaderStyle : SongStyle;
    }
}

public sealed partial class AlbumViewPage : Page
{
    private readonly ILogger<AlbumViewPage> _logger;
    private readonly ILibraryReader _libraryReader;
    private bool _isSearchExpanded;
    private bool _isUpdatingSelection;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
    private NowPlayingIndicatorBinder? _flatBinder;
    private NowPlayingIndicatorBinder? _groupedBinder;

    public AlbumViewPage()
    {
        InitializeComponent();
        _dispatcherQueue = this.DispatcherQueue;
        ViewModel = App.Services!.GetRequiredService<IViewModelFactory>().Create<AlbumViewViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<AlbumViewPage>>();
        _libraryReader = App.Services!.GetRequiredService<ILibraryReader>();
        DataContext = ViewModel;

        Loaded += OnNowPlayingBinderAttach;
        Unloaded += OnNowPlayingBinderDetach;

        Loaded += OnPageLoaded;
        _logger.LogDebug("AlbumViewPage initialized.");
    }

    private void OnNowPlayingBinderAttach(object sender, RoutedEventArgs e)
    {
        if (_flatBinder is not null || _groupedBinder is not null) return;
        var playerViewModel = App.Services!.GetRequiredService<PlayerViewModel>();
        var playingBrush = (Brush)Application.Current.Resources["AppPrimaryColorBrush"];
        _flatBinder = new NowPlayingIndicatorBinder(SongsListView, ViewModel, playerViewModel, playingBrush);
        _groupedBinder = new NowPlayingIndicatorBinder(GroupedSongsListView, ViewModel, playerViewModel, playingBrush);
        _flatBinder.Refresh();
        _groupedBinder.Refresh();
    }

    private void OnNowPlayingBinderDetach(object sender, RoutedEventArgs e)
    {
        _flatBinder?.Dispose();
        _flatBinder = null;
        _groupedBinder?.Dispose();
        _groupedBinder = null;
    }

    public AlbumViewViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _logger.LogDebug("Navigated to AlbumViewPage.");

        if (e.Parameter is AlbumViewNavigationParameter navParam)
        {
            try
            {
                await ViewModel.LoadAlbumDetailsAsync(navParam.AlbumId);
                await ViewModel.LoadAvailablePlaylistsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load details for AlbumId: {AlbumId}", navParam.AlbumId);
            }
        }
        else
        {
            var paramType = e.Parameter?.GetType().Name ?? "null";
            _logger.LogError(
                "Received incorrect navigation parameter type. Expected '{ExpectedType}', but got '{ActualType}'.",
                nameof(AlbumViewNavigationParameter), paramType);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logger.LogDebug("Navigating away from AlbumViewPage. Disposing ViewModel.");
        ViewModel.Dispose();
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("AlbumViewPage loaded. Setting initial visual state.");
        VisualStateManager.GoToState(this, "SearchCollapsed", false);
        Loaded -= OnPageLoaded;
    }

    private void OnSearchToggleButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isSearchExpanded)
        {
            _logger.LogDebug("Collapsing search box.");
            CollapseSearch();
        }
        else
        {
            _logger.LogDebug("Expanding search box.");
            ExpandSearch();
        }
    }

    private void OnSearchTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            _logger.LogDebug("Escape key pressed in search box. Collapsing search.");
            CollapseSearch();
            e.Handled = true;
        }
    }

    private void ExpandSearch()
    {
        if (_isSearchExpanded) return;

        _isSearchExpanded = true;
        _logger.LogDebug("Search UI expanded.");
        ToolTipService.SetToolTip(SearchToggleButton, Nagi.WinUI.Resources.Strings.AlbumViewPage_SearchButton_Close_ToolTip);
        VisualStateManager.GoToState(this, "SearchExpanded", true);
    }

    private void OnSearchExpansionCompleted(object sender, object e)
    {
        if (IsLoaded && _isSearchExpanded) SearchTextBox.Focus(FocusState.Programmatic);
    }

    private void CollapseSearch()
    {
        if (!_isSearchExpanded) return;

        _isSearchExpanded = false;

        _logger.LogDebug("Search UI collapsed and search term cleared.");
        ToolTipService.SetToolTip(SearchToggleButton, Nagi.WinUI.Resources.Strings.AlbumViewPage_SearchButton_Search_ToolTip);
        VisualStateManager.GoToState(this, "SearchCollapsed", true);
        ViewModel.SearchTerm = string.Empty;
    }

    private void SongsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection || sender is not ListView listView) return;

        UpdateLogicalSelection(e);
        ViewModel.OnSongsSelectionChanged(listView.SelectedItems);
    }

    private void UpdateLogicalSelection(SelectionChangedEventArgs e)
    {
        foreach (var song in e.AddedItems.OfType<Song>())
            ViewModel.SelectionState.Select(song.Id);

        foreach (var song in e.RemovedItems.OfType<Song>())
            ViewModel.SelectionState.Deselect(song.Id);
    }

    private void OnSongsListViewContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not Song song) return;

        // Ensure the visual selection state matches our logical state as containers are reused.
        try
        {
            _isUpdatingSelection = true;
            args.ItemContainer.IsSelected = ViewModel.SelectionState.IsSelected(song.Id);
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

        _logger.LogDebug("Ctrl+A invoked. Selecting all songs in album.");
        ViewModel.SelectAllCommand.Execute(null);

        try
        {
            _isUpdatingSelection = true;
            if (ViewModel.IsGroupedByDisc)
                GroupedSongsListView.SelectAll();
            else
                SongsListView.SelectAll();
        }
        finally
        {
            _isUpdatingSelection = false;
        }

        args.Handled = true;
    }

    private void SongsListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: Song tappedSong })
        {
            _logger.LogDebug("User double-tapped song '{SongTitle}' (Id: {SongId}). Executing play command.",
                tappedSong.Title, tappedSong.Id);
            ViewModel.PlaySongCommand.Execute(tappedSong);
        }
    }

    private void SongItemMenuFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menuFlyout) return;

        // Ensure the right-clicked song is selected before showing the context menu.
        if (menuFlyout.Target?.DataContext is Song rightClickedSong)
        {
            _logger.LogDebug("Context menu opening for song '{SongTitle}'.", rightClickedSong.Title);
            var listView = ViewModel.IsGroupedByDisc ? GroupedSongsListView : SongsListView;
            if (!listView.SelectedItems.Contains(rightClickedSong))
                listView.SelectedItem = rightClickedSong;
        }

        // Find and populate the "Add to Playlist" submenu (supports both grouped and regular ListViews).
        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name is "AddToPlaylistSubMenu" or "AddToPlaylistSubMenuGrouped") is { } addToPlaylistSubMenu)
        {
            _logger.LogDebug("Populating 'Add to playlist' submenu.");
            PlaylistMenuFlyoutHelper.Populate(addToPlaylistSubMenu, ViewModel.AvailablePlaylists,
                ViewModel.AddSelectedSongsToPlaylistCommand, Nagi.WinUI.Resources.Strings.AlbumViewPage_PlaylistMenu_NoPlaylists);
        }

        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name is "GoToArtistSubMenu" or "GoToArtistSubMenuGrouped") is { } goToArtistSubMenu
            && menuFlyout.Target?.DataContext is Song song)
        {
            ArtistMenuFlyoutHelper.PopulateSubMenu(goToArtistSubMenu, song, ViewModel.GoToArtistCommand, _libraryReader, _dispatcherQueue, _logger);
        }
    }

}
