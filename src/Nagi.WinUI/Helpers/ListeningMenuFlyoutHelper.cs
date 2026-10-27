using System.Linq;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Resources;
using Nagi.Presentation.ViewModels;

namespace Nagi.WinUI.Helpers;

internal static class ListeningMenuFlyoutHelper
{
    public static void PopulateMixes(MenuFlyout menu, ICommand command, ICommand playFor)
    {
        menu.Items.Clear();
        foreach (var mode in ShuffleModeText.MixModes)
        {
            var item = new MenuFlyoutItem { Text = ShuffleModeText.GetName(mode), Command = command, CommandParameter = mode };
            var description = ShuffleModeText.GetDescription(mode);
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
                Text = viewModel.SelectedFavoriteActionText,
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
