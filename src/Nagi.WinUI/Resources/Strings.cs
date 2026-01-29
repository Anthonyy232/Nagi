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

    // App
    public static string App_Title => GetString("App_Title");


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

    // Themes
    public static string Theme_Light => GetString("Theme_Light");
    public static string Theme_Dark => GetString("Theme_Dark");
    public static string Theme_Default => GetString("Theme_Default");

    // Backdrop Materials
    public static string Backdrop_Mica => GetString("Backdrop_Mica");
    public static string Backdrop_MicaAlt => GetString("Backdrop_MicaAlt");
    public static string Backdrop_Acrylic => GetString("Backdrop_Acrylic");

    // Labels
    public static string Label_Composer => GetString("Label_Composer");
    public static string Label_Comment => GetString("Label_Comment");
    public static string Label_Rating => GetString("Label_Rating");

    // Operators
    public static string Operator_Contains => GetString("Operator_Contains");
    public static string Operator_DoesNotContain => GetString("Operator_DoesNotContain");
    public static string Operator_Is => GetString("Operator_Is");
    public static string Operator_IsNot => GetString("Operator_IsNot");
    public static string Operator_StartsWith => GetString("Operator_StartsWith");
    public static string Operator_EndsWith => GetString("Operator_EndsWith");
    public static string Operator_Equals => GetString("Operator_Equals");
    public static string Operator_NotEquals => GetString("Operator_NotEquals");
    public static string Operator_GreaterThan => GetString("Operator_GreaterThan");
    public static string Operator_LessThan => GetString("Operator_LessThan");
    public static string Operator_GreaterThanOrEqual => GetString("Operator_GreaterThanOrEqual");
    public static string Operator_LessThanOrEqual => GetString("Operator_LessThanOrEqual");
    public static string Operator_IsInTheLast => GetString("Operator_IsInTheLast");
    public static string Operator_IsNotInTheLast => GetString("Operator_IsNotInTheLast");
    public static string Operator_IsTrue => GetString("Operator_IsTrue");
    public static string Operator_IsFalse => GetString("Operator_IsFalse");

    // Smart Playlist
    public static string SmartPlaylist_Title_New => GetString("SmartPlaylist_Title_New");
    public static string SmartPlaylist_Title_EditFormat => GetString("SmartPlaylist_Title_EditFormat");
    public static string SmartPlaylist_Status_EnterName => GetString("SmartPlaylist_Status_EnterName");
    public static string SmartPlaylist_Status_MatchCountFormat => GetString("SmartPlaylist_Status_MatchCountFormat");
    public static string SmartPlaylist_Status_EnterNameToSeeSongs => GetString("SmartPlaylist_Status_EnterNameToSeeSongs");
    public static string SmartPlaylist_Status_Error => GetString("SmartPlaylist_Status_Error");
    public static string SmartPlaylist_Status_Calculating => GetString("SmartPlaylist_Status_Calculating");
    public static string SmartPlaylist_Error_DuplicateName => GetString("SmartPlaylist_Error_DuplicateName");
    public static string SmartPlaylist_Error_NameEmptyFeedback => GetString("SmartPlaylist_Error_NameEmptyFeedback");

    // Status
    public static string Status_Loading => GetString("Status_Loading");

    // Errors
    public static string Error_FailedToLoadArtists => GetString("Error_FailedToLoadArtists");

    // Crash Report
    public static string CrashReport_Title => GetString("CrashReport_Title");
    public static string CrashReport_Message => GetString("CrashReport_Message");

    // Elevation Warning
    public static string ElevationWarning_Title => GetString("ElevationWarning_Title");
    public static string ElevationWarning_Message => GetString("ElevationWarning_Message");
    public static string ElevationWarning_Restart => GetString("ElevationWarning_Restart");
    public static string ElevationWarning_Continue => GetString("ElevationWarning_Continue");

    // Mini Player
    public static string MiniPlayer_Title => GetString("MiniPlayer_Title");
}
