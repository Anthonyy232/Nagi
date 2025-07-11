using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Models;
using Nagi.Navigation;
using Nagi.ViewModels;

namespace Nagi.Pages;

/// <summary>
/// A page that displays detailed information for a specific artist,
/// including their albums and songs.
/// </summary>
public sealed partial class ArtistViewPage : Page {
    public ArtistViewPage() {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<ArtistViewViewModel>();
        DataContext = ViewModel;
    }

    /// <summary>
    /// Gets the ViewModel associated with this page.
    /// </summary>
    public ArtistViewViewModel ViewModel { get; }

    /// <summary>
    /// Initializes the ViewModel when the page is navigated to.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e) {
        base.OnNavigatedTo(e);

        if (e.Parameter is ArtistViewNavigationParameter navParam) {
            await ViewModel.LoadArtistDetailsAsync(navParam.ArtistId);
            await ViewModel.LoadAvailablePlaylistsAsync();
        }
    }

    /// <summary>
    /// Cleans up resources when the user navigates away from the page.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e) {
        base.OnNavigatedFrom(e);
        ViewModel.Cleanup();
    }

    /// <summary>
    /// Handles clicks on the album grid, navigating to the selected album.
    /// </summary>
    private void AlbumGridView_ItemClick(object sender, ItemClickEventArgs e) {
        if (e.ClickedItem is ArtistAlbumViewModelItem clickedAlbum) {
            ViewModel.ViewAlbumCommand.Execute(clickedAlbum.Id);
        }
    }

    /// <summary>
    /// Updates the ViewModel's selection when the song list selection changes.
    /// </summary>
    private void SongsListView_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (sender is ListView listView) {
            ViewModel.OnSongsSelectionChanged(listView.SelectedItems);
        }
    }

    /// <summary>
    /// Handles the opening of the context menu for a song item.
    /// </summary>
    private void SongItemMenuFlyout_Opening(object sender, object e) {
        if (sender is not MenuFlyout menuFlyout) return;

        // If the user right-clicks a song that is not already selected,
        // make it the only selected item.
        if (menuFlyout.Target?.DataContext is Song rightClickedSong &&
            !SongsListView.SelectedItems.Contains(rightClickedSong)) {
            SongsListView.SelectedItem = rightClickedSong;
        }

        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name == "AddToPlaylistSubMenu") is { } addToPlaylistSubMenu) {
            PopulatePlaylistSubMenu(addToPlaylistSubMenu);
        }
    }

    /// <summary>
    /// Populates the "Add to playlist" submenu with available playlists.
    /// </summary>
    private void PopulatePlaylistSubMenu(MenuFlyoutSubItem subMenu) {
        subMenu.Items.Clear();

        if (ViewModel.AvailablePlaylists.Any()) {
            foreach (var playlist in ViewModel.AvailablePlaylists) {
                var playlistMenuItem = new MenuFlyoutItem {
                    Text = playlist.Name,
                    Command = ViewModel.AddSelectedSongsToPlaylistCommand,
                    CommandParameter = playlist
                };
                subMenu.Items.Add(playlistMenuItem);
            }
        }
        else {
            var disabledItem = new MenuFlyoutItem { Text = "No playlists available", IsEnabled = false };
            subMenu.Items.Add(disabledItem);
        }
    }
}