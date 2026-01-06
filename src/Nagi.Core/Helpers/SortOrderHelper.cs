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
    public const string TrackNumberLabel = "Track Number";
    public const string MostPlayedLabel = "Most Played";
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
    public const string SortByTrackNumber = $"Sort By: {TrackNumberLabel}";
    public const string SortByRandom = $"Sort By: {RandomLabel}";

    public static string GetDisplayName(SongSortOrder sortOrder) => sortOrder switch
    {
        SongSortOrder.TitleAsc => SortByTitleAsc,
        SongSortOrder.AlbumAsc => SortByAlbumAsc,
        SongSortOrder.AlbumDesc => SortByAlbumDesc,
        SongSortOrder.YearAsc => SortByYearOldest,
        SongSortOrder.YearDesc => SortByYearNewest,
        SongSortOrder.ArtistAsc => SortByArtistAsc,
        SongSortOrder.TrackNumberAsc => SortByTrackNumber,
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
        _ => SortByTitleAsc
    };

    public static string GetDisplayName(ArtistSortOrder sortOrder) => sortOrder switch
    {
        ArtistSortOrder.NameAsc => SortByTitleAsc,
        ArtistSortOrder.NameDesc => SortByTitleDesc,
        ArtistSortOrder.SongCountDesc => SortByMostSongs,
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
        _ => SortByArtistAsc
    };
}
