using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Models;
using Nagi.Navigation;
using Nagi.ViewModels;

namespace Nagi.Pages;

/// <summary>
/// A page for displaying the list of songs within a specific playlist.
/// </summary>
public sealed partial class PlaylistSongViewPage : Page {
    /// <summary>
    /// Gets the view model associated with this page.
    /// </summary>
    public PlaylistSongListViewModel ViewModel { get; }

    public PlaylistSongViewPage() {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<PlaylistSongListViewModel>();
        DataContext = ViewModel;
    }

    /// <summary>
    /// Initializes the view model with navigation parameters when the page is navigated to.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e) {
        base.OnNavigatedTo(e);

        if (e.Parameter is PlaylistSongViewNavigationParameter navParam) {
            await ViewModel.InitializeAsync(navParam.Title, navParam.PlaylistId);
        }
        else {
            // Log a warning and initialize with a fallback state if navigation parameters are invalid.
            Debug.WriteLine($"[WARNING] {nameof(PlaylistSongViewPage)}: Received invalid navigation parameter. Type: {e.Parameter?.GetType().Name ?? "null"}");
            await ViewModel.InitializeAsync("Unknown Playlist", null);
        }
    }

    /// <summary>
    /// Cleans up resources when the user navigates away from this page.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e) {
        base.OnNavigatedFrom(e);
        ViewModel.Cleanup();
    }

    /// <summary>
    /// Updates the view model with the current selection from the song list.
    /// </summary>
    private void SongsListView_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (sender is ListView listView) {
            ViewModel.OnSongsSelectionChanged(listView.SelectedItems);
        }
    }

    /// <summary>
    /// Ensures the right-clicked song is selected before its context menu is opened.
    /// </summary>
    private void SongItemMenuFlyout_Opening(object sender, object e) {
        if (sender is not MenuFlyout { Target.DataContext: Song rightClickedSong }) return;

        // For a more intuitive user experience, if the right-clicked item is not already selected,
        // this clears the previous selection and selects only the right-clicked item.
        if (!SongsListView.SelectedItems.Contains(rightClickedSong)) {
            SongsListView.SelectedItem = rightClickedSong;
        }
    }
}