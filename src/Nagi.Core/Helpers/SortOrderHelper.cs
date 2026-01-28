using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Helpers;

public static class SortOrderHelper
{
    // Settings Keys
    public const string LibrarySortOrderKey = "SortOrder_Library";
    public const string AlbumsSortOrderKey = "SortOrder_Albums";
    public const string ArtistsSortOrderKey = "SortOrder_Artists";
    public const string GenresSortOrderKey = "SortOrder_Genres";
    public const string PlaylistsSortOrderKey = "SortOrder_Playlists";
    public const string FolderViewSortOrderKey = "SortOrder_FolderView";
    public const string AlbumViewSortOrderKey = "SortOrder_AlbumView";
    public const string ArtistViewSortOrderKey = "SortOrder_ArtistView";
    public const string GenreViewSortOrderKey = "SortOrder_GenreView";

    // Base Labels
    public static string TitleAscLabel => string.Format(Resources.Strings.Format_AlphaAsc, Resources.Strings.Label_Title);
    public static string TitleDescLabel => string.Format(Resources.Strings.Format_AlphaDesc, Resources.Strings.Label_Title);
    public static string DateCreatedNewestLabel => string.Format(Resources.Strings.Format_TemporalNewest, Resources.Strings.Label_DateCreated);
    public static string DateCreatedOldestLabel => string.Format(Resources.Strings.Format_TemporalOldest, Resources.Strings.Label_DateCreated);
    public static string ModifiedNewestLabel => string.Format(Resources.Strings.Format_TemporalNewest, Resources.Strings.Label_DateModified);
    public static string ModifiedOldestLabel => string.Format(Resources.Strings.Format_TemporalOldest, Resources.Strings.Label_DateModified);
    public static string ArtistAscLabel => string.Format(Resources.Strings.Format_AlphaAsc, Resources.Strings.Label_Artist);
    public static string ArtistDescLabel => string.Format(Resources.Strings.Format_AlphaDesc, Resources.Strings.Label_Artist);
    public static string AlbumAscLabel => string.Format(Resources.Strings.Format_AlphaAsc, Resources.Strings.Label_Album);
    public static string AlbumDescLabel => string.Format(Resources.Strings.Format_AlphaDesc, Resources.Strings.Label_Album);
    public static string YearNewestLabel => string.Format(Resources.Strings.Format_TemporalNewest, Resources.Strings.Label_Year);
    public static string YearOldestLabel => string.Format(Resources.Strings.Format_TemporalOldest, Resources.Strings.Label_Year);
    public static string MostSongsLabel => string.Format(Resources.Strings.Format_Most, Resources.Strings.Label_Songs);
    public static string LeastSongsLabel => string.Format(Resources.Strings.Format_Least, Resources.Strings.Label_Songs);
    public static string TrackNumberAscLabel => string.Format(Resources.Strings.Format_DirectionalAsc, Resources.Strings.Label_TrackNumber);
    public static string TrackNumberDescLabel => string.Format(Resources.Strings.Format_DirectionalDesc, Resources.Strings.Label_TrackNumber);
    public static string PlayCountAscLabel => string.Format(Resources.Strings.Format_Least, Resources.Strings.Label_PlayCount);
    public static string PlayCountDescLabel => string.Format(Resources.Strings.Format_Most, Resources.Strings.Label_PlayCount);
    public static string DateAddedAscLabel => string.Format(Resources.Strings.Format_TemporalOldest, Resources.Strings.Label_DateAdded);
    public static string DateAddedDescLabel => string.Format(Resources.Strings.Format_TemporalNewest, Resources.Strings.Label_DateAdded);
    public static string LastPlayedAscLabel => string.Format(Resources.Strings.Format_TemporalOldest, Resources.Strings.Label_LastPlayed);
    public static string LastPlayedDescLabel => string.Format(Resources.Strings.Format_TemporalNewest, Resources.Strings.Label_LastPlayed);
    public static string DurationAscLabel => Resources.Strings.Format_ShortestFirst;
    public static string DurationDescLabel => Resources.Strings.Format_LongestFirst;
    public static string BpmAscLabel => string.Format(Resources.Strings.Format_SlowestFirst, Resources.Strings.Label_Bpm);
    public static string BpmDescLabel => string.Format(Resources.Strings.Format_FastestFirst, Resources.Strings.Label_Bpm);
    public static string PlaylistOrderLabel => Resources.Strings.Label_ManualOrder;
    public static string RandomLabel => Resources.Strings.Label_Random;

