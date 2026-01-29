using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Navigation;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Helper class to populate the "Go to artist" submenu in a consistent way across different pages.
/// </summary>
public static class ArtistMenuFlyoutHelper
{
    /// <summary>
    ///     Populates the given submenu with artists for the specified song.
    ///     If artists are already loaded in the song object, they are used immediately.
    ///     Otherwise, they are fetched asynchronously using the provided ILibraryReader.
    /// </summary>
    /// <param name="subMenu">The menu flyout sub-item to populate.</param>
    /// <param name="song">The song to get artists for.</param>
    /// <param name="goToArtistCommand">The command to execute when an artist is clicked.</param>
    /// <param name="libraryReader">The service to fetch artists if needed.</param>
    /// <param name="dispatcherQueue">The dispatcher queue to update UI from background threads.</param>
    /// <param name="logger">The logger for error reporting.</param>
    public static void PopulateSubMenu(
        MenuFlyoutSubItem subMenu,
        Song song,
        ICommand goToArtistCommand,
        ILibraryReader libraryReader,
        DispatcherQueue dispatcherQueue,
        ILogger logger)
    {
        subMenu.Items.Clear();

        // 1. Use pre-loaded artists if available
        if (song.SongArtists != null && song.SongArtists.Count > 0)
        {
            var artists = song.SongArtists.OrderBy(sa => sa.Order).Select(sa => sa.Artist).ToList();
            foreach (var artist in artists)
            {
                if (artist == null) continue;
                AddArtistMenuItem(subMenu, song, artist.Name, goToArtistCommand);
            }
            return;
        }

        // 2. Show loading state and fetch asynchronously
        var loadingItem = new MenuFlyoutItem { Text = Nagi.WinUI.Resources.Strings.Status_Loading, IsEnabled = false };
        subMenu.Items.Add(loadingItem);

        // Fire-and-forget async population
        _ = PopulateArtistsAsync(subMenu, song, loadingItem, goToArtistCommand, libraryReader, dispatcherQueue, logger);
    }

    private static async Task PopulateArtistsAsync(
        MenuFlyoutSubItem subMenu,
        Song song,
        MenuFlyoutItem loadingItem,
        ICommand goToArtistCommand,
        ILibraryReader libraryReader,
        DispatcherQueue dispatcherQueue,
        ILogger logger)
    {
        try
        {
            var artists = (await libraryReader.GetArtistsForSongAsync(song.Id).ConfigureAwait(false)).ToList();

            dispatcherQueue.TryEnqueue(() =>
            {
                if (!subMenu.Items.Contains(loadingItem)) return;
                subMenu.Items.Remove(loadingItem);

                if (artists.Count == 0)
                {
                    subMenu.Items.Add(new MenuFlyoutItem { Text = Artist.UnknownArtistName, IsEnabled = false });
                    return;
                }

                foreach (var artist in artists)
                {
                    AddArtistMenuItem(subMenu, song, artist.Name, goToArtistCommand);
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to populate artist submenu for song {SongId}", song.Id);
            dispatcherQueue.TryEnqueue(() =>
            {
                if (subMenu.Items.Contains(loadingItem)) loadingItem.Text = Nagi.WinUI.Resources.Strings.Error_FailedToLoadArtists;
            });
        }
    }

    private static void AddArtistMenuItem(
        MenuFlyoutSubItem subMenu,
        Song song,
        string artistName,
        ICommand goToArtistCommand)
    {
        subMenu.Items.Add(new MenuFlyoutItem
        {
            Text = artistName,
            Command = goToArtistCommand,
            CommandParameter = new ArtistNavigationRequest { Song = song, ArtistName = artistName }
        });
    }
}
