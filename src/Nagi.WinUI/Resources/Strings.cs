using System.Resources;
using System.Reflection;
using System.Globalization;

namespace Nagi.WinUI.Resources;

/// <summary>
///     Provides thread-safe access to the localized strings for the Nagi.WinUI project.
/// </summary>
public static class Strings
{
    public static readonly ResourceManager ResourceManager =
        new("Nagi.WinUI.Resources.Strings", typeof(Strings).Assembly);

    /// <summary>
    ///     Returns the localized string for the specified key, or the key itself if not found.
    /// </summary>
    public static string GetString(string name)
    {
        return ResourceManager.GetString(name, CultureInfo.CurrentUICulture) ?? name;
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
    public static string Settings_Export_Failed_Message => GetString("Settings_Export_Failed_Message");
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

    // Taskbar Tooltips
    public static string Taskbar_Tooltip_Previous => GetString("Taskbar_Tooltip_Previous");
    public static string Taskbar_Tooltip_Play => GetString("Taskbar_Tooltip_Play");
    public static string Taskbar_Tooltip_Pause => GetString("Taskbar_Tooltip_Pause");
    public static string Taskbar_Tooltip_Next => GetString("Taskbar_Tooltip_Next");

    // Updates
    public static string Update_Available_Title => GetString("Update_Available_Title");
    public static string Update_Available_Message => GetString("Update_Available_Message");
    public static string Update_Button_InstallNow => GetString("Update_Button_InstallNow");
    public static string Update_Button_Later => GetString("Update_Button_Later");
    public static string Update_Button_Skip => GetString("Update_Button_Skip");
    public static string Update_UpToDate_Title => GetString("Update_UpToDate_Title");
    public static string Update_UpToDate_Message => GetString("Update_UpToDate_Message");
    public static string Update_Error_Title => GetString("Update_Error_Title");
    public static string Update_Check_Error_Message => GetString("Update_Check_Error_Message");
    public static string Update_Install_Error_Message => GetString("Update_Install_Error_Message");
    public static string Update_Downloading_Title => GetString("Update_Downloading_Title");
    public static string Update_Downloading_Message => GetString("Update_Downloading_Message");

    // Player Errors
    public static string Player_Error_LibVLC_Unspecified => GetString("Player_Error_LibVLC_Unspecified");
    public static string Player_Error_LibVLC_WithDetails => GetString("Player_Error_LibVLC_WithDetails");
    public static string Player_Error_LoadFailed => GetString("Player_Error_LoadFailed");

    // Settings
    public static string Settings_Nav_Library => GetString("Settings_Nav_Library");
    public static string Settings_Nav_Folders => GetString("Settings_Nav_Folders");
    public static string Settings_Nav_Playlists => GetString("Settings_Nav_Playlists");
    public static string Settings_Nav_Artists => GetString("Settings_Nav_Artists");
    public static string Settings_Nav_Albums => GetString("Settings_Nav_Albums");
    public static string Settings_Nav_Genres => GetString("Settings_Nav_Genres");

    public static string Settings_Button_Shuffle => GetString("Settings_Button_Shuffle");
    public static string Settings_Button_Previous => GetString("Settings_Button_Previous");
    public static string Settings_Button_PlayPause => GetString("Settings_Button_PlayPause");
    public static string Settings_Button_Next => GetString("Settings_Button_Next");
    public static string Settings_Button_Repeat => GetString("Settings_Button_Repeat");
    public static string Settings_Button_Divider => GetString("Settings_Button_Divider");
    public static string Settings_Button_Lyrics => GetString("Settings_Button_Lyrics");
    public static string Settings_Button_Queue => GetString("Settings_Button_Queue");
    public static string Settings_Button_Volume => GetString("Settings_Button_Volume");

    public static string Settings_Provider_LRCLIB_Desc => GetString("Settings_Provider_LRCLIB_Desc");
    public static string Settings_Provider_NetEase_Desc => GetString("Settings_Provider_NetEase_Desc");
    public static string Settings_Provider_MusicBrainz_Desc => GetString("Settings_Provider_MusicBrainz_Desc");
    public static string Settings_Provider_TheAudioDB_Desc => GetString("Settings_Provider_TheAudioDB_Desc");
    public static string Settings_Provider_FanartTv_Desc => GetString("Settings_Provider_FanartTv_Desc");
    public static string Settings_Provider_Spotify_Desc => GetString("Settings_Provider_Spotify_Desc");
    public static string Settings_Provider_LastFm_Desc => GetString("Settings_Provider_LastFm_Desc");

    // App Info
    public static string App_Name_Default => GetString("App_Name_Default");
    public static string App_Version_Unknown => GetString("App_Version_Unknown");

    // AlbumPage
    public static string AlbumPage_SearchButton_Close_ToolTip => GetString("AlbumPage_SearchButton_Close_ToolTip");
    public static string AlbumPage_SearchButton_Search_ToolTip => GetString("AlbumPage_SearchButton_Search_ToolTip");

    // AlbumViewPage
    public static string AlbumViewPage_SearchButton_Close_ToolTip => GetString("AlbumViewPage_SearchButton_Close_ToolTip");
    public static string AlbumViewPage_SearchButton_Search_ToolTip => GetString("AlbumViewPage_SearchButton_Search_ToolTip");
    public static string AlbumViewPage_PlaylistMenu_NoPlaylists => GetString("AlbumViewPage_PlaylistMenu_NoPlaylists");

    // ArtistPage
    public static string ArtistPage_SearchButton_Close_ToolTip => GetString("ArtistPage_SearchButton_Close_ToolTip");
    public static string ArtistPage_SearchButton_Search_ToolTip => GetString("ArtistPage_SearchButton_Search_ToolTip");

    // ArtistViewPage
    public static string ArtistViewPage_SearchButton_Close_ToolTip => GetString("ArtistViewPage_SearchButton_Close_ToolTip");
    public static string ArtistViewPage_SearchButton_Search_ToolTip => GetString("ArtistViewPage_SearchButton_Search_ToolTip");
    public static string ArtistViewPage_PlaylistMenu_NoPlaylists => GetString("ArtistViewPage_PlaylistMenu_NoPlaylists");

    // FolderPage
    public static string FolderPage_DeleteDialog_Title => GetString("FolderPage_DeleteDialog_Title");
    public static string FolderPage_DeleteDialog_Format => GetString("FolderPage_DeleteDialog_Format");
    public static string FolderPage_DeleteDialog_PrimaryButton => GetString("FolderPage_DeleteDialog_PrimaryButton");
    public static string FolderPage_DeleteDialog_CloseButton => GetString("FolderPage_DeleteDialog_CloseButton");

    // FolderSongViewPage
    public static string FolderSongViewPage_Default_Title => GetString("FolderSongViewPage_Default_Title");
    public static string FolderSongViewPage_SearchButton_Close_ToolTip => GetString("FolderSongViewPage_SearchButton_Close_ToolTip");
    public static string FolderSongViewPage_SearchButton_Search_ToolTip => GetString("FolderSongViewPage_SearchButton_Search_ToolTip");
    public static string FolderSongViewPage_PlaylistMenu_NoPlaylists => GetString("FolderSongViewPage_PlaylistMenu_NoPlaylists");

    // GenrePage
    public static string GenrePage_SearchButton_Close_ToolTip => GetString("GenrePage_SearchButton_Close_ToolTip");
    public static string GenrePage_SearchButton_Search_ToolTip => GetString("GenrePage_SearchButton_Search_ToolTip");

    // GenreViewPage
    public static string GenreViewPage_SearchButton_Close_ToolTip => GetString("GenreViewPage_SearchButton_Close_ToolTip");
    public static string GenreViewPage_SearchButton_Search_ToolTip => GetString("GenreViewPage_SearchButton_Search_ToolTip");
    public static string GenreViewPage_PlaylistMenu_NoPlaylists => GetString("GenreViewPage_PlaylistMenu_NoPlaylists");

    // LibraryPage
    public static string LibraryPage_SearchButton_Close_ToolTip => GetString("LibraryPage_SearchButton_Close_ToolTip");
    public static string LibraryPage_SearchButton_Search_ToolTip => GetString("LibraryPage_SearchButton_Search_ToolTip");
    public static string LibraryPage_PlaylistMenu_NoPlaylists => GetString("LibraryPage_PlaylistMenu_NoPlaylists");

    // PlaylistPage
    public static string PlaylistPage_SearchButton_Close_ToolTip => GetString("PlaylistPage_SearchButton_Close_ToolTip");
    public static string PlaylistPage_SearchButton_Search_ToolTip => GetString("PlaylistPage_SearchButton_Search_ToolTip");
    public static string PlaylistPage_CreateDialog_Placeholder => GetString("PlaylistPage_CreateDialog_Placeholder");
    public static string PlaylistPage_CreateDialog_PickImage => GetString("PlaylistPage_CreateDialog_PickImage");
    public static string PlaylistPage_CreateDialog_Title => GetString("PlaylistPage_CreateDialog_Title");
    public static string PlaylistPage_CreateDialog_CreateButton => GetString("PlaylistPage_CreateDialog_CreateButton");
    public static string PlaylistPage_CreateDialog_CancelButton => GetString("PlaylistPage_CreateDialog_CancelButton");
    public static string PlaylistPage_RenameDialog_Title_Format => GetString("PlaylistPage_RenameDialog_Title_Format");
    public static string PlaylistPage_RenameDialog_RenameButton => GetString("PlaylistPage_RenameDialog_RenameButton");
    public static string PlaylistPage_DeleteDialog_Title => GetString("PlaylistPage_DeleteDialog_Title");
    public static string PlaylistPage_DeleteDialog_Content_Format => GetString("PlaylistPage_DeleteDialog_Content_Format");
    public static string PlaylistPage_DeleteDialog_DeleteButton => GetString("PlaylistPage_DeleteDialog_DeleteButton");
    
    // PlaylistSongViewPage
    public static string PlaylistSongViewPage_SearchButton_Close_ToolTip => GetString("PlaylistSongViewPage_SearchButton_Close_ToolTip");
    public static string PlaylistSongViewPage_SearchButton_Search_ToolTip => GetString("PlaylistSongViewPage_SearchButton_Search_ToolTip");
    public static string PlaylistSongViewPage_UnknownPlaylist => GetString("PlaylistSongViewPage_UnknownPlaylist");

    // SmartPlaylistSongViewPage
    public static string SmartPlaylistSongViewPage_SearchButton_Close_ToolTip => GetString("SmartPlaylistSongViewPage_SearchButton_Close_ToolTip");
    public static string SmartPlaylistSongViewPage_SearchButton_Search_ToolTip => GetString("SmartPlaylistSongViewPage_SearchButton_Search_ToolTip");
    public static string SmartPlaylistSongViewPage_UnknownPlaylist => GetString("SmartPlaylistSongViewPage_UnknownPlaylist");

    // Settings
    public static string Settings_Dialog_Rescan_Title => GetString("Settings_Dialog_Rescan_Title");
    public static string Settings_Dialog_Rescan_Content => GetString("Settings_Dialog_Rescan_Content");
    public static string Settings_Dialog_Rescan_PrimaryButton => GetString("Settings_Dialog_Rescan_PrimaryButton");
    public static string Settings_Status_Rescan_Preparing => GetString("Settings_Status_Rescan_Preparing");
    public static string Settings_Dialog_RescanComplete_Title => GetString("Settings_Dialog_RescanComplete_Title");
    public static string Settings_Dialog_RescanComplete_Content => GetString("Settings_Dialog_RescanComplete_Content");
    public static string Settings_Dialog_RescanFailed_Title => GetString("Settings_Dialog_RescanFailed_Title");
    public static string Settings_Dialog_RescanFailed_Content => GetString("Settings_Dialog_RescanFailed_Content");
    public static string Settings_Dialog_FFmpegNotFound_Title => GetString("Settings_Dialog_FFmpegNotFound_Title");
    public static string Settings_Dialog_VolumeNorm_Title => GetString("Settings_Dialog_VolumeNorm_Title");
    public static string Settings_Dialog_VolumeNorm_Content => GetString("Settings_Dialog_VolumeNorm_Content");
    public static string Settings_Dialog_VolumeNorm_PrimaryButton => GetString("Settings_Dialog_VolumeNorm_PrimaryButton");
    public static string Settings_Status_VolumeNorm_Preparing => GetString("Settings_Status_VolumeNorm_Preparing");
    public static string Settings_Status_VolumeNorm_Cancelled => GetString("Settings_Status_VolumeNorm_Cancelled");
    public static string Settings_Status_VolumeNorm_Error_Format => GetString("Settings_Status_VolumeNorm_Error_Format");

    // App
    public static string App_CrashReport_LogFallbackError_Format => GetString("App_CrashReport_LogFallbackError_Format");
    public static string Language_Auto => GetString("Language_Auto");

    // Migrated from resw
    public static string TrayIcon_ShowWindow => GetString("TrayIcon_ShowWindow");
    public static string TrayIcon_Exit => GetString("TrayIcon_Exit");
    public static string MiniPlayer_BackButton_ToolTip => GetString("MiniPlayer_BackButton_ToolTip");
    public static string MiniPlayer_UpNextHeader => GetString("MiniPlayer_UpNextHeader");
    public static string MiniPlayer_RestoreButton_ToolTip => GetString("MiniPlayer_RestoreButton_ToolTip");
    public static string MiniPlayer_PreviousButton_ToolTip => GetString("MiniPlayer_PreviousButton_ToolTip");
    public static string MiniPlayer_NextButton_ToolTip => GetString("MiniPlayer_NextButton_ToolTip");
    public static string MiniPlayer_QueueButton_ToolTip => GetString("MiniPlayer_QueueButton_ToolTip");
    public static string CrashReport_ReportIssuePreLink => GetString("CrashReport_ReportIssuePreLink");
    public static string CrashReport_IssueLink => GetString("CrashReport_IssueLink");
    public static string CrashReport_ReportIssuePostLink => GetString("CrashReport_ReportIssuePostLink");
    public static string CrashReport_ResetAppAdvice => GetString("CrashReport_ResetAppAdvice");
    public static string CrashReport_CopyLogButton_ToolTip => GetString("CrashReport_CopyLogButton_ToolTip");
    public static string CrashReport_CopyLogLabel => GetString("CrashReport_CopyLogLabel");
    public static string SmartPlaylist_PickCoverButton_Content => GetString("SmartPlaylist_PickCoverButton_Content");
    public static string SmartPlaylist_PickCoverButton_ToolTip => GetString("SmartPlaylist_PickCoverButton_ToolTip");
    public static string SmartPlaylist_PlaylistNameLabel => GetString("SmartPlaylist_PlaylistNameLabel");
    public static string SmartPlaylist_PlaylistNameTextBox_Placeholder => GetString("SmartPlaylist_PlaylistNameTextBox_Placeholder");
    public static string SmartPlaylist_SortOrderLabel => GetString("SmartPlaylist_SortOrderLabel");
    public static string SmartPlaylist_MatchLabel => GetString("SmartPlaylist_MatchLabel");
    public static string SmartPlaylist_MatchLogic_All_Content => GetString("SmartPlaylist_MatchLogic_All_Content");
    public static string SmartPlaylist_MatchLogic_Any_Content => GetString("SmartPlaylist_MatchLogic_Any_Content");
    public static string SmartPlaylist_MatchSuffixLabel => GetString("SmartPlaylist_MatchSuffixLabel");
    public static string SmartPlaylist_AddRuleButton_ToolTip => GetString("SmartPlaylist_AddRuleButton_ToolTip");
    public static string SmartPlaylist_AddRuleLabel => GetString("SmartPlaylist_AddRuleLabel");
    public static string SmartPlaylist_RuleValueTextBox_Placeholder => GetString("SmartPlaylist_RuleValueTextBox_Placeholder");
    public static string SmartPlaylist_RemoveRuleButton_ToolTip => GetString("SmartPlaylist_RemoveRuleButton_ToolTip");
    public static string SmartPlaylist_NoRulesTitle => GetString("SmartPlaylist_NoRulesTitle");
    public static string SmartPlaylist_NoRulesMessage => GetString("SmartPlaylist_NoRulesMessage");
    public static string SmartPlaylist_AddFirstRuleButton_Content => GetString("SmartPlaylist_AddFirstRuleButton_Content");
    public static string SmartPlaylist_AddFirstRuleButton_ToolTip => GetString("SmartPlaylist_AddFirstRuleButton_ToolTip");
    public static string SmartPlaylist_Dialog_PrimaryButton => GetString("SmartPlaylist_Dialog_PrimaryButton");
    public static string SmartPlaylist_Dialog_CloseButton => GetString("SmartPlaylist_Dialog_CloseButton");
    public static string TrayPopup_PreviousButton_ToolTip => GetString("TrayPopup_PreviousButton_ToolTip");
    public static string TrayPopup_NextButton_ToolTip => GetString("TrayPopup_NextButton_ToolTip");
    public static string TrayPopup_QueueButton_ToolTip => GetString("TrayPopup_QueueButton_ToolTip");
    public static string TrayPopup_BackButton_ToolTip => GetString("TrayPopup_BackButton_ToolTip");
    public static string TrayPopup_UpNextHeader => GetString("TrayPopup_UpNextHeader");
    public static string AppName => GetString("AppName");
    public static string AppDescription => GetString("AppDescription");
    public static string AppShortName => GetString("AppShortName");
    public static string AppDisplayName => GetString("AppDisplayName");
    public static string AppInfoTip => GetString("AppInfoTip");
    public static string StartupTaskDisplayName => GetString("StartupTaskDisplayName");
    public static string Player_PreviousButton_ToolTip => GetString("Player_PreviousButton_ToolTip");
    public static string Player_NextButton_ToolTip => GetString("Player_NextButton_ToolTip");
    public static string Player_LyricsButton_ToolTip => GetString("Player_LyricsButton_ToolTip");
    public static string Player_QueueButton_ToolTip => GetString("Player_QueueButton_ToolTip");
    public static string Player_GoToAlbumButton_ToolTip => GetString("Player_GoToAlbumButton_ToolTip");
    public static string QueueFlyout_Title => GetString("QueueFlyout_Title");
    public static string AlbumPage_Title => GetString("AlbumPage_Title");
    public static string AlbumPage_SearchBox_Placeholder => GetString("AlbumPage_SearchBox_Placeholder");
    public static string AlbumPage_SearchButton_ToolTip => GetString("AlbumPage_SearchButton_ToolTip");
    public static string AlbumPage_RandomButton_ToolTip => GetString("AlbumPage_RandomButton_ToolTip");
    public static string AlbumPage_Menu_Play => GetString("AlbumPage_Menu_Play");
    public static string AlbumPage_Empty_Title => GetString("AlbumPage_Empty_Title");
    public static string AlbumPage_Empty_Subtitle => GetString("AlbumPage_Empty_Subtitle");
    public static string AlbumViewPage_Header_Label => GetString("AlbumViewPage_Header_Label");
    public static string AlbumViewPage_Songs_Title => GetString("AlbumViewPage_Songs_Title");
    public static string AlbumViewPage_SearchBox_Placeholder => GetString("AlbumViewPage_SearchBox_Placeholder");
    public static string AlbumViewPage_SearchButton_ToolTip => GetString("AlbumViewPage_SearchButton_ToolTip");
    public static string AlbumViewPage_PlayAllButton_ToolTip => GetString("AlbumViewPage_PlayAllButton_ToolTip");
    public static string AlbumViewPage_ShufflePlayButton_ToolTip => GetString("AlbumViewPage_ShufflePlayButton_ToolTip");
    public static string AlbumViewPage_SongMenu_Play => GetString("AlbumViewPage_SongMenu_Play");
    public static string AlbumViewPage_SongMenu_PlayNext => GetString("AlbumViewPage_SongMenu_PlayNext");
    public static string AlbumViewPage_SongMenu_AddToQueue => GetString("AlbumViewPage_SongMenu_AddToQueue");
    public static string AlbumViewPage_SongMenu_AddToPlaylist => GetString("AlbumViewPage_SongMenu_AddToPlaylist");
    public static string AlbumViewPage_SongMenu_GoToArtist => GetString("AlbumViewPage_SongMenu_GoToArtist");
    public static string AlbumViewPage_SongMenu_ShowInExplorer => GetString("AlbumViewPage_SongMenu_ShowInExplorer");
    public static string AlbumViewPage_SongPlayButton_ToolTip => GetString("AlbumViewPage_SongPlayButton_ToolTip");
    public static string AlbumViewPage_Empty_Title => GetString("AlbumViewPage_Empty_Title");
    public static string AlbumViewPage_Empty_Subtitle => GetString("AlbumViewPage_Empty_Subtitle");
    public static string ArtistPage_Title => GetString("ArtistPage_Title");
    public static string ArtistPage_SearchBox_Placeholder => GetString("ArtistPage_SearchBox_Placeholder");
    public static string ArtistPage_SearchButton_ToolTip => GetString("ArtistPage_SearchButton_ToolTip");
    public static string ArtistPage_RandomButton_ToolTip => GetString("ArtistPage_RandomButton_ToolTip");
    public static string ArtistPage_Menu_Play => GetString("ArtistPage_Menu_Play");
    public static string ArtistPage_Menu_ChangeImage => GetString("ArtistPage_Menu_ChangeImage");
    public static string ArtistPage_Menu_RemoveImage => GetString("ArtistPage_Menu_RemoveImage");
    public static string ArtistPage_Empty_Title => GetString("ArtistPage_Empty_Title");
    public static string ArtistPage_Empty_Subtitle => GetString("ArtistPage_Empty_Subtitle");
    public static string ArtistViewPage_EditOverlay_Content => GetString("ArtistViewPage_EditOverlay_Content");
    public static string ArtistViewPage_ImageMenu_ChangeImage => GetString("ArtistViewPage_ImageMenu_ChangeImage");
    public static string ArtistViewPage_ImageMenu_RemoveImage => GetString("ArtistViewPage_ImageMenu_RemoveImage");
    public static string ArtistViewPage_Header_Label => GetString("ArtistViewPage_Header_Label");
    public static string ArtistViewPage_Albums_Title => GetString("ArtistViewPage_Albums_Title");
    public static string ArtistViewPage_Songs_Title => GetString("ArtistViewPage_Songs_Title");
    public static string ArtistViewPage_SearchBox_Placeholder => GetString("ArtistViewPage_SearchBox_Placeholder");
    public static string ArtistViewPage_SearchButton_ToolTip => GetString("ArtistViewPage_SearchButton_ToolTip");
    public static string ArtistViewPage_PlayAllButton_ToolTip => GetString("ArtistViewPage_PlayAllButton_ToolTip");
    public static string ArtistViewPage_ShufflePlayButton_ToolTip => GetString("ArtistViewPage_ShufflePlayButton_ToolTip");
    public static string ArtistViewPage_SongMenu_Play => GetString("ArtistViewPage_SongMenu_Play");
    public static string ArtistViewPage_SongMenu_PlayNext => GetString("ArtistViewPage_SongMenu_PlayNext");
    public static string ArtistViewPage_SongMenu_AddToQueue => GetString("ArtistViewPage_SongMenu_AddToQueue");
    public static string ArtistViewPage_SongMenu_AddToPlaylist => GetString("ArtistViewPage_SongMenu_AddToPlaylist");
    public static string ArtistViewPage_SongMenu_GoToAlbum => GetString("ArtistViewPage_SongMenu_GoToAlbum");
    public static string ArtistViewPage_SongMenu_ShowInExplorer => GetString("ArtistViewPage_SongMenu_ShowInExplorer");
    public static string ArtistViewPage_SongPlayButton_ToolTip => GetString("ArtistViewPage_SongPlayButton_ToolTip");
    public static string ArtistViewPage_Empty_Title => GetString("ArtistViewPage_Empty_Title");
    public static string ArtistViewPage_Empty_Subtitle => GetString("ArtistViewPage_Empty_Subtitle");
    public static string FolderPage_Title => GetString("FolderPage_Title");
    public static string FolderPage_RandomButton_ToolTip => GetString("FolderPage_RandomButton_ToolTip");
    public static string FolderPage_AddFolderButton_ToolTip => GetString("FolderPage_AddFolderButton_ToolTip");
    public static string FolderPage_AddFolder_Label => GetString("FolderPage_AddFolder_Label");
    public static string FolderPage_Menu_Play => GetString("FolderPage_Menu_Play");
    public static string FolderPage_Menu_Rescan => GetString("FolderPage_Menu_Rescan");
    public static string FolderPage_Menu_Delete => GetString("FolderPage_Menu_Delete");
    public static string FolderPage_Empty_Title => GetString("FolderPage_Empty_Title");
    public static string FolderPage_Empty_Subtitle => GetString("FolderPage_Empty_Subtitle");
    public static string FolderSongViewPage_FolderMenu_Play => GetString("FolderSongViewPage_FolderMenu_Play");
    public static string FolderSongViewPage_FolderMenu_AddToPlaylist => GetString("FolderSongViewPage_FolderMenu_AddToPlaylist");
    public static string FolderSongViewPage_SongMenu_Play => GetString("FolderSongViewPage_SongMenu_Play");
    public static string FolderSongViewPage_SongMenu_PlayNext => GetString("FolderSongViewPage_SongMenu_PlayNext");
    public static string FolderSongViewPage_SongMenu_AddToQueue => GetString("FolderSongViewPage_SongMenu_AddToQueue");
    public static string FolderSongViewPage_SongMenu_AddToPlaylist => GetString("FolderSongViewPage_SongMenu_AddToPlaylist");
    public static string FolderSongViewPage_SongMenu_GoToAlbum => GetString("FolderSongViewPage_SongMenu_GoToAlbum");
    public static string FolderSongViewPage_SongMenu_GoToArtist => GetString("FolderSongViewPage_SongMenu_GoToArtist");
    public static string FolderSongViewPage_SongMenu_ShowInExplorer => GetString("FolderSongViewPage_SongMenu_ShowInExplorer");
    public static string FolderSongViewPage_SongPlayButton_ToolTip => GetString("FolderSongViewPage_SongPlayButton_ToolTip");
    public static string FolderSongViewPage_Header_Label => GetString("FolderSongViewPage_Header_Label");
    public static string FolderSongViewPage_Songs_Title => GetString("FolderSongViewPage_Songs_Title");
    public static string FolderSongViewPage_SearchBox_Placeholder => GetString("FolderSongViewPage_SearchBox_Placeholder");
    public static string FolderSongViewPage_SearchButton_ToolTip => GetString("FolderSongViewPage_SearchButton_ToolTip");
    public static string FolderSongViewPage_PlayAllButton_ToolTip => GetString("FolderSongViewPage_PlayAllButton_ToolTip");
    public static string FolderSongViewPage_ShufflePlayButton_ToolTip => GetString("FolderSongViewPage_ShufflePlayButton_ToolTip");
    public static string FolderSongViewPage_Empty_Title => GetString("FolderSongViewPage_Empty_Title");
    public static string FolderSongViewPage_Empty_Subtitle => GetString("FolderSongViewPage_Empty_Subtitle");
    public static string GenrePage_Title => GetString("GenrePage_Title");
    public static string GenrePage_SearchBox_Placeholder => GetString("GenrePage_SearchBox_Placeholder");
    public static string GenrePage_SearchButton_ToolTip => GetString("GenrePage_SearchButton_ToolTip");
    public static string GenrePage_RandomButton_ToolTip => GetString("GenrePage_RandomButton_ToolTip");
    public static string GenrePage_Menu_Play => GetString("GenrePage_Menu_Play");
    public static string GenrePage_Empty_Title => GetString("GenrePage_Empty_Title");
    public static string GenrePage_Empty_Subtitle => GetString("GenrePage_Empty_Subtitle");
    public static string LibraryPage_Title => GetString("LibraryPage_Title");
    public static string LibraryPage_SearchBox_Placeholder => GetString("LibraryPage_SearchBox_Placeholder");
    public static string LibraryPage_SearchButton_ToolTip => GetString("LibraryPage_SearchButton_ToolTip");
    public static string LibraryPage_PlayAllButton_ToolTip => GetString("LibraryPage_PlayAllButton_ToolTip");
    public static string LibraryPage_ShufflePlayAllButton_ToolTip => GetString("LibraryPage_ShufflePlayAllButton_ToolTip");
    public static string LibraryPage_Context_Play => GetString("LibraryPage_Context_Play");
    public static string LibraryPage_Context_PlayNext => GetString("LibraryPage_Context_PlayNext");
    public static string LibraryPage_Context_AddToQueue => GetString("LibraryPage_Context_AddToQueue");
    public static string LibraryPage_Context_AddToPlaylist => GetString("LibraryPage_Context_AddToPlaylist");
    public static string LibraryPage_Context_GoToAlbum => GetString("LibraryPage_Context_GoToAlbum");
    public static string LibraryPage_Context_GoToArtist => GetString("LibraryPage_Context_GoToArtist");
    public static string LibraryPage_Context_ShowInExplorer => GetString("LibraryPage_Context_ShowInExplorer");
    public static string LibraryPage_SongElement_PlayButton_ToolTip => GetString("LibraryPage_SongElement_PlayButton_ToolTip");
    public static string LibraryPage_EmptyState_Title => GetString("LibraryPage_EmptyState_Title");
    public static string LibraryPage_EmptyState_Subtitle => GetString("LibraryPage_EmptyState_Subtitle");
    public static string LyricsPage_NoLyrics_Title => GetString("LyricsPage_NoLyrics_Title");
    public static string LyricsPage_NoLyrics_Subtitle => GetString("LyricsPage_NoLyrics_Subtitle");
    public static string OnboardingPage_Welcome_Title => GetString("OnboardingPage_Welcome_Title");
    public static string OnboardingPage_AddFolderButton_ToolTip => GetString("OnboardingPage_AddFolderButton_ToolTip");
    public static string OnboardingPage_AddFolderButton_Content => GetString("OnboardingPage_AddFolderButton_Content");
    public static string PlaylistPage_Title => GetString("PlaylistPage_Title");
    public static string PlaylistPage_SearchBox_Placeholder => GetString("PlaylistPage_SearchBox_Placeholder");
    public static string PlaylistPage_SearchButton_ToolTip => GetString("PlaylistPage_SearchButton_ToolTip");
    public static string PlaylistPage_RandomButton_ToolTip => GetString("PlaylistPage_RandomButton_ToolTip");
    public static string PlaylistPage_NewPlaylistButton_ToolTip => GetString("PlaylistPage_NewPlaylistButton_ToolTip");
    public static string PlaylistPage_NewPlaylistButton_Content => GetString("PlaylistPage_NewPlaylistButton_Content");
    public static string PlaylistPage_NewPlaylistMenu_Regular => GetString("PlaylistPage_NewPlaylistMenu_Regular");
    public static string PlaylistPage_NewPlaylistMenu_Smart => GetString("PlaylistPage_NewPlaylistMenu_Smart");
    public static string PlaylistPage_Context_Play => GetString("PlaylistPage_Context_Play");
    public static string PlaylistPage_Context_Rename => GetString("PlaylistPage_Context_Rename");
    public static string PlaylistPage_Context_ChangeImage => GetString("PlaylistPage_Context_ChangeImage");
    public static string PlaylistPage_Context_RemoveImage => GetString("PlaylistPage_Context_RemoveImage");
    public static string PlaylistPage_Context_Delete => GetString("PlaylistPage_Context_Delete");
    public static string PlaylistPage_EmptyState_Title => GetString("PlaylistPage_EmptyState_Title");
    public static string PlaylistPage_EmptyState_Subtitle => GetString("PlaylistPage_EmptyState_Subtitle");
    public static string PlaylistSongViewPage_Header_Caption => GetString("PlaylistSongViewPage_Header_Caption");
    public static string PlaylistSongViewPage_Section_Songs => GetString("PlaylistSongViewPage_Section_Songs");
    public static string PlaylistSongViewPage_SearchBox_Placeholder => GetString("PlaylistSongViewPage_SearchBox_Placeholder");
    public static string PlaylistSongViewPage_SearchButton_ToolTip => GetString("PlaylistSongViewPage_SearchButton_ToolTip");
    public static string PlaylistSongViewPage_PlayAllButton_ToolTip => GetString("PlaylistSongViewPage_PlayAllButton_ToolTip");
    public static string PlaylistSongViewPage_ShufflePlayAllButton_ToolTip => GetString("PlaylistSongViewPage_ShufflePlayAllButton_ToolTip");
    public static string PlaylistSongViewPage_Context_Play => GetString("PlaylistSongViewPage_Context_Play");
    public static string PlaylistSongViewPage_Context_PlayNext => GetString("PlaylistSongViewPage_Context_PlayNext");
    public static string PlaylistSongViewPage_Context_AddToQueue => GetString("PlaylistSongViewPage_Context_AddToQueue");
    public static string PlaylistSongViewPage_Context_GoToAlbum => GetString("PlaylistSongViewPage_Context_GoToAlbum");
    public static string PlaylistSongViewPage_Context_GoToArtist => GetString("PlaylistSongViewPage_Context_GoToArtist");
    public static string PlaylistSongViewPage_Context_ShowInExplorer => GetString("PlaylistSongViewPage_Context_ShowInExplorer");
    public static string PlaylistSongViewPage_Context_RemoveFromPlaylist => GetString("PlaylistSongViewPage_Context_RemoveFromPlaylist");
    public static string PlaylistSongViewPage_SongElement_PlayButton_ToolTip => GetString("PlaylistSongViewPage_SongElement_PlayButton_ToolTip");
    public static string PlaylistSongViewPage_EmptyState_Title => GetString("PlaylistSongViewPage_EmptyState_Title");
    public static string PlaylistSongViewPage_EmptyState_Subtitle => GetString("PlaylistSongViewPage_EmptyState_Subtitle");
    public static string GenreViewPage_Header_Caption => GetString("GenreViewPage_Header_Caption");
    public static string GenreViewPage_Section_Songs => GetString("GenreViewPage_Section_Songs");
    public static string GenreViewPage_SearchBox_Placeholder => GetString("GenreViewPage_SearchBox_Placeholder");
    public static string GenreViewPage_SearchButton_ToolTip => GetString("GenreViewPage_SearchButton_ToolTip");
    public static string GenreViewPage_PlayAllButton_ToolTip => GetString("GenreViewPage_PlayAllButton_ToolTip");
    public static string GenreViewPage_ShufflePlayAllButton_ToolTip => GetString("GenreViewPage_ShufflePlayAllButton_ToolTip");
    public static string GenreViewPage_Context_Play => GetString("GenreViewPage_Context_Play");
    public static string GenreViewPage_Context_PlayNext => GetString("GenreViewPage_Context_PlayNext");
    public static string GenreViewPage_Context_AddToQueue => GetString("GenreViewPage_Context_AddToQueue");
    public static string GenreViewPage_Context_AddToPlaylist => GetString("GenreViewPage_Context_AddToPlaylist");
    public static string GenreViewPage_Context_GoToAlbum => GetString("GenreViewPage_Context_GoToAlbum");
    public static string GenreViewPage_Context_GoToArtist => GetString("GenreViewPage_Context_GoToArtist");
    public static string GenreViewPage_Context_ShowInExplorer => GetString("GenreViewPage_Context_ShowInExplorer");
    public static string GenreViewPage_Context_NoPlaylists => GetString("GenreViewPage_Context_NoPlaylists");
    public static string GenreViewPage_SongElement_PlayButton_ToolTip => GetString("GenreViewPage_SongElement_PlayButton_ToolTip");
    public static string GenreViewPage_EmptyState_Title => GetString("GenreViewPage_EmptyState_Title");
    public static string GenreViewPage_EmptyState_Subtitle => GetString("GenreViewPage_EmptyState_Subtitle");
    public static string SettingsPage_Title => GetString("SettingsPage_Title");
    public static string SettingsPage_Section_Appearance => GetString("SettingsPage_Section_Appearance");
    public static string SettingsPage_Theme_Header => GetString("SettingsPage_Theme_Header");
    public static string SettingsPage_Theme_Description => GetString("SettingsPage_Theme_Description");
    public static string SettingsPage_AccentColor => GetString("SettingsPage_AccentColor");
    public static string SettingsPage_ResetAccentColor_Content => GetString("SettingsPage_ResetAccentColor_Content");
    public static string SettingsPage_WindowMaterial_Header => GetString("SettingsPage_WindowMaterial_Header");
    public static string SettingsPage_WindowMaterial_Description => GetString("SettingsPage_WindowMaterial_Description");
    public static string SettingsPage_DynamicTheming_Header => GetString("SettingsPage_DynamicTheming_Header");
    public static string SettingsPage_DynamicTheming_Description => GetString("SettingsPage_DynamicTheming_Description");
    public static string SettingsPage_RememberWindowSize_Header => GetString("SettingsPage_RememberWindowSize_Header");
    public static string SettingsPage_RememberWindowSize_Description => GetString("SettingsPage_RememberWindowSize_Description");
    public static string SettingsPage_RememberWindowPosition_Header => GetString("SettingsPage_RememberWindowPosition_Header");
    public static string SettingsPage_RememberWindowPosition_Description => GetString("SettingsPage_RememberWindowPosition_Description");
    public static string SettingsPage_RememberNavState_Header => GetString("SettingsPage_RememberNavState_Header");
    public static string SettingsPage_RememberNavState_Description => GetString("SettingsPage_RememberNavState_Description");
    public static string SettingsPage_CustomizeNav_Header => GetString("SettingsPage_CustomizeNav_Header");
    public static string SettingsPage_CustomizeNav_Description => GetString("SettingsPage_CustomizeNav_Description");
    public static string SettingsPage_Section_Player => GetString("SettingsPage_Section_Player");
    public static string SettingsPage_VolumeNormalization_Header => GetString("SettingsPage_VolumeNormalization_Header");
    public static string SettingsPage_VolumeNormalization_Description => GetString("SettingsPage_VolumeNormalization_Description");
    public static string SettingsPage_Equalizer => GetString("SettingsPage_Equalizer");
    public static string SettingsPage_ResetEqualizer_Content => GetString("SettingsPage_ResetEqualizer_Content");
    public static string SettingsPage_Equalizer_Preamp => GetString("SettingsPage_Equalizer_Preamp");
    public static string SettingsPage_Equalizer_Preset_Custom_Placeholder => GetString("SettingsPage_Equalizer_Preset_Custom_Placeholder");
    public static string SettingsPage_CustomizePlayer => GetString("SettingsPage_CustomizePlayer");
    public static string SettingsPage_ResetPlayerButtons_Content => GetString("SettingsPage_ResetPlayerButtons_Content");
    public static string SettingsPage_CustomizePlayer_Instructions => GetString("SettingsPage_CustomizePlayer_Instructions");
    public static string SettingsPage_CollapsablePlayer_Header => GetString("SettingsPage_CollapsablePlayer_Header");
    public static string SettingsPage_CollapsablePlayer_Description => GetString("SettingsPage_CollapsablePlayer_Description");
    public static string SettingsPage_RestorePlayback_Header => GetString("SettingsPage_RestorePlayback_Header");
    public static string SettingsPage_RestorePlayback_Description => GetString("SettingsPage_RestorePlayback_Description");
    public static string SettingsPage_AutoLaunch_Header => GetString("SettingsPage_AutoLaunch_Header");
    public static string SettingsPage_AutoLaunch_Description => GetString("SettingsPage_AutoLaunch_Description");
    public static string SettingsPage_StartMinimized_Header => GetString("SettingsPage_StartMinimized_Header");
    public static string SettingsPage_StartMinimized_Description => GetString("SettingsPage_StartMinimized_Description");
    public static string SettingsPage_MinimizeToMiniPlayer_Header => GetString("SettingsPage_MinimizeToMiniPlayer_Header");
    public static string SettingsPage_MinimizeToMiniPlayer_Description => GetString("SettingsPage_MinimizeToMiniPlayer_Description");
    public static string SettingsPage_HideToTray_Header => GetString("SettingsPage_HideToTray_Header");
    public static string SettingsPage_HideToTray_Description => GetString("SettingsPage_HideToTray_Description");
    public static string SettingsPage_TrayCoverArt_Header => GetString("SettingsPage_TrayCoverArt_Header");
    public static string SettingsPage_TrayCoverArt_Description => GetString("SettingsPage_TrayCoverArt_Description");
    public static string SettingsPage_Section_Data => GetString("SettingsPage_Section_Data");
    public static string SettingsPage_ArtistSplitting_Header => GetString("SettingsPage_ArtistSplitting_Header");
    public static string SettingsPage_ArtistSplitting_Description => GetString("SettingsPage_ArtistSplitting_Description");
    public static string SettingsPage_RescanLibrary_Content => GetString("SettingsPage_RescanLibrary_Content");
    public static string SettingsPage_RescanLibrary_ToolTip => GetString("SettingsPage_RescanLibrary_ToolTip");
    public static string SettingsPage_OnlineMetadata_Header => GetString("SettingsPage_OnlineMetadata_Header");
    public static string SettingsPage_OnlineMetadata_Description => GetString("SettingsPage_OnlineMetadata_Description");
    public static string SettingsPage_MetadataSources_Header => GetString("SettingsPage_MetadataSources_Header");
    public static string SettingsPage_MetadataSources_Description => GetString("SettingsPage_MetadataSources_Description");
    public static string SettingsPage_OnlineLyrics_Header => GetString("SettingsPage_OnlineLyrics_Header");
    public static string SettingsPage_OnlineLyrics_Description => GetString("SettingsPage_OnlineLyrics_Description");
    public static string SettingsPage_LyricsSources_Header => GetString("SettingsPage_LyricsSources_Header");
    public static string SettingsPage_LyricsSources_Description => GetString("SettingsPage_LyricsSources_Description");
    public static string SettingsPage_PlaylistsIO_Header => GetString("SettingsPage_PlaylistsIO_Header");
    public static string SettingsPage_PlaylistsIO_Description => GetString("SettingsPage_PlaylistsIO_Description");
    public static string SettingsPage_ExportPlaylists_Content => GetString("SettingsPage_ExportPlaylists_Content");
    public static string SettingsPage_ExportPlaylists_ToolTip => GetString("SettingsPage_ExportPlaylists_ToolTip");
    public static string SettingsPage_ImportPlaylists_Content => GetString("SettingsPage_ImportPlaylists_Content");
    public static string SettingsPage_ImportPlaylists_ToolTip => GetString("SettingsPage_ImportPlaylists_ToolTip");
    public static string SettingsPage_ResetApp_Header => GetString("SettingsPage_ResetApp_Header");
    public static string SettingsPage_ResetApp_Description => GetString("SettingsPage_ResetApp_Description");
    public static string SettingsPage_ResetButton_Content => GetString("SettingsPage_ResetButton_Content");
    public static string SettingsPage_Section_Integrations => GetString("SettingsPage_Section_Integrations");
    public static string SettingsPage_LastFm_Header => GetString("SettingsPage_LastFm_Header");
    public static string SettingsPage_LastFm_Description => GetString("SettingsPage_LastFm_Description");
    public static string SettingsPage_LastFm_Connect_Content => GetString("SettingsPage_LastFm_Connect_Content");
    public static string SettingsPage_LastFm_Complete_Content => GetString("SettingsPage_LastFm_Complete_Content");
    public static string SettingsPage_LastFm_Cancel_Content => GetString("SettingsPage_LastFm_Cancel_Content");
    public static string SettingsPage_LastFm_Disconnect_Content => GetString("SettingsPage_LastFm_Disconnect_Content");
    public static string SettingsPage_LastFm_Scrobbling_Header => GetString("SettingsPage_LastFm_Scrobbling_Header");
    public static string SettingsPage_LastFm_Scrobbling_Description => GetString("SettingsPage_LastFm_Scrobbling_Description");
    public static string SettingsPage_LastFm_NowPlaying_Header => GetString("SettingsPage_LastFm_NowPlaying_Header");
    public static string SettingsPage_LastFm_NowPlaying_Description => GetString("SettingsPage_LastFm_NowPlaying_Description");
    public static string SettingsPage_Discord_Header => GetString("SettingsPage_Discord_Header");
    public static string SettingsPage_Discord_Description => GetString("SettingsPage_Discord_Description");
    public static string SmartPlaylistSongViewPage_Header_Caption => GetString("SmartPlaylistSongViewPage_Header_Caption");
    public static string SmartPlaylistSongViewPage_Section_Songs => GetString("SmartPlaylistSongViewPage_Section_Songs");
    public static string SmartPlaylistSongViewPage_SearchBox_Placeholder => GetString("SmartPlaylistSongViewPage_SearchBox_Placeholder");
    public static string SmartPlaylistSongViewPage_SearchButton_ToolTip => GetString("SmartPlaylistSongViewPage_SearchButton_ToolTip");
    public static string SmartPlaylistSongViewPage_EditRulesButton_ToolTip => GetString("SmartPlaylistSongViewPage_EditRulesButton_ToolTip");
    public static string SmartPlaylistSongViewPage_PlayAllButton_ToolTip => GetString("SmartPlaylistSongViewPage_PlayAllButton_ToolTip");
    public static string SmartPlaylistSongViewPage_ShufflePlayAllButton_ToolTip => GetString("SmartPlaylistSongViewPage_ShufflePlayAllButton_ToolTip");
    public static string SmartPlaylistSongViewPage_Context_Play => GetString("SmartPlaylistSongViewPage_Context_Play");
    public static string SmartPlaylistSongViewPage_Context_PlayNext => GetString("SmartPlaylistSongViewPage_Context_PlayNext");
    public static string SmartPlaylistSongViewPage_Context_AddToQueue => GetString("SmartPlaylistSongViewPage_Context_AddToQueue");
    public static string SmartPlaylistSongViewPage_Context_GoToAlbum => GetString("SmartPlaylistSongViewPage_Context_GoToAlbum");
    public static string SmartPlaylistSongViewPage_Context_GoToArtist => GetString("SmartPlaylistSongViewPage_Context_GoToArtist");
    public static string SmartPlaylistSongViewPage_Context_ShowInExplorer => GetString("SmartPlaylistSongViewPage_Context_ShowInExplorer");
    public static string SmartPlaylistSongViewPage_SongElement_PlayButton_ToolTip => GetString("SmartPlaylistSongViewPage_SongElement_PlayButton_ToolTip");
    public static string SmartPlaylistSongViewPage_EmptyState_Title => GetString("SmartPlaylistSongViewPage_EmptyState_Title");
    public static string SmartPlaylistSongViewPage_EmptyState_Subtitle => GetString("SmartPlaylistSongViewPage_EmptyState_Subtitle");
    public static string SmartPlaylistSongViewPage_EmptyState_Button_Content => GetString("SmartPlaylistSongViewPage_EmptyState_Button_Content");
    public static string SmartPlaylistSongViewPage_EmptyState_Button_ToolTip => GetString("SmartPlaylistSongViewPage_EmptyState_Button_ToolTip");
    public static string SettingsPage_Section_About => GetString("SettingsPage_Section_About");
    public static string SettingsPage_AboutExpander_Header => GetString("SettingsPage_AboutExpander_Header");
    public static string SettingsPage_AboutExpander_Description => GetString("SettingsPage_AboutExpander_Description");
    public static string SettingsPage_About_VersionPrefix => GetString("SettingsPage_About_VersionPrefix");
    public static string SettingsPage_About_Licenses_Content => GetString("SettingsPage_About_Licenses_Content");
    public static string SettingsPage_About_SourceCode_Content => GetString("SettingsPage_About_SourceCode_Content");
    public static string SettingsPage_About_CheckUpdates_Content => GetString("SettingsPage_About_CheckUpdates_Content");
    public static string SettingsPage_About_Feedback_Content => GetString("SettingsPage_About_Feedback_Content");
    public static string SettingsPage_CheckForUpdates_Header => GetString("SettingsPage_CheckForUpdates_Header");
    public static string SettingsPage_CheckForUpdates_Description => GetString("SettingsPage_CheckForUpdates_Description");
    public static string SettingsPage_Language_Header => GetString("SettingsPage_Language_Header");
    public static string SettingsPage_Language_Description => GetString("SettingsPage_Language_Description");
    public static string SettingsPage_RestartRequired_Title => GetString("SettingsPage_RestartRequired_Title");
    public static string SettingsPage_RestartRequired_Message => GetString("SettingsPage_RestartRequired_Message");
    public static string SettingsPage_About_PrivacyPolicy_Content => GetString("SettingsPage_About_PrivacyPolicy_Content");
    public static string SettingsPage_BackupRestore_Header => GetString("SettingsPage_BackupRestore_Header");
    public static string SettingsPage_BackupRestore_Description => GetString("SettingsPage_BackupRestore_Description");
    public static string SettingsPage_CreateBackup_Content => GetString("SettingsPage_CreateBackup_Content");
    public static string SettingsPage_RestoreBackup_Content => GetString("SettingsPage_RestoreBackup_Content");
    public static string Settings_Backup_Success_Title => GetString("Settings_Backup_Success_Title");
    public static string Settings_Backup_Success_Message => GetString("Settings_Backup_Success_Message");
    public static string Settings_Backup_Failed_Title => GetString("Settings_Backup_Failed_Title");
    public static string Settings_Backup_Failed_Message => GetString("Settings_Backup_Failed_Message");
    public static string Settings_Restore_Invalid_Title => GetString("Settings_Restore_Invalid_Title");
    public static string Settings_Restore_Invalid_Message => GetString("Settings_Restore_Invalid_Message");
    public static string Settings_Restore_Confirm_Title => GetString("Settings_Restore_Confirm_Title");
    public static string Settings_Restore_Confirm_Message => GetString("Settings_Restore_Confirm_Message");
    public static string Settings_Restore_Confirm_Button => GetString("Settings_Restore_Confirm_Button");
    public static string Settings_Restore_Success_Title => GetString("Settings_Restore_Success_Title");
    public static string Settings_Restore_Success_Message => GetString("Settings_Restore_Success_Message");
    public static string Settings_Restore_Failed_Title => GetString("Settings_Restore_Failed_Title");
    public static string Settings_Restore_Failed_Message => GetString("Settings_Restore_Failed_Message");
}
