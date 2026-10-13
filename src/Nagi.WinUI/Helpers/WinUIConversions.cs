using Microsoft.UI.Xaml;
using Nagi.Presentation.Models;
using Windows.UI;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Maps the app's platform-neutral theme and color types to their WinUI counterparts.
/// </summary>
internal static class WinUIConversions
{
    internal static ElementTheme ToElementTheme(this AppTheme theme) => theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    internal static Color ToWindowsColor(this ArgbColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    internal static ArgbColor ToArgbColor(this Color color) => new(color.A, color.R, color.G, color.B);
}
