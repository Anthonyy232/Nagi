using System.Collections.Generic;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;
using Nagi.Core.Models;
using Nagi.Presentation.Resources;

namespace Nagi.WinUI.Helpers;

internal static class PlaylistMenuFlyoutHelper
{
    public static void Populate(MenuFlyoutSubItem menu, IEnumerable<Playlist> playlists, ICommand command)
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
            menu.Items.Add(new MenuFlyoutItem { Text = Strings.Generic_NoPlaylists, IsEnabled = false });
    }
}
