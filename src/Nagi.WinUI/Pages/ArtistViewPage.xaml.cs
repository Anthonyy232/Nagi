using System;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
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
using Microsoft.Windows.Storage.Pickers;
using Nagi.Core.Constants;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Services.Abstractions;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Nagi.WinUI.Pages;

public sealed partial class ArtistViewPage : Page
{
    private readonly ILogger<ArtistViewPage> _logger;
    private bool _isSearchExpanded;
    private bool _isUpdatingSelection;
    private bool _isUpdatingAlbumsLayout;
    private bool _pendingLayoutUpdate;
    private ScrollViewer? _songsScrollViewer;
    private double _lastKnownAlbumsHeight;
    private NowPlayingIndicatorBinder? _nowPlayingBinder;

    public ArtistViewPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<IViewModelFactory>().Create<ArtistViewViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<ArtistViewPage>>();
        DataContext = ViewModel;

        Loaded += OnNowPlayingBinderAttach;
        Unloaded += OnNowPlayingBinderDetach;

        Loaded += OnPageLoaded;

        SongsListView.Loaded += OnSongsListViewLoaded;

        _logger.LogDebug("ArtistViewPage initialized.");
    }

    private void OnNowPlayingBinderAttach(object sender, RoutedEventArgs e)
    {
        if (_nowPlayingBinder is not null) return;
        _nowPlayingBinder = new NowPlayingIndicatorBinder(
            SongsListView,
            ViewModel,
            App.Services!.GetRequiredService<PlayerViewModel>(),
            (Brush)Application.Current.Resources["AppPrimaryColorBrush"]);
        _nowPlayingBinder.Refresh();
    }

    private void OnNowPlayingBinderDetach(object sender, RoutedEventArgs e)
    {
        _nowPlayingBinder?.Dispose();
        _nowPlayingBinder = null;
    }

    public ArtistViewViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _logger.LogDebug("Navigated to ArtistViewPage.");

        // Reset layout state for new artist
        AlbumsViewport.Height = double.NaN;
        AlbumsViewport.Opacity = 1;
        AlbumsContent.Translation = Vector3.Zero;
        _lastKnownAlbumsHeight = 0;

        if (e.Parameter is ArtistViewNavigationParameter navParam)
        {
            try
            {
                await ViewModel.LoadArtistDetailsAsync(navParam.ArtistId);
                await ViewModel.LoadAvailablePlaylistsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load details for ArtistId: {ArtistId}", navParam.ArtistId);
            }
        }
        else
        {
            var paramType = e.Parameter?.GetType().Name ?? "null";
            _logger.LogError(
                "Received incorrect navigation parameter type. Expected '{ExpectedType}', but got '{ActualType}'.",
                nameof(ArtistViewNavigationParameter), paramType);
        }
    }

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

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("ArtistViewPage loaded. Setting initial visual state.");
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
        ToolTipService.SetToolTip(SearchToggleButton, Nagi.WinUI.Resources.Strings.ArtistViewPage_SearchButton_Close_ToolTip);
        VisualStateManager.GoToState(this, "SearchExpanded", false);
        SearchTextBox.Focus(FocusState.Programmatic);
    }

    private void CollapseSearch()
    {
        if (!_isSearchExpanded) return;

        _isSearchExpanded = false;

        _logger.LogDebug("Search UI collapsed and search term cleared.");
        ToolTipService.SetToolTip(SearchToggleButton, Nagi.WinUI.Resources.Strings.ArtistViewPage_SearchButton_Search_ToolTip);
        VisualStateManager.GoToState(this, "SearchCollapsed", false);
        SearchToggleButton.Focus(FocusState.Programmatic);
        ViewModel.SearchTerm = string.Empty;
    }

    private void AlbumGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ArtistAlbumViewModelItem clickedAlbum)
        {
            _logger.LogDebug("User clicked album '{AlbumTitle}' (Id: {AlbumId}). Navigating to album view.",
                clickedAlbum.Name, clickedAlbum.Id);
            ViewModel.ViewAlbumCommand.Execute(clickedAlbum.Id);
        }
    }

    private void SongsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection || sender is not ListView listView) return;

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
            if (!SongsListView.SelectedItems.Contains(rightClickedSong))
                SongsListView.SelectedItem = rightClickedSong;
        }

        // Find and populate the "Add to Playlist" submenu.
        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name == "AddToPlaylistSubMenu") is { } addToPlaylistSubMenu)
        {
            _logger.LogDebug("Populating 'Add to playlist' submenu.");
            PlaylistMenuFlyoutHelper.Populate(addToPlaylistSubMenu, ViewModel.AvailablePlaylists,
                ViewModel.AddSelectedSongsToPlaylistCommand, Nagi.WinUI.Resources.Strings.ArtistViewPage_PlaylistMenu_NoPlaylists);
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
        var picker = new FileOpenPicker(App.RootWindow!.AppWindow.Id);

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

        _songsScrollViewer = SongsListView.FindDescendant<ScrollViewer>();
        if (_songsScrollViewer != null)
        {
            _songsScrollViewer.ViewChanged += OnSongsScrollViewerViewChanged;
        }
    }

    private void OnAlbumsContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Height - _lastKnownAlbumsHeight) < 0.5) return;

        _lastKnownAlbumsHeight = e.NewSize.Height;
        ScheduleAlbumsLayoutUpdate();
    }

    private void OnSongsScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        ScheduleAlbumsLayoutUpdate();
    }

    private void ScheduleAlbumsLayoutUpdate()
    {
        // Defer changes until after measure/arrange and coalesce repeated scroll/size events.

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
            AlbumsContent.Translation = new Vector3(0, translationY, 0);

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
    private void ListeningOptions_Opening(object sender, object e) =>
        ListeningMenuFlyoutHelper.PopulateMixes((MenuFlyout)sender, ViewModel.PlayMixCommand, ViewModel.PlayForCommand);
}
