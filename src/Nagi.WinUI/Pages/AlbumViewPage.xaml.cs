using System;
using System.Linq;
using Windows.System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Core.Models;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.ViewModels;
using System.Threading.Tasks;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Pages;

/// <summary>
///     Template selector for disc-grouped list (headers vs songs).
/// </summary>
public class DiscGroupTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? SongTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        return item is DiscHeader ? HeaderTemplate : SongTemplate;
    }
}

/// <summary>
///     Style selector for disc-grouped list items.
/// </summary>
public class DiscGroupStyleSelector : StyleSelector
{
    public Style? HeaderStyle { get; set; }
    public Style? SongStyle { get; set; }

    protected override Style? SelectStyleCore(object item, DependencyObject container)
    {
        return item is DiscHeader ? HeaderStyle : SongStyle;
    }
}

/// <summary>
///     A page that displays detailed information for a specific album, including its track list.
/// </summary>
public sealed partial class AlbumViewPage : Page
{
    private readonly ILogger<AlbumViewPage> _logger;
    private readonly ILibraryReader _libraryReader;
    private bool _isSearchExpanded;
    private bool _isUpdatingSelection;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;

    public AlbumViewPage()
    {
        InitializeComponent();
        _dispatcherQueue = this.DispatcherQueue;
        ViewModel = App.Services!.GetRequiredService<AlbumViewViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<AlbumViewPage>>();
        _libraryReader = App.Services!.GetRequiredService<ILibraryReader>(); // Inject ILibraryReader
        DataContext = ViewModel;

        Loaded += OnPageLoaded;
        _logger.LogDebug("AlbumViewPage initialized.");
    }

    /// <summary>
    ///     Gets the view model associated with this page.
    /// </summary>
    public AlbumViewViewModel ViewModel { get; }

    /// <summary>
    ///     Initializes the view model with navigation parameters when the page is navigated to.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _logger.LogDebug("Navigated to AlbumViewPage.");

        if (e.Parameter is AlbumViewNavigationParameter navParam)
        {
            _logger.LogDebug("Loading details for AlbumId: {AlbumId}", navParam.AlbumId);
            try
            {
                await ViewModel.LoadAlbumDetailsAsync(navParam.AlbumId);
                await ViewModel.LoadAvailablePlaylistsAsync();
                _logger.LogDebug("Successfully loaded details for AlbumId: {AlbumId}", navParam.AlbumId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load details for AlbumId: {AlbumId}", navParam.AlbumId);
            }
        }
        else
        {
            // This is a developer error, so log it as an error.
            var paramType = e.Parameter?.GetType().Name ?? "null";
            _logger.LogError(
                "Received incorrect navigation parameter type. Expected '{ExpectedType}', but got '{ActualType}'.",
                nameof(AlbumViewNavigationParameter), paramType);
        }
    }

