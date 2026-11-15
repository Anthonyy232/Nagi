using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nagi.WinUI.Styles;

namespace Nagi.WinUI.Controls;

/// <summary>
///     A list row that shows whether its song is the current one. <see cref="Helpers.NowPlayingIndicatorBinder" />
///     calls it on the template root of each realized row.
/// </summary>
public interface INowPlayingRow
{
    void SetNowPlaying(bool isCurrent, bool isPlaying);

    static void HighlightTitle(TextBlock title, bool isCurrent)
    {
        if (isCurrent) title.Foreground = (Brush)Application.Current.Resources[AppResourceKeys.AccentTextBrush];
        else title.ClearValue(TextBlock.ForegroundProperty);
    }
}
