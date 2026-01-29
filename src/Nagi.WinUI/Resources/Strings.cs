using System.Resources;
using System.Reflection;

namespace Nagi.WinUI.Resources;

/// <summary>
///     Provides thread-safe access to the localized strings for the Nagi.WinUI project.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager _resourceManager =
        new("Nagi.WinUI.Resources.Strings", typeof(Strings).Assembly);

    /// <summary>
    ///     Returns the localized string for the specified key, or the key itself if not found.
    /// </summary>
    public static string GetString(string name)
    {
        return _resourceManager.GetString(name) ?? name;
    }

    // Navigation View Items
    public static string NavItem_Library => GetString("NavItem_Library");
    public static string NavItem_Albums => GetString("NavItem_Albums");
    public static string NavItem_Artists => GetString("NavItem_Artists");
    public static string NavItem_Genres => GetString("NavItem_Genres");
    public static string NavItem_Playlists => GetString("NavItem_Playlists");
    public static string NavItem_Folders => GetString("NavItem_Folders");
    public static string NavItem_Settings => GetString("NavItem_Settings");

    // Player Status
    public static string Status_NoTrackPlaying => GetString("Status_NoTrackPlaying");

    // Player Tooltips
    public static string Tooltip_Play => GetString("Tooltip_Play");
    public static string Tooltip_Pause => GetString("Tooltip_Pause");
    public static string Tooltip_ShuffleOn => GetString("Tooltip_ShuffleOn");
    public static string Tooltip_ShuffleOff => GetString("Tooltip_ShuffleOff");
    public static string Tooltip_RepeatOff => GetString("Tooltip_RepeatOff");
    public static string Tooltip_RepeatAll => GetString("Tooltip_RepeatAll");
    public static string Tooltip_RepeatOne => GetString("Tooltip_RepeatOne");
    public static string Tooltip_Repeat => GetString("Tooltip_Repeat");
    public static string Tooltip_Unmute => GetString("Tooltip_Unmute");
    public static string Tooltip_Mute => GetString("Tooltip_Mute");
}
