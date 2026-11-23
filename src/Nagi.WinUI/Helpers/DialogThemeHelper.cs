using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.WinUI.Styles;

namespace Nagi.WinUI.Helpers;

internal static class DialogThemeHelper
{
    // The accent overrides are the theme resources in Styles/Colors.xaml. Dialogs get their own references to the
    // same brushes, so they look like pages and follow accent changes.
    private static readonly Lazy<IReadOnlyList<object>> AccentOverrideKeys = new(() =>
        Application.Current.Resources.MergedDictionaries
            .Single(dictionary => dictionary.Source?.AbsolutePath.EndsWith(AppResourceKeys.ColorsDictionaryPath, StringComparison.Ordinal) == true)
            .ThemeDictionaries["Default"] is ResourceDictionary overrides
            ? overrides.Keys.ToList()
            : []);

    public static void ApplyThemeOverrides(ContentDialog dialog, FrameworkElement? root)
    {
        if (root is not null) dialog.RequestedTheme = root.RequestedTheme;

        var appResources = Application.Current.Resources;
        foreach (var key in AccentOverrideKeys.Value) dialog.Resources[key] = appResources[key];
    }
}
