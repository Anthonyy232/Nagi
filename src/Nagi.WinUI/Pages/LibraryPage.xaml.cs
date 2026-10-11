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
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Pages;

/// <summary>
///     The page for displaying and interacting with the user's music library.
/// </summary>
public sealed partial class LibraryPage : Page
{
    private string GetEmptyStateTitle(bool isSearchActive, bool favorites) => isSearchActive
        ? Nagi.WinUI.Resources.Strings.LibraryPage_SearchEmpty_Title
        : favorites ? Nagi.WinUI.Resources.Strings.Listening_FavoritesEmpty : Nagi.WinUI.Resources.Strings.LibraryPage_EmptyState_Title;

    private string GetEmptyStateSubtitle(bool isSearchActive, bool favorites) => isSearchActive
        ? Nagi.WinUI.Resources.Strings.LibraryPage_SearchEmpty_Subtitle
        : favorites ? Nagi.WinUI.Resources.Strings.Listening_FavoritesEmptyDescription : Nagi.WinUI.Resources.Strings.LibraryPage_EmptyState_Subtitle;

    private void OnArtistColumnSizeChanged(object sender, SizeChangedEventArgs e) =>
        ((FrameworkElement)((Grid)sender).Children[0]).MaxWidth = e.NewSize.Width;

    private readonly ILogger<LibraryPage> _logger;
    private readonly ILibraryReader _libraryReader;
    private bool _isSearchExpanded;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
    private NowPlayingIndicatorBinder? _nowPlayingBinder;

    public LibraryPage()
    {
        InitializeComponent();
        _dispatcherQueue = this.DispatcherQueue;
        ViewModel = App.Services!.GetRequiredService<LibraryViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<LibraryPage>>();
        _libraryReader = App.Services!.GetRequiredService<ILibraryReader>();
        DataContext = ViewModel;

        // Recreated on each Loaded so cached-page re-navigations get a fresh binder.
        Loaded += OnNowPlayingBinderAttach;
        Unloaded += OnNowPlayingBinderDetach;

        Loaded += OnPageLoaded;
        _logger.LogDebug("LibraryPage initialized.");
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

    public LibraryViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Reset selection every time we navigate back to this cached page.
        ViewModel.DeselectAll();
        _isUpdatingSelection = true;
        try { SongsListView.SelectedItems.Clear(); }
        finally { _isUpdatingSelection = false; }

        try
        {
            if (!ViewModel.HasLoadedPlaylists)
            {
                await ViewModel.LoadAvailablePlaylistsAsync();
            }

            if (!ViewModel.HasInitialized)
            {
                // Loads songs and triggers the initial background rescan (once per session).
                await ViewModel.InitializeAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize LibraryPage.");
        }
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("LibraryPage loaded. Setting initial visual state.");
        _isSearchExpanded = !string.IsNullOrWhiteSpace(ViewModel.SearchTerm);
        ToolTipService.SetToolTip(SearchToggleButton, _isSearchExpanded
            ? Nagi.WinUI.Resources.Strings.LibraryPage_SearchButton_Close_ToolTip
            : Nagi.WinUI.Resources.Strings.LibraryPage_SearchButton_Search_ToolTip);
        VisualStateManager.GoToState(this, _isSearchExpanded ? "SearchExpanded" : "SearchCollapsed", false);
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
        ToolTipService.SetToolTip(SearchToggleButton, Nagi.WinUI.Resources.Strings.LibraryPage_SearchButton_Close_ToolTip);
        VisualStateManager.GoToState(this, "SearchExpanded", false);
        SearchTextBox.Focus(FocusState.Programmatic);

    }

    private void CollapseSearch()
    {
        if (!_isSearchExpanded) return;

        _isSearchExpanded = false;
        _logger.LogDebug("Search UI collapsed and search term cleared.");
        ToolTipService.SetToolTip(SearchToggleButton, Nagi.WinUI.Resources.Strings.LibraryPage_SearchButton_Search_ToolTip);
        VisualStateManager.GoToState(this, "SearchCollapsed", false);
        SearchToggleButton.Focus(FocusState.Programmatic);
        ViewModel.SearchTerm = string.Empty;
    }

    private void SongItemMenuFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menuFlyout) return;

        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name == "AddToPlaylistSubMenu") is { } addToPlaylistSubMenu)
        {
            _logger.LogDebug("Populating 'Add to playlist' submenu.");
            PlaylistMenuFlyoutHelper.Populate(addToPlaylistSubMenu, ViewModel.AvailablePlaylists,
                ViewModel.AddSelectedSongsToPlaylistCommand, Nagi.WinUI.Resources.Strings.LibraryPage_PlaylistMenu_NoPlaylists);
        }

        if (menuFlyout.Target?.DataContext is not Song rightClickedSong) return;

        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name == "GoToArtistSubMenu") is { } goToArtistSubMenu)
        {
            ArtistMenuFlyoutHelper.PopulateSubMenu(goToArtistSubMenu, rightClickedSong, ViewModel.GoToArtistCommand, _libraryReader, _dispatcherQueue, _logger);
        }

        _logger.LogDebug("Context menu opening for song '{SongTitle}'.", rightClickedSong.Title);
        if (!SongsListView.SelectedItems.Contains(rightClickedSong))
            SongsListView.SelectedItem = rightClickedSong;
        ListeningMenuFlyoutHelper.PopulateFavorites(menuFlyout, ViewModel);
    }
    private bool _isUpdatingSelection;

    private void OnListViewSelectionChanged(object sender, SelectionChangedEventArgs e)
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

        _logger.LogDebug("Ctrl+A invoked. Selecting all songs.");
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

    private void SongsListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: Song tappedSong })
        {
            _logger.LogDebug("User double-tapped song '{SongTitle}' (Id: {SongId}). Executing play command.",
                tappedSong.Title, tappedSong.Id);
            ViewModel.PlaySongCommand.Execute(tappedSong);
        }
    }
    private void ListeningOptions_Opening(object sender, object e) =>
        ListeningMenuFlyoutHelper.PopulateMixes((MenuFlyout)sender, ViewModel.PlayMixCommand, ViewModel.PlayForCommand);
}
