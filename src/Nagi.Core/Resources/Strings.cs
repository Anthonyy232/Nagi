using System.Resources;
using System.Reflection;

namespace Nagi.Core.Resources;

/// <summary>
///     Provides thread-safe access to the localized strings for the Nagi.Core library.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager _resourceManager =
        new("Nagi.Core.Resources.Strings", typeof(Strings).Assembly);

    private static string GetString(string name)
    {
        return _resourceManager.GetString(name) ?? name;
    }

    // Noun Labels
    public static string Label_Artist => GetString("Label_Artist");
    public static string Label_Album => GetString("Label_Album");
    public static string Label_Genre => GetString("Label_Genre");
    public static string Label_Title => GetString("Label_Title");
    public static string Label_Playlist => GetString("Label_Playlist");
    public static string Label_File => GetString("Label_File");
    public static string Label_Year => GetString("Label_Year");
    public static string Label_DateAdded => GetString("Label_DateAdded");
    public static string Label_DateCreated => GetString("Label_DateCreated");
    public static string Label_DateModified => GetString("Label_DateModified");
    public static string Label_LastPlayed => GetString("Label_LastPlayed");
    public static string Label_Duration => GetString("Label_Duration");
    public static string Label_Bpm => GetString("Label_Bpm");
    public static string Label_TrackNumber => GetString("Label_TrackNumber");
    public static string Label_PlayCount => GetString("Label_PlayCount");
    public static string Label_Song => GetString("Label_Song");
    public static string Label_Songs => GetString("Label_Songs");
    public static string Label_Artists => GetString("Label_Artists");
    public static string Label_Folder => GetString("Label_Folder");
    public static string Label_Folders => GetString("Label_Folders");
    public static string Label_ManualOrder => GetString("Label_ManualOrder");
    public static string Label_Random => GetString("Label_Random");

    // Format Patterns
    public static string Format_Unknown => GetString("Format_Unknown");
    public static string Format_New => GetString("Format_New");
    public static string Format_NotFound => GetString("Format_NotFound");
    public static string Format_AlphaAsc => GetString("Format_AlphaAsc");
    public static string Format_AlphaDesc => GetString("Format_AlphaDesc");
    public static string Format_TemporalNewest => GetString("Format_TemporalNewest");
    public static string Format_TemporalOldest => GetString("Format_TemporalOldest");
    public static string Format_DirectionalAsc => GetString("Format_DirectionalAsc");
    public static string Format_DirectionalDesc => GetString("Format_DirectionalDesc");
    public static string Format_ShortestFirst => GetString("Format_ShortestFirst");
    public static string Format_LongestFirst => GetString("Format_LongestFirst");
    public static string Format_SlowestFirst => GetString("Format_SlowestFirst");
    public static string Format_FastestFirst => GetString("Format_FastestFirst");
    public static string Format_Most => GetString("Format_Most");
    public static string Format_Least => GetString("Format_Least");
    public static string Format_ScanFolderProgress => GetString("Format_ScanFolderProgress");
    public static string Format_ScanCompleteResult => GetString("Format_ScanCompleteResult");
    public static string Format_SavingBatch => GetString("Format_SavingBatch");

    // Status & Success Messages
    public static string Status_ScanningFolder => GetString("Status_ScanningFolder");
    public static string Status_ScanCompleteUpToDate => GetString("Status_ScanCompleteUpToDate");
    public static string Status_ScanCancelled => GetString("Status_ScanCancelled");
    public static string Status_ScanFailed => GetString("Status_ScanFailed");
    public static string Status_PreparingScanCaches => GetString("Status_PreparingScanCaches");
    public static string Status_ReadingSongDetails => GetString("Status_ReadingSongDetails");
    public static string Status_CleaningUpLibrary => GetString("Status_CleaningUpLibrary");
    public static string Status_Finalizing => GetString("Status_Finalizing");
    public static string Status_NormalizationFailed => GetString("Status_NormalizationFailed");
    public static string Status_NoFoldersToRefresh => GetString("Status_NoFoldersToRefresh");
    public static string Status_LibraryRefreshComplete => GetString("Status_LibraryRefreshComplete");

    // Error Messages
    public static string Error_FailedToAddFolder => GetString("Error_FailedToAddFolder");
    public static string Error_PlaylistNameEmpty => GetString("Error_PlaylistNameEmpty");
    public static string Error_RatingRange => GetString("Error_RatingRange");

    // Labels
    public static string Label_WithLyrics => GetString("Label_WithLyrics");
    public static string Label_WithCoverArt => GetString("Label_WithCoverArt");
    public static string Label_WithArtistImage => GetString("Label_WithArtistImage");
    public static string Label_And => GetString("Label_And");

    // Static Strings
    public static string UnknownFilename => GetString("UnknownFilename");
    public static string ArtistSeparator => GetString("ArtistSeparator");
    public static string SortByPrefix => GetString("SortByPrefix");

    public static string FFmpeg_SetupInstructions => GetString("FFmpeg_SetupInstructions");
    public static string Error_M3uImportNoSongsMatched => GetString("Error_M3uImportNoSongsMatched");
    public static string Error_NoPlaylistsToExport => GetString("Error_NoPlaylistsToExport");

    // Equalizer Presets
    public static string EqPreset_None => GetString("EqPreset_None");
    public static string EqPreset_Classical => GetString("EqPreset_Classical");
    public static string EqPreset_Dance => GetString("EqPreset_Dance");
    public static string EqPreset_FullBass => GetString("EqPreset_FullBass");
    public static string EqPreset_FullTreble => GetString("EqPreset_FullTreble");
    public static string EqPreset_Jazz => GetString("EqPreset_Jazz");
    public static string EqPreset_Pop => GetString("EqPreset_Pop");
    public static string EqPreset_Rock => GetString("EqPreset_Rock");
    public static string EqPreset_Soft => GetString("EqPreset_Soft");
    public static string EqPreset_Techno => GetString("EqPreset_Techno");
    public static string EqPreset_Vocal => GetString("EqPreset_Vocal");

    // ReplayGain Progress
    public static string ReplayGain_AllSongsScanned => GetString("ReplayGain_AllSongsScanned");
    public static string ReplayGain_CalculatingCount => GetString("ReplayGain_CalculatingCount");
    public static string ReplayGain_CalculatingProgress => GetString("ReplayGain_CalculatingProgress");
    public static string ReplayGain_Complete => GetString("ReplayGain_Complete");
}
