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
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Pages;

/// <summary>
///     The page for displaying and interacting with the user's music library.
/// </summary>
public sealed partial class LibraryPage : Page {
    private readonly ILogger<LibraryPage> _logger;
    private bool _isSearchExpanded;

    public LibraryPage() {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<LibraryViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<LibraryPage>>();
        DataContext = ViewModel;

        Loaded += OnPageLoaded;
        _logger.LogInformation("LibraryPage initialized.");
    }

    /// <summary>
    ///     Gets the ViewModel associated with this page.
    /// </summary>
    public LibraryViewModel ViewModel { get; }

    /// <summary>
    ///     Loads necessary data when the page is navigated to.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e) {
        base.OnNavigatedTo(e);
        _logger.LogInformation("Navigated to LibraryPage. Initializing...");

        try {
            // Load playlists first for context menu availability.
            await ViewModel.LoadAvailablePlaylistsAsync();

            // This handles the initial UI load and a background rescan.
            await ViewModel.InitializeAsync();
            _logger.LogInformation("LibraryPage initialization complete.");
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to initialize LibraryPage.");
        }
    }

    /// <summary>
    ///     Cleans up the search state when navigating away from the page.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e) {
        base.OnNavigatedFrom(e);
        _logger.LogInformation("Navigating away from LibraryPage. Cleaning up ViewModel.");
        ViewModel.Cleanup();
    }

    /// <summary>
    ///     Handles the page loaded event to set initial visual state.
    /// </summary>
    private void OnPageLoaded(object sender, RoutedEventArgs e) {
        _logger.LogDebug("LibraryPage loaded. Setting initial visual state.");
        VisualStateManager.GoToState(this, "SearchCollapsed", false);
        Loaded -= OnPageLoaded;
    }

    /// <summary>
    ///     Handles the search toggle button click to expand or collapse the search box.
    /// </summary>
    private void OnSearchToggleButtonClick(object sender, RoutedEventArgs e) {
        if (_isSearchExpanded)
            CollapseSearch();
        else
            ExpandSearch();
    }

    /// <summary>
    ///     Handles key down events in the search text box.
    /// </summary>
    private void OnSearchTextBoxKeyDown(object sender, KeyRoutedEventArgs e) {
        if (e.Key == VirtualKey.Escape) {
            _logger.LogDebug("Escape key pressed in search box. Collapsing search.");
            CollapseSearch();
            e.Handled = true;
        }
    }

    /// <summary>
    ///     Expands the search interface with an animation.
    /// </summary>
    private void ExpandSearch() {
        if (_isSearchExpanded) return;

        _isSearchExpanded = true;
        _logger.LogInformation("Search UI expanded.");
        ToolTipService.SetToolTip(SearchToggleButton, "Close search");
        VisualStateManager.GoToState(this, "SearchExpanded", true);

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(150);
        timer.Tick += (s, args) => {
            timer.Stop();
            SearchTextBox.Focus(FocusState.Programmatic);
        };
        timer.Start();
    }

    /// <summary>
    ///     Collapses the search interface with an animation and resets the filter.
    /// </summary>
    private void CollapseSearch() {
        if (!_isSearchExpanded) return;

        _isSearchExpanded = false;
        _logger.LogInformation("Search UI collapsed and search term cleared.");
        ToolTipService.SetToolTip(SearchToggleButton, "Search library");
        VisualStateManager.GoToState(this, "SearchCollapsed", true);
        ViewModel.SearchTerm = string.Empty;
    }

    /// <summary>
    ///     Handles the opening of the context menu for a song item.
    /// </summary>
    private void SongItemMenuFlyout_Opening(object sender, object e) {
        if (sender is not MenuFlyout menuFlyout) return;

        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name == "AddToPlaylistSubMenu") is { } addToPlaylistSubMenu) {
            _logger.LogDebug("Populating 'Add to playlist' submenu.");
            addToPlaylistSubMenu.Items.Clear();
            if (ViewModel.AvailablePlaylists.Any()) {
                foreach (var playlist in ViewModel.AvailablePlaylists) {
                    var playlistMenuItem = new MenuFlyoutItem {
                        Text = playlist.Name,
                        Command = ViewModel.AddSelectedSongsToPlaylistCommand,
                        CommandParameter = playlist
                    };
                    addToPlaylistSubMenu.Items.Add(playlistMenuItem);
                }
            }
            else {
                addToPlaylistSubMenu.Items.Add(
                    new MenuFlyoutItem { Text = "No playlists available", IsEnabled = false });
            }
        }

        if (menuFlyout.Target?.DataContext is not Song rightClickedSong) return;

        _logger.LogDebug("Context menu opening for song '{SongTitle}'.", rightClickedSong.Title);
        if (!SongsListView.SelectedItems.Contains(rightClickedSong))
            SongsListView.SelectedItem = rightClickedSong;
    }

    /// <summary>
    ///     Notifies the ViewModel when the ListView's selection has changed.
    /// </summary>
    private void OnListViewSelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (sender is ListView listView) {
            _logger.LogTrace("Song selection changed. {SelectedCount} items selected.", listView.SelectedItems.Count);
            ViewModel.OnSongsSelectionChanged(listView.SelectedItems);
        }
    }

    /// <summary>
    ///     Handles the double-tapped event on the song list to play the selected song.
    /// </summary>
    private void SongsListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) {
        if (e.OriginalSource is FrameworkElement { DataContext: Song tappedSong }) {
            _logger.LogInformation("User double-tapped song '{SongTitle}' (Id: {SongId}). Executing play command.",
                tappedSong.Title, tappedSong.Id);
            ViewModel.PlaySongCommand.Execute(tappedSong);
        }
    }
}