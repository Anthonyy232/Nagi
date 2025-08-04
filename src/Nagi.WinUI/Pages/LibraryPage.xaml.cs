using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Core.Models;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Pages;

/// <summary>
///     The page for displaying and interacting with the user's music library.
/// </summary>
public sealed partial class LibraryPage : Page
{
    public LibraryPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<LibraryViewModel>();
        DataContext = ViewModel;
    }

    /// <summary>
    ///     Gets the ViewModel associated with this page.
    /// </summary>
    public LibraryViewModel ViewModel { get; }

    /// <summary>
    ///     Loads necessary data when the page is navigated to.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (ViewModel != null)
        {
            // Load playlists first for context menu availability.
            await ViewModel.LoadAvailablePlaylistsAsync();

            // This method handles the initial UI load and a background rescan.
            await ViewModel.InitializeAsync();
        }
    }

    /// <summary>
    ///     Handles the opening of the context menu for a song item. It dynamically populates
    ///     the "Add to playlist" submenu and ensures the right-clicked item is selected.
    /// </summary>
    private void SongItemMenuFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menuFlyout) return;

        // Dynamically build the "Add to playlist" submenu.
        if (menuFlyout.Items.OfType<MenuFlyoutSubItem>()
                .FirstOrDefault(item => item.Name == "AddToPlaylistSubMenu") is { } addToPlaylistSubMenu)
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

        // If the user right-clicks an item that is not already selected,
        // change the selection to that single item for a better user experience.
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