using Nagi.Presentation.Common;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Maps <see cref="AppIcon" /> to Segoe Fluent Icons glyphs.
/// </summary>
public static class AppIconGlyphs
{
    public static string ToGlyph(AppIcon icon) => icon switch
    {
        AppIcon.Library => "\uE8F1",
        AppIcon.Folders => "\uE8B7",
        AppIcon.Playlists => "\uE90B",
        AppIcon.Artists => "\uE77B",
        AppIcon.Albums => "\uE93C",
        AppIcon.Genres => "\uE8EC",
        AppIcon.Insights => "\uE9D2",
        AppIcon.Favorites => "\uEB51",
        AppIcon.Shuffle => "\uE8B1",
        AppIcon.Previous => "\uE892",
        AppIcon.Play => "\uE768",
        AppIcon.Pause => "\uE769",
        AppIcon.Next => "\uE893",
        AppIcon.RepeatOff => "\uF5E7",
        AppIcon.RepeatAll => "\uE8EE",
        AppIcon.RepeatOne => "\uE8ED",
        AppIcon.Lyrics => "\uED1E",
        AppIcon.Queue => "\uE90B",
        AppIcon.Mute => "\uE74F",
        AppIcon.VolumeLow => "\uE993",
        AppIcon.VolumeMedium => "\uE994",
        AppIcon.VolumeHigh => "\uE767",
        AppIcon.OnlineSource => "\uE774",
        AppIcon.History => "\uE81C",
        AppIcon.FullScreen => "\uE740",
        AppIcon.ExitFullScreen => "\uE73F",
        _ => string.Empty
    };
}
