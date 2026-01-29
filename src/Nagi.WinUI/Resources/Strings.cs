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

    // ViewModels - Album View
    public static string AlbumView_AlbumNotFound => GetString("AlbumView_AlbumNotFound");
    public static string AlbumView_Error => GetString("AlbumView_Error");
    public static string AlbumView_DefaultAlbumTitle => GetString("AlbumView_DefaultAlbumTitle");
    public static string AlbumView_DefaultArtistName => GetString("AlbumView_DefaultArtistName");
    public static string AlbumView_SongCount_Singular => GetString("AlbumView_SongCount_Singular");
    public static string AlbumView_SongCount_Plural => GetString("AlbumView_SongCount_Plural");
    public static string AlbumView_DiscHeader => GetString("AlbumView_DiscHeader");


    // ViewModels - Artist View
    public static string ArtistView_ArtistNotFound => GetString("ArtistView_ArtistNotFound");
    public static string ArtistView_Error => GetString("ArtistView_Error");
    public static string ArtistView_DefaultArtistName => GetString("ArtistView_DefaultArtistName");
    public static string ArtistView_NoBiography => GetString("ArtistView_NoBiography");
    public static string ArtistView_ErrorLoadingDetails => GetString("ArtistView_ErrorLoadingDetails");

    // ViewModels - Folders
    public static string Folders_ErrorLoading => GetString("Folders_ErrorLoading");
    public static string Folders_AddFolder_Success => GetString("Folders_AddFolder_Success");
    public static string Folders_AddFolder_Failed => GetString("Folders_AddFolder_Failed");
    public static string Folders_AddFolder_Exists => GetString("Folders_AddFolder_Exists");
    public static string Folders_AddFolder_InProgress => GetString("Folders_AddFolder_InProgress");
    public static string Folders_Delete_Success => GetString("Folders_Delete_Success");
    public static string Folders_Delete_Failed => GetString("Folders_Delete_Failed");
    public static string Folders_Delete_InProgress => GetString("Folders_Delete_InProgress");
    public static string Folders_Rescan_Complete => GetString("Folders_Rescan_Complete");
    public static string Folders_Rescan_NoChanges => GetString("Folders_Rescan_NoChanges");
    public static string Folders_Rescan_InProgress => GetString("Folders_Rescan_InProgress");
    public static string Folders_Empty_NoMusicFound => GetString("Folders_Empty_NoMusicFound");
    public static string Folders_Error_Playback => GetString("Folders_Error_Playback");
    public static string Folders_Error_RandomPlayback => GetString("Folders_Error_RandomPlayback");
    public static string Folders_Error_AddFolder => GetString("Folders_Error_AddFolder");
    public static string Folders_Error_DeleteFolder => GetString("Folders_Error_DeleteFolder");
    public static string Folders_Error_RescanFolder => GetString("Folders_Error_RescanFolder");
    public static string Folders_Count_Singular => GetString("Folders_Count_Singular");

    public static string Folders_Count_Plural => GetString("Folders_Count_Plural");

    // ViewModels - Generic & Other
    public static string Songs_Count_Singular => GetString("Songs_Count_Singular");
    public static string Songs_Count_Plural => GetString("Songs_Count_Plural");
    public static string Albums_Count_Singular => GetString("Albums_Count_Singular");
    public static string Albums_Count_Plural => GetString("Albums_Count_Plural");
    public static string Artists_Count_Singular => GetString("Artists_Count_Singular");
    public static string Artists_Count_Plural => GetString("Artists_Count_Plural");
    public static string Generic_NoItems => GetString("Generic_NoItems");
    public static string Generic_Error => GetString("Generic_Error");
    public static string GenreView_DefaultName => GetString("GenreView_DefaultName");
    public static string GenreView_Error => GetString("GenreView_Error");

    // Lyrics
    public static string Lyrics_NoSongSelected => GetString("Lyrics_NoSongSelected");
    public static string Lyrics_SongTitleFormat => GetString("Lyrics_SongTitleFormat");

    // Onboarding
    public static string Onboarding_Welcome => GetString("Onboarding_Welcome");
    public static string Onboarding_WaitingForSelection => GetString("Onboarding_WaitingForSelection");
    public static string Onboarding_BuildingLibrary => GetString("Onboarding_BuildingLibrary");
    public static string Onboarding_Error => GetString("Onboarding_Error");

    // Playlist
    public static string Playlist_Smart_Count_Singular => GetString("Playlist_Smart_Count_Singular");
    public static string Playlist_Smart_Count_Plural => GetString("Playlist_Smart_Count_Plural");
    public static string Playlist_Count_Singular => GetString("Playlist_Count_Singular");
    public static string Playlist_Count_Plural => GetString("Playlist_Count_Plural");
    public static string Playlist_ErrorLoading => GetString("Playlist_ErrorLoading");
    public static string Playlist_Status_Starting => GetString("Playlist_Status_Starting");
    public static string Playlist_Status_SmartEmpty => GetString("Playlist_Status_SmartEmpty");
    public static string Playlist_Status_ErrorPlayback => GetString("Playlist_Status_ErrorPlayback");
    public static string Playlist_Status_PickingRandom => GetString("Playlist_Status_PickingRandom");
    public static string Playlist_Status_NoPlaylists => GetString("Playlist_Status_NoPlaylists");
    public static string Playlist_Status_SmartSelectedEmpty => GetString("Playlist_Status_SmartSelectedEmpty");
    public static string Playlist_Status_ErrorRandom => GetString("Playlist_Status_ErrorRandom");
    public static string Playlist_Status_Loading => GetString("Playlist_Status_Loading");
    public static string Playlist_Status_ErrorLoadingPlaylists => GetString("Playlist_Status_ErrorLoadingPlaylists");
    public static string Playlist_Status_Creating => GetString("Playlist_Status_Creating");
    public static string Playlist_Status_CreateFailed => GetString("Playlist_Status_CreateFailed");
    public static string Playlist_Status_CreateError => GetString("Playlist_Status_CreateError");
    public static string Playlist_Status_UpdatingCover => GetString("Playlist_Status_UpdatingCover");
    public static string Playlist_Status_UpdateCoverFailed => GetString("Playlist_Status_UpdateCoverFailed");
    public static string Playlist_Status_UpdateCoverError => GetString("Playlist_Status_UpdateCoverError");
    public static string Playlist_Status_RemovingCover => GetString("Playlist_Status_RemovingCover");
    public static string Playlist_Status_RemoveCoverFailed => GetString("Playlist_Status_RemoveCoverFailed");
    public static string Playlist_Status_RemoveCoverError => GetString("Playlist_Status_RemoveCoverError");
    public static string Playlist_Status_Renaming => GetString("Playlist_Status_Renaming");
    public static string Playlist_Status_RenameFailed => GetString("Playlist_Status_RenameFailed");
    public static string Playlist_Status_RenameError => GetString("Playlist_Status_RenameError");
    public static string Playlist_Status_Deleting => GetString("Playlist_Status_Deleting");
    public static string Playlist_Status_DeleteFailed => GetString("Playlist_Status_DeleteFailed");
    public static string Playlist_Status_DeleteError => GetString("Playlist_Status_DeleteError");

    // Settings
    public static string Settings_Reset_Title => GetString("Settings_Reset_Title");
    public static string Settings_Reset_Message => GetString("Settings_Reset_Message");
    public static string Settings_Reset_Button => GetString("Settings_Reset_Button");
    public static string Settings_ResetError_Title => GetString("Settings_ResetError_Title");
    public static string Settings_ResetError_Message => GetString("Settings_ResetError_Message");
    public static string Settings_LastFm_AuthError_Title => GetString("Settings_LastFm_AuthError_Title");
    public static string Settings_LastFm_AuthError_Message => GetString("Settings_LastFm_AuthError_Message");
    public static string Settings_LastFm_FinalizeError_Title => GetString("Settings_LastFm_FinalizeError_Title");
    public static string Settings_LastFm_FinalizeError_Message => GetString("Settings_LastFm_FinalizeError_Message");
    public static string Settings_LastFm_Disconnect_Title => GetString("Settings_LastFm_Disconnect_Title");
    public static string Settings_LastFm_Disconnect_Message => GetString("Settings_LastFm_Disconnect_Message");
    public static string Settings_LastFm_Disconnect_Button => GetString("Settings_LastFm_Disconnect_Button");
    public static string Settings_Export_Success_Title => GetString("Settings_Export_Success_Title");
    public static string Settings_Export_Success_Message => GetString("Settings_Export_Success_Message");
    public static string Settings_Export_Failed_Title => GetString("Settings_Export_Failed_Title");
    public static string Settings_Import_Success_Title => GetString("Settings_Import_Success_Title");
    public static string Settings_Import_Success_Message => GetString("Settings_Import_Success_Message");
    public static string Settings_Import_Unmatched_Message => GetString("Settings_Import_Unmatched_Message");
    public static string Settings_Import_FailedFiles_Message => GetString("Settings_Import_FailedFiles_Message");
    public static string Settings_Import_FailedTotal_Title => GetString("Settings_Import_FailedTotal_Title");
    public static string Settings_Import_FailedTotal_Message => GetString("Settings_Import_FailedTotal_Message");

    // Smart Playlist
    public static string SmartPlaylist_ErrorLoading => GetString("SmartPlaylist_ErrorLoading");
    public static string SmartPlaylist_RuleSummary_NoRules => GetString("SmartPlaylist_RuleSummary_NoRules");
    public static string SmartPlaylist_RuleSummary_Format => GetString("SmartPlaylist_RuleSummary_Format");
    public static string SmartPlaylist_MatchAll => GetString("SmartPlaylist_MatchAll");
    public static string SmartPlaylist_MatchAny => GetString("SmartPlaylist_MatchAny");
    public static string SmartPlaylist_Rule_Singular => GetString("SmartPlaylist_Rule_Singular");
    public static string SmartPlaylist_Rule_Plural => GetString("SmartPlaylist_Rule_Plural");

    // Song List Base
    public static string SongList_PageTitle_Default => GetString("SongList_PageTitle_Default");
    public static string SongList_TotalItems_Default => GetString("SongList_TotalItems_Default");
    public static string SongList_ErrorLoading => GetString("SongList_ErrorLoading");
    public static string SongList_TotalItems_Format_Singular => GetString("SongList_TotalItems_Format_Singular");
    public static string SongList_TotalItems_Format_Plural => GetString("SongList_TotalItems_Format_Plural");
    public static string SongList_SelectedCount_Format => GetString("SongList_SelectedCount_Format");

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

    // Generic
    public static string Generic_OK => GetString("Generic_OK");
    public static string Generic_Cancel => GetString("Generic_Cancel");
    public static string Generic_Close => GetString("Generic_Close");
    public static string Generic_Recheck => GetString("Generic_Recheck");

    // Crash Report Buttons
    public static string CrashReport_Button_Reset => GetString("CrashReport_Button_Reset");

    // FFmpeg Setup
    public static string FFmpeg_Status_NotDetected => GetString("FFmpeg_Status_NotDetected");
    public static string FFmpeg_Status_Checking => GetString("FFmpeg_Status_Checking");
    public static string FFmpeg_Status_Detected => GetString("FFmpeg_Status_Detected");
    public static string FFmpeg_Status_StillNotDetected => GetString("FFmpeg_Status_StillNotDetected");
    public static string FFmpeg_Status_Error => GetString("FFmpeg_Status_Error");
}
