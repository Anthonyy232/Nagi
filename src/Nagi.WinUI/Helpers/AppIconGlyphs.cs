using Nagi.Presentation.Models;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Maps <see cref="AppIcon" /> to Segoe Fluent Icons glyphs.
/// </summary>
public static class AppIconGlyphs
{
    public static string ToGlyph(AppIcon icon) => icon switch
    {
        AppIcon.Library => "",
        AppIcon.Folders => "",
        AppIcon.Playlists => "",
        AppIcon.Artists => "",
        AppIcon.Albums => "",
        AppIcon.Genres => "",
        AppIcon.Insights => "",
        AppIcon.Favorites => "",
        AppIcon.Shuffle => "",
        AppIcon.Previous => "",
        AppIcon.Play => "",
        AppIcon.Pause => "",
        AppIcon.Next => "",
        AppIcon.RepeatOff => "",
        AppIcon.RepeatAll => "",
        AppIcon.RepeatOne => "",
        AppIcon.Lyrics => "",
        AppIcon.Queue => "",
        AppIcon.Mute => "",
        AppIcon.VolumeLow => "",
        AppIcon.VolumeMedium => "",
        AppIcon.VolumeHigh => "",
        _ => string.Empty
    };
}
