// Nagi/Pages/AlbumViewPage.xaml.cs

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
public sealed partial class AlbumViewPage : Page
{
    public AlbumViewPage()
    {
        InitializeComponent();
        // Resolve the ViewModel from the dependency injection container.
        ViewModel = App.Services.GetRequiredService<AlbumViewViewModel>();
        DataContext = ViewModel;
    }

    public AlbumViewViewModel ViewModel { get; }

    /// <summary>
    ///     Invoked when the page is navigated to.
    /// </summary>
    /// <param name="e">Event data that contains the navigation parameter.</param>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is AlbumViewNavigationParameter navParam)
        {
            // Asynchronously load album details and the list of available playlists.
            await ViewModel.LoadAlbumDetailsAsync(navParam.AlbumId);
            await ViewModel.LoadAvailablePlaylistsAsync();
        }
    }

    /// <summary>
    ///     Handles the selection changed event for the songs ListView.
    /// </summary>
    private void SongsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView listView)
            // Update the ViewModel with the current selection.
            ViewModel.OnSongsSelectionChanged(listView.SelectedItems);
    }

    /// <summary>
    ///     Handles the opening of a song item's context menu.
    /// </summary>
    private void SongItemMenuFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menuFlyout) return;

        // Ensure the item that was right-clicked is part of the selection.
        if (menuFlyout.Target?.DataContext is Song rightClickedSong &&
            !SongsListView.SelectedItems.Contains(rightClickedSong))
            SongsListView.SelectedItem = rightClickedSong;

        // Find the "Add to playlist" submenu and populate it.
        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name == "AddToPlaylistSubMenu") is { } addToPlaylistSubMenu)
            PopulatePlaylistSubMenu(addToPlaylistSubMenu);
    }

    /// <summary>
    ///     Populates the "Add to playlist" submenu with playlists from the ViewModel.
    /// </summary>
    /// <param name="subMenu">The submenu to populate.</param>
    private void PopulatePlaylistSubMenu(MenuFlyoutSubItem subMenu)
    {
        subMenu.Items.Clear();

        if (ViewModel.AvailablePlaylists.Any())
        {
            foreach (var playlist in ViewModel.AvailablePlaylists)
            {
                var playlistMenuItem = new MenuFlyoutItem
                {
                    Text = playlist.Name,
                    Command = ViewModel.AddSelectedSongsToPlaylistCommand,
                    CommandParameter = playlist
                };
                subMenu.Items.Add(playlistMenuItem);
            }
        }
        else
        {
            // Display a disabled item if no playlists are available.
            var disabledItem = new MenuFlyoutItem { Text = "No playlists available", IsEnabled = false };
            subMenu.Items.Add(disabledItem);
        }
    }
}