using System;
using System.Threading.Tasks;
using System.Linq;
using System.Numerics;
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
using Windows.Storage.Pickers;
using WinRT.Interop;
using Nagi.Core.Constants;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page that displays detailed information for a specific artist, including their albums and songs.
/// </summary>
public sealed partial class ArtistViewPage : Page
{
    private readonly ILogger<ArtistViewPage> _logger;
    private bool _isSearchExpanded;
    private bool _isUpdatingSelection;
    private bool _isUpdatingAlbumsLayout;
    private bool _pendingLayoutUpdate;
    private ScrollViewer? _songsScrollViewer;
    private double _lastKnownAlbumsHeight;

    public ArtistViewPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<ArtistViewViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<ArtistViewPage>>();
        DataContext = ViewModel;

        Loaded += OnPageLoaded;
        
        // Ensure we hook up the scroll viewer once the list is loaded
        SongsListView.Loaded += OnSongsListViewLoaded;
        
        _logger.LogDebug("ArtistViewPage initialized.");
    }

    /// <summary>
    ///     Gets the view model associated with this page.
    /// </summary>
    public ArtistViewViewModel ViewModel { get; }

    /// <summary>
    ///     Initializes the view model with navigation parameters when the page is navigated to.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _logger.LogDebug("Navigated to ArtistViewPage.");

        // Reset layout state for new artist
        if (AlbumsViewport != null)
        {
            AlbumsViewport.Height = double.NaN;
            AlbumsViewport.Opacity = 1;
            if (AlbumsContent != null) AlbumsContent.Translation = Vector3.Zero;
        }
        _lastKnownAlbumsHeight = 0;

        if (e.Parameter is ArtistViewNavigationParameter navParam)
        {
            _logger.LogDebug("Loading details for ArtistId: {ArtistId}", navParam.ArtistId);
            try
            {
                await ViewModel.LoadArtistDetailsAsync(navParam.ArtistId);
                await ViewModel.LoadAvailablePlaylistsAsync();
                _logger.LogDebug("Successfully loaded details for ArtistId: {ArtistId}", navParam.ArtistId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load details for ArtistId: {ArtistId}", navParam.ArtistId);
            }
        }
        else
        {
            // This is a developer error, so log it as an error.
            var paramType = e.Parameter?.GetType().Name ?? "null";
            _logger.LogError(
                "Received incorrect navigation parameter type. Expected '{ExpectedType}', but got '{ActualType}'.",
                nameof(ArtistViewNavigationParameter), paramType);
        }
    }

    /// <summary>
    ///     Cleans up resources when the user navigates away from this page.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logger.LogDebug("Navigating away from ArtistViewPage. Disposing ViewModel.");
        if (_songsScrollViewer != null)
        {
            _songsScrollViewer.ViewChanged -= OnSongsScrollViewerViewChanged;
        }
        ViewModel.Dispose();
    }

    /// <summary>
    ///     Handles the page loaded event to set initial visual state.
    /// </summary>
    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("ArtistViewPage loaded. Setting initial visual state.");
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
        ToolTipService.SetToolTip(SearchToggleButton, Nagi.WinUI.Resources.Strings.ArtistViewPage_SearchButton_Close_ToolTip);
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
        ToolTipService.SetToolTip(SearchToggleButton, Nagi.WinUI.Resources.Strings.ArtistViewPage_SearchButton_Search_ToolTip);
        VisualStateManager.GoToState(this, "SearchCollapsed", true);
        ViewModel.SearchTerm = string.Empty;
    }

    /// <summary>
    ///     Handles clicks on the album grid, navigating to the selected album's page.
    /// </summary>
    private void AlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ArtistAlbumViewModelItem clickedAlbum)
        {
            _logger.LogDebug("User clicked album '{AlbumTitle}' (Id: {AlbumId}). Navigating to album view.",
                clickedAlbum.Name, clickedAlbum.Id);
            ViewModel.ViewAlbumCommand.Execute(clickedAlbum.Id);
        }
    }

    /// <summary>
    ///     Updates the view model with the current selection from the song list.
    /// </summary>
    private void SongsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection || sender is not ListView listView) return;

        // Update logical selection state based on individual changes
        foreach (var song in e.AddedItems.OfType<Song>())
            ViewModel.SelectionState.Select(song.Id);

        foreach (var song in e.RemovedItems.OfType<Song>())
            ViewModel.SelectionState.Deselect(song.Id);

        ViewModel.OnSongsSelectionChanged(listView.SelectedItems);
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

        _logger.LogDebug("Ctrl+A invoked. Selecting all songs for artist.");
        ViewModel.SelectAllCommand.Execute(null);

        // Sync the current visible items
        try
        {
            _isUpdatingSelection = true;
            SongsListView.SelectAll();
        }
        finally
        {
            _isUpdatingSelection = false;
        }

        args.Handled = true;
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

        // Find and populate the "Add to Playlist" submenu.
        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name == "AddToPlaylistSubMenu") is { } addToPlaylistSubMenu)
        {
            _logger.LogDebug("Populating 'Add to playlist' submenu.");
            PopulatePlaylistSubMenu(addToPlaylistSubMenu);
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
            var disabledItem = new MenuFlyoutItem { Text = Nagi.WinUI.Resources.Strings.ArtistViewPage_PlaylistMenu_NoPlaylists, IsEnabled = false };
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

    private void ArtistImage_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        EditOverlay.Opacity = 1;
    }

    private void ArtistImage_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        EditOverlay.Opacity = 0;
    }

    private void EditOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ImageEditFlyout.ShowAt(EditOverlay, new FlyoutShowOptions { Position = e.GetCurrentPoint(EditOverlay).Position });
    }

    private async void ChangeImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _logger.LogDebug("User initiated image change for artist '{ArtistName}'.", ViewModel.ArtistName);
            var newImagePath = await PickImageAsync();

            if (!string.IsNullOrWhiteSpace(newImagePath))
            {
                _logger.LogDebug("User selected new image for artist '{ArtistName}'. Updating.", ViewModel.ArtistName);
                await ViewModel.UpdateArtistImageCommand.ExecuteAsync(newImagePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing image for artist {ArtistName}", ViewModel.ArtistName);
        }
    }

    private async void RemoveImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _logger.LogDebug("User requested removal of custom image for artist '{ArtistName}'.", ViewModel.ArtistName);
            await ViewModel.RemoveArtistImageCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing image for artist {ArtistName}", ViewModel.ArtistName);
        }
    }

    private async Task<string?> PickImageAsync()
    {
        _logger.LogDebug("Opening file picker for artist image.");
        var picker = new FileOpenPicker();
        var hwnd = WindowNative.GetWindowHandle(App.RootWindow);
        InitializeWithWindow.Initialize(picker, hwnd);
        
        foreach (var ext in FileExtensions.ImageFileExtensions)
            picker.FileTypeFilter.Add(ext);

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            _logger.LogDebug("User picked image file: {FilePath}", file.Path);
            return file.Path;
        }

        _logger.LogDebug("User did not pick an image file.");
        return null;
    }

    private void OnSongsListViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_songsScrollViewer != null) return;

        _songsScrollViewer = FindScrollViewer(SongsListView);
        if (_songsScrollViewer != null)
        {
            _songsScrollViewer.ViewChanged += OnSongsScrollViewerViewChanged;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var queue = new System.Collections.Generic.Queue<DependencyObject>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is ScrollViewer sv) return sv;

            int count = VisualTreeHelper.GetChildrenCount(current);
            for (int i = 0; i < count; i++)
            {
                queue.Enqueue(VisualTreeHelper.GetChild(current, i));
            }
        }
        return null;
    }

    private void OnAlbumsContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Only update if the height actually changed
        if (Math.Abs(e.NewSize.Height - _lastKnownAlbumsHeight) < 0.5) return;
        
        _lastKnownAlbumsHeight = e.NewSize.Height;
        ScheduleAlbumsLayoutUpdate();
    }

    private void OnSongsScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        ScheduleAlbumsLayoutUpdate();
    }

    /// <summary>
    ///     Schedules a layout update for the albums section on the next dispatcher tick.
    ///     This prevents layout cycles by ensuring we don't modify layout properties
    ///     during an active measure/arrange pass. Multiple rapid calls are coalesced.
    /// </summary>
    private void ScheduleAlbumsLayoutUpdate()
    {
        // Coalesce multiple rapid requests - only schedule if not already pending
        if (_pendingLayoutUpdate) return;
        _pendingLayoutUpdate = true;
        
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _pendingLayoutUpdate = false;
            UpdateAlbumsLayout();
        });
    }

    private void UpdateAlbumsLayout()
    {
        if (_isUpdatingAlbumsLayout || _songsScrollViewer == null || !ViewModel.HasAlbums) return;

        try
        {
            _isUpdatingAlbumsLayout = true;
            
            // Calculate new height: Max - ScrollOffset, clamped to 0
            var scrollOffset = _songsScrollViewer.VerticalOffset;
            var newHeight = Math.Max(0, _lastKnownAlbumsHeight - scrollOffset);

            // Only apply height if it changed significantly to avoid layout cycles
            if (Math.Abs(AlbumsViewport.Height - newHeight) > 0.5 || double.IsNaN(AlbumsViewport.Height))
            {
                AlbumsViewport.Height = newHeight;
            }
            
            // Translate the content up to mimic scrolling naturally
            // We clamp the translation so it doesn't float away if we scroll way past
            var translationY = (float)-Math.Min(scrollOffset, _lastKnownAlbumsHeight);
            if (AlbumsContent != null)
            {
                AlbumsContent.Translation = new Vector3(0, translationY, 0);
            }

            if (_lastKnownAlbumsHeight > 0)
            {
                var opacity = Math.Clamp(newHeight / _lastKnownAlbumsHeight, 0, 1);
                
                // Optimization: Snap to 0 if very low to save GPU composition effort
                if (opacity < 0.05) opacity = 0;
                
                AlbumsViewport.Opacity = opacity;
            }
        }
        finally
        {
            _isUpdatingAlbumsLayout = false;
        }
    }
}