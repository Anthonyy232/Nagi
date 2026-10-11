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

    public static void PopulateMixes(MenuFlyout menu, ICommand command)
    {
        menu.Items.Clear();
        foreach (var mode in System.Enum.GetValues<ShuffleMode>())
        {
            var item = new MenuFlyoutItem { Text = GetModeName(mode), Command = command, CommandParameter = mode };
            var description = GetModeDescription(mode);
            if (description.Length > 0) ToolTipService.SetToolTip(item, description);
            menu.Items.Add(item);
        }
    }
}
