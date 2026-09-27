using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Nagi.WinUI.Helpers;

public static class DialogThemeHelper
{
    public static void ApplyThemeOverrides(ContentDialog dialog)
    {
        var appResources = Application.Current.Resources;
        var accentBrush = (SolidColorBrush)appResources["AppPrimaryColorBrush"];

        // Share the XAML brush so dialogs use the same theme and live accent updates as pages.
        foreach (var key in new[] { "TextControlElevationBorderFocusedBrush", "TextControlBorderBrushFocused" })
            dialog.Resources[key] = appResources[key];

        foreach (var key in new[]
                 {
                     "TextControlSelectionHighlightColor",
                     "HyperlinkButtonForeground", "HyperlinkButtonForegroundPointerOver", "HyperlinkButtonForegroundPressed",
                     "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed",
                     "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundFillCheckedPressed",
                     "RadioButtonOuterEllipseCheckedFill", "RadioButtonOuterEllipseCheckedFillPointerOver", "RadioButtonOuterEllipseCheckedFillPressed",
                     "ComboBoxItemPillFillBrush", "ProgressBarForeground", "ProgressRingForegroundThemeBrush"
                 })
            dialog.Resources[key] = accentBrush;

        dialog.Resources["AccentButtonBackground"] = accentBrush;
        dialog.Resources["AccentButtonBackgroundPointerOver"] = new SolidColorBrush(accentBrush.Color) { Opacity = 0.9 };
        dialog.Resources["AccentButtonBackgroundPressed"] = new SolidColorBrush(accentBrush.Color) { Opacity = 0.8 };
    }
}
