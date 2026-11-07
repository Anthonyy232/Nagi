using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;
using Nagi.Presentation.Helpers;
using Nagi.Presentation.Resources;

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
}