    // Full Display Text (for Buttons/Tooltips)
    public static string SortByTitleAsc => $"{Resources.Strings.SortByPrefix}{TitleAscLabel}";
    public static string SortByTitleDesc => $"{Resources.Strings.SortByPrefix}{TitleDescLabel}";
    public static string SortByArtistAsc => $"{Resources.Strings.SortByPrefix}{ArtistAscLabel}";
    public static string SortByArtistDesc => $"{Resources.Strings.SortByPrefix}{ArtistDescLabel}";
    public static string SortByAlbumAsc => $"{Resources.Strings.SortByPrefix}{AlbumAscLabel}";
    public static string SortByAlbumDesc => $"{Resources.Strings.SortByPrefix}{AlbumDescLabel}";
    public static string SortByYearNewest => $"{Resources.Strings.SortByPrefix}{YearNewestLabel}";
    public static string SortByYearOldest => $"{Resources.Strings.SortByPrefix}{YearOldestLabel}";
    public static string SortByMostSongs => $"{Resources.Strings.SortByPrefix}{MostSongsLabel}";
    public static string SortByLeastSongs => $"{Resources.Strings.SortByPrefix}{LeastSongsLabel}";
    public static string SortByTrackNumberAsc => $"{Resources.Strings.SortByPrefix}{TrackNumberAscLabel}";
    public static string SortByTrackNumberDesc => $"{Resources.Strings.SortByPrefix}{TrackNumberDescLabel}";
    public static string SortByPlayCountAsc => $"{Resources.Strings.SortByPrefix}{PlayCountAscLabel}";
    public static string SortByPlayCountDesc => $"{Resources.Strings.SortByPrefix}{PlayCountDescLabel}";
    public static string SortByDateAddedAsc => $"{Resources.Strings.SortByPrefix}{DateAddedAscLabel}";
    public static string SortByDateAddedDesc => $"{Resources.Strings.SortByPrefix}{DateAddedDescLabel}";
    public static string SortByLastPlayedAsc => $"{Resources.Strings.SortByPrefix}{LastPlayedAscLabel}";
    public static string SortByLastPlayedDesc => $"{Resources.Strings.SortByPrefix}{LastPlayedDescLabel}";
    public static string SortByDurationAsc => $"{Resources.Strings.SortByPrefix}{DurationAscLabel}";
    public static string SortByDurationDesc => $"{Resources.Strings.SortByPrefix}{DurationDescLabel}";
    public static string SortByBpmAsc => $"{Resources.Strings.SortByPrefix}{BpmAscLabel}";
    public static string SortByBpmDesc => $"{Resources.Strings.SortByPrefix}{BpmDescLabel}";
    public static string SortByRandom => $"{Resources.Strings.SortByPrefix}{RandomLabel}";

    public static string GetDisplayName(SongSortOrder sortOrder) => sortOrder switch
    {
        SongSortOrder.TitleAsc => SortByTitleAsc,
        SongSortOrder.TitleDesc => SortByTitleDesc,
        SongSortOrder.AlbumAsc => SortByAlbumAsc,
        SongSortOrder.AlbumDesc => SortByAlbumDesc,
        SongSortOrder.YearAsc => SortByYearOldest,
        SongSortOrder.YearDesc => SortByYearNewest,
        SongSortOrder.ArtistAsc => SortByArtistAsc,
        SongSortOrder.ArtistDesc => SortByArtistDesc,
        SongSortOrder.TrackNumberAsc => SortByTrackNumberAsc,
        SongSortOrder.TrackNumberDesc => SortByTrackNumberDesc,
        SongSortOrder.PlaylistOrder => Resources.Strings.Label_ManualOrder,
        _ => SortByTitleAsc
    };

