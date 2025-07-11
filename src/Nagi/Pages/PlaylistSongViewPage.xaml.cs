using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Models;
using Nagi.Navigation;
using Nagi.ViewModels;

namespace Nagi.Pages;

/// <summary>
///     A page for displaying the list of songs within a specific playlist.
/// </summary>
public sealed partial class PlaylistSongViewPage : Page {
    public PlaylistSongViewPage() {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<PlaylistSongListViewModel>();
        DataContext = ViewModel;
    }

    /// <summary>
    ///     Gets the ViewModel for this page.
    /// </summary>
    public PlaylistSongListViewModel ViewModel { get; }

    /// <summary>
    ///     Initializes the ViewModel with navigation parameters when the page is navigated to.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e) {
        base.OnNavigatedTo(e);
        if (e.Parameter is PlaylistSongViewNavigationParameter navParam) {
            await ViewModel.InitializeAsync(navParam.Title, navParam.PlaylistId);
        }
        else {
            // Fallback if navigation parameter is missing or incorrect.
            Debug.WriteLine(
                "[WARNING] PlaylistSongViewPage: OnNavigatedTo received invalid or missing navigation parameter.");
            await ViewModel.InitializeAsync("Unknown Playlist", null);
        }
    }

    /// <summary>
    ///     Handles the SelectionChanged event of the song list to update the ViewModel's selected items.
    /// </summary>
    private void SongsListView_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (sender is ListView listView) ViewModel.OnSongsSelectionChanged(listView.SelectedItems);
    }

    // FIX: This method was missing, causing the CS1061 build error. It has been restored.
    /// <summary>
    ///     Ensures the right-clicked song is selected before its context menu is opened.
    ///     This provides a better user experience by making the context menu operate on the
    ///     item under the cursor, rather than the previously selected items.
    /// </summary>
    private void SongItemMenuFlyout_Opening(object sender, object e) {
        if (sender is not MenuFlyout menuFlyout || menuFlyout.Target?.DataContext is not Song rightClickedSong) return;

        // If the item being right-clicked is not already in the selection,
        // clear the current selection and select only the right-clicked item.
        if (!SongsListView.SelectedItems.Contains(rightClickedSong)) SongsListView.SelectedItem = rightClickedSong;
    }
}