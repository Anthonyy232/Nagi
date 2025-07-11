using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Models;
using Nagi.Navigation;
using Nagi.ViewModels;

namespace Nagi.Pages;

/// <summary>
///     A page that displays detailed information for a specific album, including its track list.
/// </summary>
public sealed partial class AlbumViewPage : Page {
    public AlbumViewPage() {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<AlbumViewViewModel>();
        DataContext = ViewModel;
    }

    public AlbumViewViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e) {
        base.OnNavigatedTo(e);

        if (e.Parameter is AlbumViewNavigationParameter navParam) {
            await ViewModel.LoadAlbumDetailsAsync(navParam.AlbumId);
            await ViewModel.LoadAvailablePlaylistsAsync();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) {
        base.OnNavigatedFrom(e);
        ViewModel.Cleanup();
    }

    private void SongsListView_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (sender is ListView listView)
            ViewModel.OnSongsSelectionChanged(listView.SelectedItems);
    }

    private void SongItemMenuFlyout_Opening(object sender, object e) {
        if (sender is not MenuFlyout menuFlyout) return;

        if (menuFlyout.Target?.DataContext is Song rightClickedSong &&
            !SongsListView.SelectedItems.Contains(rightClickedSong))
            SongsListView.SelectedItem = rightClickedSong;

        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name == "AddToPlaylistSubMenu") is { } addToPlaylistSubMenu)
            PopulatePlaylistSubMenu(addToPlaylistSubMenu);
    }

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