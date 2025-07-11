using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Models;
using Nagi.Navigation;
using Nagi.ViewModels;

namespace Nagi.Pages;

/// <summary>
/// A page for displaying the list of songs within a specific folder.
/// </summary>
public sealed partial class FolderSongViewPage : Page {
    public FolderSongViewPage() {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<FolderSongListViewModel>();
        DataContext = ViewModel;
    }

    /// <summary>
    /// Gets the ViewModel associated with this page.
    /// </summary>
    public FolderSongListViewModel ViewModel { get; }

    /// <summary>
    /// Initializes the ViewModel with navigation parameters when the page is navigated to.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e) {
        base.OnNavigatedTo(e);
        if (e.Parameter is FolderSongViewNavigationParameter navParam) {
            await ViewModel.InitializeAsync(navParam.Title, navParam.FolderId);
            await ViewModel.LoadAvailablePlaylistsAsync();
        }
        else {
            Debug.WriteLine("[WARNING] FolderSongViewPage: OnNavigatedTo received invalid navigation parameter.");
            await ViewModel.InitializeAsync("Unknown Folder", null);
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
    /// Handles the SelectionChanged event of the ListView to update the ViewModel.
    /// </summary>
    private void SongsListView_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (sender is ListView listView) {
            ViewModel.OnSongsSelectionChanged(listView.SelectedItems);
        }
    }

    /// <summary>
    /// Handles the opening of the context menu for a song item, populating submenus
    /// and ensuring the correct item is selected.
    /// </summary>
    private void SongItemMenuFlyout_Opening(object sender, object e) {
        if (sender is not MenuFlyout menuFlyout) return;

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

        if (!SongsListView.SelectedItems.Contains(rightClickedSong)) {
            SongsListView.SelectedItem = rightClickedSong;
        }
    }
}