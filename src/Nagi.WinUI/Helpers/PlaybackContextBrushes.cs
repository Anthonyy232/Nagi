using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Nagi.Core.Models;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     The chart colour for each playback source on the Insights page.
///     Brushes are created on first use, which happens on the UI thread while binding.
/// </summary>
public static class PlaybackContextBrushes
{
    private static readonly Dictionary<PlaybackContextType, Brush> Brushes = new()
    {
        [PlaybackContextType.Album] = new SolidColorBrush(ColorHelper.FromArgb(255, 239, 68, 68)),
        [PlaybackContextType.Artist] = new SolidColorBrush(ColorHelper.FromArgb(255, 249, 115, 22)),
        [PlaybackContextType.Playlist] = new SolidColorBrush(ColorHelper.FromArgb(255, 16, 185, 129)),
        [PlaybackContextType.SmartPlaylist] = new SolidColorBrush(ColorHelper.FromArgb(255, 6, 182, 212)),
        [PlaybackContextType.Folder] = new SolidColorBrush(ColorHelper.FromArgb(255, 139, 92, 246)),
        [PlaybackContextType.Genre] = new SolidColorBrush(ColorHelper.FromArgb(255, 236, 72, 153)),
        [PlaybackContextType.Search] = new SolidColorBrush(ColorHelper.FromArgb(255, 99, 102, 241)),
        [PlaybackContextType.Library] = new SolidColorBrush(ColorHelper.FromArgb(255, 245, 158, 11)),
        [PlaybackContextType.Transient] = new SolidColorBrush(ColorHelper.FromArgb(255, 107, 114, 128)),
    };

    private static readonly Brush DefaultBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 156, 163, 175));

    public static Brush For(PlaybackContextType type) =>
        Brushes.TryGetValue(type, out var brush) ? brush : DefaultBrush;
}
