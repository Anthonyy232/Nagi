using CommunityToolkit.WinUI.Animations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Core.Models;
using Nagi.WinUI.ViewModels;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Nagi.WinUI.Pages;

/// <summary>
/// The page for displaying and interacting with the user's music library.
/// </summary>
public sealed partial class LibraryPage : Page {
    private bool _isSearchExpanded;

    public LibraryPage() {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<LibraryViewModel>();
        DataContext = ViewModel;

        Loaded += OnPageLoaded;
    }

    /// <summary>
    /// Gets the ViewModel associated with this page.
    /// </summary>
    public LibraryViewModel ViewModel { get; }

    /// <summary>
    /// Loads necessary data when the page is navigated to.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e) {
        base.OnNavigatedTo(e);

        // Load playlists first for context menu availability.
        await ViewModel.LoadAvailablePlaylistsAsync();

        // This handles the initial UI load and a background rescan.
        await ViewModel.InitializeAsync();
    }

    /// <summary>
    /// Cleans up the search state when navigating away from the page.
    /// This ensures that if a search filter is active, it's cleared so the user
    /// sees the full library when they return.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e) {
        base.OnNavigatedFrom(e);
        ViewModel.Cleanup();
    }

    /// <summary>
    /// Handles the page loaded event to set initial visual state.
    /// </summary>
    private void OnPageLoaded(object sender, RoutedEventArgs e) {
        // Set initial search state to collapsed without animation.
        VisualStateManager.GoToState(this, "SearchCollapsed", false);
        Loaded -= OnPageLoaded;
    }

    /// <summary>
    /// Handles the search toggle button click to expand or collapse the search box.
    /// </summary>
    private void OnSearchToggleButtonClick(object sender, RoutedEventArgs e) {
        if (_isSearchExpanded) {
            CollapseSearch();
        }
        else {
            ExpandSearch();
        }
    }

    /// <summary>
    /// Handles key down events in the search text box.
    /// </summary>
    private void OnSearchTextBoxKeyDown(object sender, KeyRoutedEventArgs e) {
        if (e.Key == Windows.System.VirtualKey.Escape) {
            CollapseSearch();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Expands the search interface with an animation.
    /// </summary>
    private void ExpandSearch() {
        if (_isSearchExpanded) return;

        _isSearchExpanded = true;
        ToolTipService.SetToolTip(SearchToggleButton, "Close search");
        VisualStateManager.GoToState(this, "SearchExpanded", true);

        // Focus the search text box after the animation has started for a smooth transition.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(150);
        timer.Tick += (s, args) => {
            timer.Stop();
            SearchTextBox.Focus(FocusState.Programmatic);
        };
        timer.Start();
    }

    /// <summary>
    /// Collapses the search interface with an animation and resets the filter.
    /// The data refresh is delayed to occur after the animation completes for a smoother UX.
    /// </summary>
    private void CollapseSearch() {
        if (!_isSearchExpanded) return;

        _isSearchExpanded = false;

        // Update UI immediately and start the collapse animation.
        ToolTipService.SetToolTip(SearchToggleButton, "Search library");
        VisualStateManager.GoToState(this, "SearchCollapsed", true);

        // Store the current search term to check if we need to refresh data
        var previousSearchTerm = ViewModel.SearchTerm;
        
        // Clear the search term. This may trigger a debounced search in the ViewModel.
        ViewModel.SearchTerm = string.Empty;

        // Only schedule a data refresh if the search term was not already empty
        // This prevents unnecessary refetching when collapsing from an empty search
        if (!string.IsNullOrWhiteSpace(previousSearchTerm)) {
            // Schedule the data refresh to happen after the animation completes.
            // This prevents the data fetch from causing stutters in the animation.
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(350); // Slightly longer than the animation duration.
            timer.Tick += (s, args) => {
                timer.Stop();
                try {
                    // Execute the search command to reload the full, unfiltered list.
                    // This command correctly cancels any pending debounce operations.
                    _ = ViewModel.SearchCommand.ExecuteAsync(null);
                }
                catch (Exception ex) {
                    System.Diagnostics.Debug.WriteLine($"[LibraryPage] ERROR: Failed to refresh after search collapse. {ex.Message}");
                }
            };
            timer.Start();
        }
    }

    /// <summary>
    /// Handles the opening of the context menu for a song item. It dynamically populates
    /// the "Add to playlist" submenu and ensures the right-clicked item is selected.
    /// </summary>
    private void SongItemMenuFlyout_Opening(object sender, object e) {
        if (sender is not MenuFlyout menuFlyout) return;

        // Dynamically build the "Add to playlist" submenu.
        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name == "AddToPlaylistSubMenu") is { } addToPlaylistSubMenu) {
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

        // If the user right-clicks an item that is not already selected,
        // change the selection to that single item for a better user experience.
        if (!SongsListView.SelectedItems.Contains(rightClickedSong)) {
            SongsListView.SelectedItem = rightClickedSong;
        }
    }

    /// <summary>
    /// Notifies the ViewModel when the ListView's selection has changed.
    /// </summary>
    private void OnListViewSelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (sender is ListView listView) {
            ViewModel.OnSongsSelectionChanged(listView.SelectedItems);
        }
    }
}