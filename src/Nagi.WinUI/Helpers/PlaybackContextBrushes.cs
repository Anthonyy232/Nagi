using System.Collections.Generic;
using Microsoft.UI.Xaml.Media;
using Nagi.Core.Models;
using Nagi.Presentation.Helpers;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     The brush for each playback source's chart color on the Insights page.
///     Brushes are created on first use, which happens on the UI thread while binding.
/// </summary>
public static class PlaybackContextBrushes
{
    private static readonly Dictionary<PlaybackContextType, Brush> Brushes = [];

    public static Brush For(PlaybackContextType type)
    {
        if (!Brushes.TryGetValue(type, out var brush))
            Brushes[type] = brush = new SolidColorBrush(PlaybackContextColors.For(type).ToWindowsColor());
        return brush;
    }
}
