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
using Nagi.Presentation.Navigation;
using Nagi.Presentation.ViewModels;
using System.Threading.Tasks;
using Nagi.Core.Services.Abstractions;
using Nagi.Presentation.Resources;
using Nagi.Presentation.Services.Abstractions;
using Nagi.WinUI.Helpers;

namespace Nagi.WinUI.Pages;

public sealed partial class GenreViewPage : Page
{
    private readonly ILogger<GenreViewPage> _logger;
    private bool _isSearchExpanded;
    private bool _isUpdatingSelection;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
    private NowPlayingIndicatorBinder? _nowPlayingBinder;

    public GenreViewPage()
    {
        InitializeComponent();
        _dispatcherQueue = this.DispatcherQueue;
        ViewModel = App.Services!.GetRequiredService<IViewModelFactory>().Create<GenreViewViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<GenreViewPage>>();
        DataContext = ViewModel;

        Loaded += OnNowPlayingBinderAttach;
        Unloaded += OnNowPlayingBinderDetach;

        Loaded += OnPageLoaded;
        _logger.LogDebug("GenreViewPage initialized.");
    }

    private void OnNowPlayingBinderAttach(object sender, RoutedEventArgs e)
    {
        if (_nowPlayingBinder is not null) return;
        _nowPlayingBinder = new NowPlayingIndicatorBinder(
            SongsListView,
            ViewModel,
            (Brush)Application.Current.Resources["AppPrimaryColorBrush"]);
        _nowPlayingBinder.Refresh();
    }

    private void OnNowPlayingBinderDetach(object sender, RoutedEventArgs e)
    {
        _nowPlayingBinder?.Dispose();
        _nowPlayingBinder = null;
    }

    public GenreViewViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _logger.LogDebug("Navigated to GenreViewPage.");

        if (e.Parameter is GenreViewNavigationParameter navParam)
        {
            try
            {
                await ViewModel.LoadGenreDetailsAsync(navParam);
                await ViewModel.LoadAvailablePlaylistsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load details for genre '{GenreName}'.", navParam.GenreName);
            }
        }
        else
        {
            var paramType = e.Parameter?.GetType().Name ?? "null";
            _logger.LogError(
                "Received incorrect navigation parameter type. Expected '{ExpectedType}', but got '{ActualType}'.",
                nameof(GenreViewNavigationParameter), paramType);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logger.LogDebug("Navigating away from GenreViewPage. Disposing ViewModel.");
        ViewModel.Dispose();
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("GenreViewPage loaded. Setting initial visual state.");
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
        ToolTipService.SetToolTip(SearchToggleButton, Strings.GenreViewPage_SearchButton_Close_ToolTip);
        VisualStateManager.GoToState(this, "SearchExpanded", false);
        SearchTextBox.Focus(FocusState.Programmatic);

    }

    private void CollapseSearch()
    {
        if (!_isSearchExpanded) return;

        _isSearchExpanded = false;
        _logger.LogDebug("Search UI collapsed and search term cleared.");
        ToolTipService.SetToolTip(SearchToggleButton, Strings.GenreViewPage_SearchButton_Search_ToolTip);
        VisualStateManager.GoToState(this, "SearchCollapsed", false);
        SearchToggleButton.Focus(FocusState.Programmatic);
        ViewModel.SearchTerm = string.Empty;
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

        _logger.LogDebug("Ctrl+A invoked. Selecting all songs for genre.");
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

        if (menuFlyout.Target?.DataContext is Song rightClickedSong)
        {
            _logger.LogDebug("Context menu opening for song '{SongTitle}'.", rightClickedSong.Title);
            if (!SongsListView.SelectedItems.Contains(rightClickedSong))
                SongsListView.SelectedItem = rightClickedSong;
        }

        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name == "AddToPlaylistSubMenu") is { } addToPlaylistSubMenu)
        {
            _logger.LogDebug("Populating 'Add to playlist' submenu.");
            PlaylistMenuFlyoutHelper.Populate(addToPlaylistSubMenu, ViewModel.AvailablePlaylists,
                ViewModel.AddSelectedSongsToPlaylistCommand, Strings.GenreViewPage_PlaylistMenu_NoPlaylists);
        }

        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name == "GoToArtistSubMenu") is { } goToArtistSubMenu
            && menuFlyout.Target?.DataContext is Song song)
        {
            ArtistMenuFlyoutHelper.PopulateSubMenu(goToArtistSubMenu, song, ViewModel.GoToArtistCommand, ViewModel.GetArtistsForSongAsync, _dispatcherQueue, _logger);
        }
        ListeningMenuFlyoutHelper.PopulateFavorites(menuFlyout, ViewModel);
    }


    private void ListeningOptions_Opening(object sender, object e) =>
        ListeningMenuFlyoutHelper.PopulateMixes((MenuFlyout)sender, ViewModel.PlayMixCommand, ViewModel.PlayForCommand);
}