    /// <summary>
    ///     Cleans up resources when the user navigates away from this page.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logger.LogDebug("Navigating away from AlbumViewPage. Disposing ViewModel.");
        ViewModel.Dispose();
    }

    /// <summary>
    ///     Handles the page loaded event to set initial visual state.
    /// </summary>
    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("AlbumViewPage loaded. Setting initial visual state.");
        VisualStateManager.GoToState(this, "SearchCollapsed", false);
        Loaded -= OnPageLoaded;
    }

    /// <summary>
    ///     Handles the search toggle button click to expand or collapse the search box.
    /// </summary>
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

    /// <summary>
    ///     Handles key down events in the search text box.
    /// </summary>
    private void OnSearchTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            _logger.LogDebug("Escape key pressed in search box. Collapsing search.");
            CollapseSearch();
            e.Handled = true;
        }
    }

    /// <summary>
    ///     Expands the search interface with an animation.
    /// </summary>
    private void ExpandSearch()
    {
        if (_isSearchExpanded) return;

        _isSearchExpanded = true;
        _logger.LogDebug("Search UI expanded.");
        ToolTipService.SetToolTip(SearchToggleButton, Nagi.WinUI.Resources.Strings.AlbumViewPage_SearchButton_Close_ToolTip);
        VisualStateManager.GoToState(this, "SearchExpanded", true);

        // Focus the search text box after the animation has started for a smooth transition.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(150);
        timer.Tick += (s, args) =>
        {
            timer.Stop();
            SearchTextBox.Focus(FocusState.Programmatic);
        };
        timer.Start();
    }

    /// <summary>
    ///     Collapses the search interface with an animation and resets the filter.
    ///     The data refresh is delayed to occur after the animation completes for a smoother UX.
    /// </summary>
    private void CollapseSearch()
    {
        if (!_isSearchExpanded) return;

        _isSearchExpanded = false;

        // Update UI immediately and start the collapse animation.
        _logger.LogDebug("Search UI collapsed and search term cleared.");
        ToolTipService.SetToolTip(SearchToggleButton, Nagi.WinUI.Resources.Strings.AlbumViewPage_SearchButton_Search_ToolTip);
        VisualStateManager.GoToState(this, "SearchCollapsed", true);
        ViewModel.SearchTerm = string.Empty;
    }

    /// <summary>
    ///     Updates the view model with the current selection from the song list.
    /// </summary>
    private void SongsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection || sender is not ListView listView) return;

        UpdateLogicalSelection(e);
        ViewModel.OnSongsSelectionChanged(listView.SelectedItems);
    }

    /// <summary>
    ///     Updates the view model with the current selection from the grouped song list (filters out headers).
    /// </summary>
    private void GroupedSongsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

        // Sync the current visible items
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

    /// <summary>
    ///     Handles double-tap on grouped list (ignores headers).
    /// </summary>
    private void GroupedSongsListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: Song tappedSong })
        {
            _logger.LogDebug("User double-tapped song '{SongTitle}'. Executing play command.", tappedSong.Title);
            ViewModel.PlaySongCommand.Execute(tappedSong);
        }
    }

    /// <summary>
    ///     Handles the double-tapped event on the song list to play the selected song.
    /// </summary>
    private void SongsListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: Song tappedSong })
        {
            _logger.LogDebug("User double-tapped song '{SongTitle}' (Id: {SongId}). Executing play command.",
                tappedSong.Title, tappedSong.Id);
            ViewModel.PlaySongCommand.Execute(tappedSong);
        }
    }

    /// <summary>
    ///     Handles the opening of the context menu for a song item.
    /// </summary>
    private void SongItemMenuFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menuFlyout) return;

        // Ensure the right-clicked song is selected before showing the context menu.
        if (menuFlyout.Target?.DataContext is Song rightClickedSong)
        {
            _logger.LogDebug("Context menu opening for song '{SongTitle}'.", rightClickedSong.Title);
            if (!SongsListView.SelectedItems.Contains(rightClickedSong))
                SongsListView.SelectedItem = rightClickedSong;
        }

        // Find and populate the "Add to Playlist" submenu (supports both grouped and regular ListViews).
        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name is "AddToPlaylistSubMenu" or "AddToPlaylistSubMenuGrouped") is { } addToPlaylistSubMenu)
        {
            _logger.LogDebug("Populating 'Add to playlist' submenu.");
            PopulatePlaylistSubMenu(addToPlaylistSubMenu);
        }

        // NEW: Populate "Go to artist" submenu (supports both grouped and regular ListViews)
        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name is "GoToArtistSubMenu" or "GoToArtistSubMenuGrouped") is { } goToArtistSubMenu
            && menuFlyout.Target?.DataContext is Song song)
        {
            ArtistMenuFlyoutHelper.PopulateSubMenu(goToArtistSubMenu, song, ViewModel.GoToArtistCommand, _libraryReader, _dispatcherQueue, _logger);
        }
    }

    /// <summary>
    ///     Populates the "Add to playlist" submenu with available playlists from the view model.
    /// </summary>
    private void PopulatePlaylistSubMenu(MenuFlyoutSubItem subMenu)
    {
        subMenu.Items.Clear();

        var availablePlaylists = ViewModel.AvailablePlaylists;

        if (availablePlaylists?.Any() != true)
        {
            _logger.LogDebug("No playlists available to populate submenu.");
            var disabledItem = new MenuFlyoutItem { Text = Nagi.WinUI.Resources.Strings.AlbumViewPage_PlaylistMenu_NoPlaylists, IsEnabled = false };
            subMenu.Items.Add(disabledItem);
            return;
        }

        _logger.LogDebug("Found {PlaylistCount} playlists to populate submenu.", availablePlaylists.Count);
        foreach (var playlist in availablePlaylists)
        {
            var playlistMenuItem = new MenuFlyoutItem
            {
                Text = playlist.Name,
                Command = ViewModel.AddSelectedSongsToPlaylistCommand,
                CommandParameter = playlist
            };
            subMenu.Items.Add(playlistMenuItem);
        }
    }
}