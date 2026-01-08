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

    // Base Labels (for Dropdowns)
    public const string TitleAscLabel = "Title (A to Z)";
    public const string TitleDescLabel = "Title (Z to A)";
    public const string DateCreatedNewestLabel = "Date Created (Newest)";
    public const string DateCreatedOldestLabel = "Date Created (Oldest)";
    public const string ModifiedNewestLabel = "Date Modified (Newest)";
    public const string ModifiedOldestLabel = "Date Modified (Oldest)";
    public const string ArtistAscLabel = "Artist (A to Z)";
    public const string ArtistDescLabel = "Artist (Z to A)";
    public const string AlbumAscLabel = "Album (A to Z)";
    public const string AlbumDescLabel = "Album (Z to A)";
    public const string YearNewestLabel = "Year (Newest)";
    public const string YearOldestLabel = "Year (Oldest)";
    public const string MostSongsLabel = "Most Songs";
    public const string LeastSongsLabel = "Least Songs";
    public const string TrackNumberAscLabel = "Track Number (Asc)";
    public const string TrackNumberDescLabel = "Track Number (Desc)";
    public const string PlayCountAscLabel = "Least Played";
    public const string PlayCountDescLabel = "Most Played";
    public const string DateAddedAscLabel = "Date Added (Oldest)";
    public const string DateAddedDescLabel = "Date Added (Newest)";
    public const string LastPlayedAscLabel = "Last Played (Oldest)";
    public const string LastPlayedDescLabel = "Last Played (Newest)";
    public const string DurationAscLabel = "Shortest First";
    public const string DurationDescLabel = "Longest First";
    public const string BpmAscLabel = "Slowest First (BPM)";
    public const string BpmDescLabel = "Fastest First (BPM)";
    public const string RandomLabel = "Random";

    // Full Display Text (for Buttons/Tooltips)
    public const string SortByTitleAsc = $"Sort By: {TitleAscLabel}";
    public const string SortByTitleDesc = $"Sort By: {TitleDescLabel}";
    public const string SortByArtistAsc = $"Sort By: {ArtistAscLabel}";
    public const string SortByArtistDesc = $"Sort By: {ArtistDescLabel}";
    public const string SortByAlbumAsc = $"Sort By: {AlbumAscLabel}";
    public const string SortByAlbumDesc = $"Sort By: {AlbumDescLabel}";
    public const string SortByYearNewest = $"Sort By: {YearNewestLabel}";
    public const string SortByYearOldest = $"Sort By: {YearOldestLabel}";
    public const string SortByMostSongs = $"Sort By: {MostSongsLabel}";
    public const string SortByLeastSongs = $"Sort By: {LeastSongsLabel}";
    public const string SortByTrackNumberAsc = $"Sort By: {TrackNumberAscLabel}";
    public const string SortByTrackNumberDesc = $"Sort By: {TrackNumberDescLabel}";
    public const string SortByPlayCountAsc = $"Sort By: {PlayCountAscLabel}";
    public const string SortByPlayCountDesc = $"Sort By: {PlayCountDescLabel}";
    public const string SortByDateAddedAsc = $"Sort By: {DateAddedAscLabel}";
    public const string SortByDateAddedDesc = $"Sort By: {DateAddedDescLabel}";
    public const string SortByLastPlayedAsc = $"Sort By: {LastPlayedAscLabel}";
    public const string SortByLastPlayedDesc = $"Sort By: {LastPlayedDescLabel}";
    public const string SortByDurationAsc = $"Sort By: {DurationAscLabel}";
    public const string SortByDurationDesc = $"Sort By: {DurationDescLabel}";
    public const string SortByBpmAsc = $"Sort By: {BpmAscLabel}";
    public const string SortByBpmDesc = $"Sort By: {BpmDescLabel}";
    public const string SortByRandom = $"Sort By: {RandomLabel}";

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
        _ => SortByTitleAsc
    };

    public static string GetDisplayName(PlaylistSortOrder sortOrder) => sortOrder switch
    {
        PlaylistSortOrder.NameAsc => SortByTitleAsc,
        PlaylistSortOrder.NameDesc => SortByTitleDesc,
        PlaylistSortOrder.DateCreatedDesc => DateCreatedNewestLabel,
        PlaylistSortOrder.DateCreatedAsc => DateCreatedOldestLabel,
        PlaylistSortOrder.DateModifiedDesc => ModifiedNewestLabel,
        PlaylistSortOrder.DateModifiedAsc => ModifiedOldestLabel,
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
