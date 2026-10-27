using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Nagi.Core.Models;
using Nagi.Presentation.Navigation;
using Nagi.Presentation.Resources;

namespace Nagi.WinUI.Helpers;

public static class ArtistMenuFlyoutHelper
{
    /// <summary>
    ///     Populates the given submenu with artists for the specified song.
    ///     If artists are already loaded in the song object, they are used immediately.
    ///     Otherwise, they are fetched asynchronously through <paramref name="loadArtists" />.
    /// </summary>
    public static void PopulateSubMenu(
        MenuFlyoutSubItem subMenu,
        Song song,
        ICommand goToArtistCommand,
        Func<Song, Task<IReadOnlyList<Artist>?>> loadArtists,
        DispatcherQueue dispatcherQueue)
    {
        subMenu.Items.Clear();

        var artists = song.GetCreditedArtists();
        if (artists.Count > 0)
        {
            foreach (var artist in artists)
            {
                AddArtistMenuItem(subMenu, song, artist.Name, goToArtistCommand);
            }
            return;
        }

        var loadingItem = new MenuFlyoutItem { Text = Strings.Status_Loading, IsEnabled = false };
        subMenu.Items.Add(loadingItem);

        _ = PopulateArtistsAsync(subMenu, song, loadingItem, goToArtistCommand, loadArtists, dispatcherQueue);
    }

    private static async Task PopulateArtistsAsync(
        MenuFlyoutSubItem subMenu,
        Song song,
        MenuFlyoutItem loadingItem,
        ICommand goToArtistCommand,
        Func<Song, Task<IReadOnlyList<Artist>?>> loadArtists,
        DispatcherQueue dispatcherQueue)
    {
        var artists = await loadArtists(song).ConfigureAwait(false);

        dispatcherQueue.TryEnqueue(() =>
        {
            if (!subMenu.Items.Contains(loadingItem)) return;
            if (artists is null)
            {
                loadingItem.Text = Strings.Error_FailedToLoadArtists;
                return;
            }

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
