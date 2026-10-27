using Nagi.Core.Models;
using Nagi.Presentation.Models;

namespace Nagi.Presentation.Helpers;

/// <summary>
///     The chart color for each playback source on the Insights page.
/// </summary>
public static class PlaybackContextColors
{
    public static ArgbColor For(PlaybackContextType type) => type switch
    {
        PlaybackContextType.Album => ArgbColor.FromRgb(239, 68, 68),
        PlaybackContextType.Artist => ArgbColor.FromRgb(249, 115, 22),
        PlaybackContextType.Playlist => ArgbColor.FromRgb(16, 185, 129),
        PlaybackContextType.SmartPlaylist => ArgbColor.FromRgb(6, 182, 212),
        PlaybackContextType.Folder => ArgbColor.FromRgb(139, 92, 246),
        PlaybackContextType.Genre => ArgbColor.FromRgb(236, 72, 153),
        PlaybackContextType.Search => ArgbColor.FromRgb(99, 102, 241),
        PlaybackContextType.Library => ArgbColor.FromRgb(245, 158, 11),
        PlaybackContextType.Transient => ArgbColor.FromRgb(107, 114, 128),
        _ => ArgbColor.FromRgb(156, 163, 175)
    };
}