    public static string GetDisplayName(PlaylistSortOrder sortOrder) => sortOrder switch
    {
        PlaylistSortOrder.NameAsc => SortByTitleAsc,
        PlaylistSortOrder.NameDesc => SortByTitleDesc,
        PlaylistSortOrder.DateCreatedDesc => $"{Resources.Strings.SortByPrefix}{string.Format(Resources.Strings.Format_TemporalNewest, Resources.Strings.Label_DateCreated)}",
        PlaylistSortOrder.DateCreatedAsc => $"{Resources.Strings.SortByPrefix}{string.Format(Resources.Strings.Format_TemporalOldest, Resources.Strings.Label_DateCreated)}",
        PlaylistSortOrder.DateModifiedDesc => $"{Resources.Strings.SortByPrefix}{string.Format(Resources.Strings.Format_TemporalNewest, Resources.Strings.Label_DateModified)}",
        PlaylistSortOrder.DateModifiedAsc => $"{Resources.Strings.SortByPrefix}{string.Format(Resources.Strings.Format_TemporalOldest, Resources.Strings.Label_DateModified)}",
        _ => SortByTitleAsc
    };

    public static string GetDisplayName(GenreSortOrder sortOrder) => sortOrder switch
    {
        GenreSortOrder.NameAsc => SortByTitleAsc,
        GenreSortOrder.NameDesc => SortByTitleDesc,
        GenreSortOrder.SongCountDesc => SortByMostSongs,
        GenreSortOrder.SongCountAsc => SortByLeastSongs,
        _ => SortByTitleAsc
    };

    public static string GetDisplayName(ArtistSortOrder sortOrder) => sortOrder switch
    {
        ArtistSortOrder.NameAsc => SortByTitleAsc,
        ArtistSortOrder.NameDesc => SortByTitleDesc,
        ArtistSortOrder.SongCountDesc => SortByMostSongs,
        ArtistSortOrder.SongCountAsc => SortByLeastSongs,
        _ => SortByTitleAsc
    };

    public static string GetDisplayName(AlbumSortOrder sortOrder) => sortOrder switch
    {
        AlbumSortOrder.ArtistAsc => SortByArtistAsc,
        AlbumSortOrder.ArtistDesc => SortByArtistDesc,
        AlbumSortOrder.AlbumTitleAsc => SortByAlbumAsc,
        AlbumSortOrder.AlbumTitleDesc => SortByAlbumDesc,
        AlbumSortOrder.YearDesc => SortByYearNewest,
        AlbumSortOrder.YearAsc => SortByYearOldest,
        AlbumSortOrder.SongCountDesc => SortByMostSongs,
        AlbumSortOrder.SongCountAsc => SortByLeastSongs,
        _ => SortByArtistAsc
    };

    public static string GetDisplayName(SmartPlaylistSortOrder sortOrder) => sortOrder switch
    {
        SmartPlaylistSortOrder.TitleAsc => SortByTitleAsc,
        SmartPlaylistSortOrder.TitleDesc => SortByTitleDesc,
        SmartPlaylistSortOrder.ArtistAsc => SortByArtistAsc,
        SmartPlaylistSortOrder.ArtistDesc => SortByArtistDesc,
        SmartPlaylistSortOrder.AlbumAsc => SortByAlbumAsc,
        SmartPlaylistSortOrder.AlbumDesc => SortByAlbumDesc,
        SmartPlaylistSortOrder.YearAsc => SortByYearOldest,
        SmartPlaylistSortOrder.YearDesc => SortByYearNewest,
        SmartPlaylistSortOrder.PlayCountAsc => SortByPlayCountAsc,
        SmartPlaylistSortOrder.PlayCountDesc => SortByPlayCountDesc,
        SmartPlaylistSortOrder.LastPlayedAsc => SortByLastPlayedAsc,
        SmartPlaylistSortOrder.LastPlayedDesc => SortByLastPlayedDesc,
        SmartPlaylistSortOrder.DateAddedAsc => SortByDateAddedAsc,
        SmartPlaylistSortOrder.DateAddedDesc => SortByDateAddedDesc,
        SmartPlaylistSortOrder.TrackNumberAsc => SortByTrackNumberAsc,
        SmartPlaylistSortOrder.TrackNumberDesc => SortByTrackNumberDesc,
        SmartPlaylistSortOrder.DurationAsc => SortByDurationAsc,
        SmartPlaylistSortOrder.DurationDesc => SortByDurationDesc,
        SmartPlaylistSortOrder.BpmAsc => SortByBpmAsc,
        SmartPlaylistSortOrder.BpmDesc => SortByBpmDesc,
        SmartPlaylistSortOrder.Random => SortByRandom,
        _ => SortByTitleAsc
    };
}
