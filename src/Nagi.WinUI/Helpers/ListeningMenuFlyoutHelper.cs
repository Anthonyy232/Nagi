using System.Linq;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;
using Nagi.Core.Models;
using Nagi.WinUI.Resources;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Helpers;

internal static class ListeningMenuFlyoutHelper
{
    public static string GetModeName(ShuffleMode mode) => mode switch
    {
        ShuffleMode.MoreVariety => Strings.Listening_MoreVariety,
        ShuffleMode.Rediscover => Strings.Listening_Rediscover,
        ShuffleMode.Unheard => Strings.Listening_Unheard,
        _ => Strings.Listening_Random
    };

    public static string GetModeDescription(ShuffleMode mode) => mode switch
    {
        ShuffleMode.MoreVariety => Strings.Listening_VarietyDescription,
        ShuffleMode.Rediscover => Strings.Listening_RediscoverDescription,
        ShuffleMode.Unheard => Strings.Listening_UnheardDescription,
        _ => string.Empty
    };

    public static string GetEmptyMixDescription(ShuffleMode mode, bool timed = false)
    {
        var description = GetModeDescription(mode);
        var guidance = timed ? Strings.Listening_NoTimedMixDescription : Strings.Listening_NoMixDescription;
        return description.Length > 0 ? $"{description}\n\n{guidance}" : guidance;
    }

    public static void PopulateMixes(MenuFlyout menu, ICommand command, ICommand playFor)
    {
        menu.Items.Clear();
        foreach (var mode in System.Enum.GetValues<ShuffleMode>().Where(mode => mode != ShuffleMode.Random))
        {
            var item = new MenuFlyoutItem { Text = GetModeName(mode), Command = command, CommandParameter = mode };
            var description = GetModeDescription(mode);
            if (description.Length > 0) ToolTipService.SetToolTip(item, description);
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = Strings.Listening_PlayFor, Command = playFor });
    }

    public static void PopulateFavorites(MenuFlyout menu, SongListViewModelBase viewModel)
    {
        foreach (var item in menu.Items.Where(i => i.Tag is "ListeningFavorites").ToArray()) menu.Items.Remove(item);
        if (viewModel.IsSingleSongSelected)
        {
            menu.Items.Insert(System.Math.Min(3, menu.Items.Count), new MenuFlyoutItem
            {
                Text = viewModel.SelectedSongs.FirstOrDefault()?.IsLoved == true ? Strings.Listening_RemoveFavorite : Strings.Listening_AddFavorite,
                Command = viewModel.ToggleSelectedFavoriteCommand,
                Tag = "ListeningFavorites"
            });
            return;
        }
        var submenu = new MenuFlyoutSubItem { Text = Strings.Listening_Favorites, Tag = "ListeningFavorites" };
        submenu.Items.Add(new MenuFlyoutItem { Text = Strings.Listening_AddFavorite, Command = viewModel.SetSelectedFavoritesCommand, CommandParameter = true });
        submenu.Items.Add(new MenuFlyoutItem { Text = Strings.Listening_RemoveFavorite, Command = viewModel.SetSelectedFavoritesCommand, CommandParameter = false });
        menu.Items.Insert(System.Math.Min(3, menu.Items.Count), submenu);
    }
}
