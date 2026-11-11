using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Nagi.WinUI.Controls;

/// <summary>
///     The root of a row template whose title, marked with <see cref="IsTitleProperty" />, turns the accent color
///     while the row's song is the current one.
/// </summary>
public sealed class NowPlayingGrid : Grid, INowPlayingRow
{
    public static readonly DependencyProperty IsTitleProperty = DependencyProperty.RegisterAttached(
        "IsTitle", typeof(bool), typeof(NowPlayingGrid), new PropertyMetadata(false));

    public static bool GetIsTitle(TextBlock element) => (bool)element.GetValue(IsTitleProperty);

    public static void SetIsTitle(TextBlock element, bool value) => element.SetValue(IsTitleProperty, value);

    public void SetNowPlaying(bool isCurrent, bool isPlaying)
    {
        foreach (var title in this.FindDescendants().OfType<TextBlock>().Where(GetIsTitle))
            INowPlayingRow.HighlightTitle(title, isCurrent);
    }
}
