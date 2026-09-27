using System.Collections.Generic;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;
using Nagi.Core.Models;

namespace Nagi.WinUI.Helpers;

internal static class PlaylistMenuFlyoutHelper
{
    public static void Populate(MenuFlyoutSubItem menu, IEnumerable<Playlist> playlists,
        ICommand command, string emptyText)
    {
        menu.Items.Clear();
        foreach (var playlist in playlists)
            menu.Items.Add(new MenuFlyoutItem
            {
                Text = playlist.Name,
                Command = command,
                CommandParameter = playlist
            });

        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuFlyoutItem { Text = emptyText, IsEnabled = false });
    }
}
