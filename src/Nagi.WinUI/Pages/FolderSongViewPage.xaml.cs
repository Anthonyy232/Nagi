using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Core.Models;
using Nagi.WinUI.Navigation;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page for displaying the list of songs within a specific folder.
/// </summary>
public sealed partial class FolderSongViewPage : Page
{
    public FolderSongViewPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<FolderSongListViewModel>();
        DataContext = ViewModel;
    }

    /// <summary>
    ///     Gets the view model associated with this page.
    /// </summary>
    public FolderSongListViewModel ViewModel { get; }

    /// <summary>
    ///     Initializes the view model with navigation parameters when the page is navigated to.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is FolderSongViewNavigationParameter navParam)
        {
            await ViewModel.InitializeAsync(navParam.Title, navParam.FolderId);
            await ViewModel.LoadAvailablePlaylistsAsync();
        }
        else
        {
            // Log a warning and initialize with a fallback state if navigation parameters are invalid.
            Debug.WriteLine(
                $"[WARNING] {nameof(FolderSongViewPage)}: Received invalid navigation parameter. Type: {e.Parameter?.GetType().Name ?? "null"}");
            await ViewModel.InitializeAsync("Unknown Folder", null);
        }
    }

    /// <summary>
    ///     Cleans up resources when the user navigates away from this page.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Cleanup();
    }

    /// <summary>
    ///     Updates the view model with the current selection from the song list.
    /// </summary>
    private void SongsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView listView) ViewModel.OnSongsSelectionChanged(listView.SelectedItems);
    }

    /// <summary>
    ///     Handles the opening of the context menu for a song item.
    /// </summary>
    private void SongItemMenuFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menuFlyout) return;

        // Ensure the right-clicked song is selected before showing the context menu.
        if (menuFlyout.Target?.DataContext is Song rightClickedSong &&
            !SongsListView.SelectedItems.Contains(rightClickedSong))
            SongsListView.SelectedItem = rightClickedSong;

        // Find and populate the "Add to Playlist" submenu.
        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name == "AddToPlaylistSubMenu") is { } addToPlaylistSubMenu)
            PopulatePlaylistSubMenu(addToPlaylistSubMenu);
    }

    /// <summary>
    ///     Populates the "Add to playlist" submenu with available playlists from the view model.
    /// </summary>
    private void PopulatePlaylistSubMenu(MenuFlyoutSubItem subMenu)
    {
        subMenu.Items.Clear();

        var availablePlaylists = ViewModel.AvailablePlaylists;

        if (availablePlaylists?.Any() != true)
        {
            // Display a disabled item if there are no playlists to add the song to.
            subMenu.Items.Add(new MenuFlyoutItem { Text = "No playlists available", IsEnabled = false });
            return;
        }

        foreach (var playlist in availablePlaylists)
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
}