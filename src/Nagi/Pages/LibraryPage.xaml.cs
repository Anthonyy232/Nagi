// Nagi/Pages/LibraryPage.xaml.cs

using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Models;
using Nagi.ViewModels;

namespace Nagi.Pages;

/// <summary>
///     The page for displaying and interacting with the user's music library.
/// </summary>
public sealed partial class LibraryPage : Page
{
    public LibraryPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<LibraryViewModel>();
        DataContext = ViewModel;
    }

    public LibraryViewModel ViewModel { get; }

    /// <summary>
    ///     Loads necessary data when the page is navigated to.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Debug.WriteLine("[LibraryPage] Navigated to page. Loading data.");
        if (ViewModel != null)
        {
            //
            // Load playlists first for context menu availability.
            //
            await ViewModel.LoadAvailablePlaylistsAsync();
            //
            // This special method handles the initial UI load and a background rescan.
            //
            await ViewModel.InitializeAndStartBackgroundScanAsync();
        }
    }

    /// <summary>
    ///     Handles the opening of the context menu for a song item.
    /// </summary>
    private void SongItemMenuFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menuFlyout) return;

        //
        // Dynamically build the "Add to playlist" submenu.
        //
        var addToPlaylistSubMenu = menuFlyout.Items.OfType<MenuFlyoutSubItem>()
            .FirstOrDefault(item => item.Name == "AddToPlaylistSubMenu");
        if (addToPlaylistSubMenu != null)
        {
            addToPlaylistSubMenu.Items.Clear();
            if (ViewModel.AvailablePlaylists.Any())
                foreach (var playlist in ViewModel.AvailablePlaylists)
                {
                    var playlistMenuItem = new MenuFlyoutItem
                    {
                        Text = playlist.Name,
                        Command = ViewModel.AddSelectedSongsToPlaylistCommand,
                        CommandParameter = playlist
                    };
                    addToPlaylistSubMenu.Items.Add(playlistMenuItem);
                }
            else
                addToPlaylistSubMenu.Items.Add(
                    new MenuFlyoutItem { Text = "No playlists available", IsEnabled = false });
        }

        if (menuFlyout.Target?.DataContext is not Song rightClickedSong) return;

        //
        // If the user right-clicks an item that is not already selected,
        // change the selection to that single item for a better user experience.
        //
        if (!SongsListView.SelectedItems.Contains(rightClickedSong)) SongsListView.SelectedItem = rightClickedSong;
    }

    /// <summary>
    ///     Notifies the ViewModel when the ListView's selection has changed.
    /// </summary>
    private void OnListViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView listView) ViewModel.OnSongsSelectionChanged(listView.SelectedItems);
    }
}