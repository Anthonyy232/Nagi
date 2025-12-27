using Nagi.Core.Services.Data;

namespace Nagi.Core.Helpers;

public static class SortOrderHelper
{
    // Base Labels (for Dropdowns)
    public const string AToZLabel = "A to Z";
    public const string ZToALabel = "Z to A";
    public const string NewestLabel = "Newest";
    public const string OldestLabel = "Oldest";
    public const string ModifiedNewestLabel = "Date Modified (Newest)";
    public const string ModifiedOldestLabel = "Date Modified (Oldest)";
    public const string MostSongsLabel = "Most Songs";
    public const string AlbumLabel = "Album";
    public const string ArtistLabel = "Artist";
    public const string DiscLabel = "Disc";
    public const string YearNewestLabel = "Year (Newest)";
    public const string YearOldestLabel = "Year (Oldest)";
    public const string TrackNumberLabel = "Disc";

    public const string TitleAscLabel = "Title (A to Z)";
    public const string TitleDescLabel = "Title (Z to A)";
    public const string ArtistAscLabel = "Artist (A to Z)";
    public const string ArtistDescLabel = "Artist (Z to A)";
    public const string AlbumAscLabel = "Album (A to Z)";
    public const string AlbumDescLabel = "Album (Z to A)";
    public const string RandomLabel = "Random";

    // Full Display Text (for Buttons/Tooltips)
    public const string AToZ = $"Sort By: {AToZLabel}";
    public const string ZToA = $"Sort By: {ZToALabel}";
    public const string Newest = $"Sort By: {NewestLabel}";
    public const string Oldest = $"Sort By: {OldestLabel}";
    public const string ModifiedNewest = $"Sort By: {ModifiedNewestLabel}";
    public const string ModifiedOldest = $"Sort By: {ModifiedOldestLabel}";
    public const string MostSongs = $"Sort By: {MostSongsLabel}";
    public const string Album = $"Sort By: {AlbumLabel}";
    public const string Artist = $"Sort By: {ArtistLabel}";
    public const string Disc = $"Sort By: {DiscLabel}";
    public const string YearNewest = $"Sort By: {YearNewestLabel}";
    public const string YearOldest = $"Sort By: {YearOldestLabel}";

    public static string GetDisplayName(SongSortOrder sortOrder) => sortOrder switch
    {
        SongSortOrder.TitleAsc => AToZ,
        SongSortOrder.TitleDesc => ZToA,
        SongSortOrder.DateAddedDesc => Newest,
        SongSortOrder.DateAddedAsc => Oldest,
        SongSortOrder.DateModifiedDesc => ModifiedNewest,
        SongSortOrder.DateModifiedAsc => ModifiedOldest,
        SongSortOrder.AlbumAsc => Album,
        SongSortOrder.ArtistAsc => Artist,
        SongSortOrder.TrackNumberAsc => Disc,
        _ => AToZ
    };

    public static string GetDisplayName(PlaylistSortOrder sortOrder) => sortOrder switch
    {
        PlaylistSortOrder.NameAsc => AToZ,
        PlaylistSortOrder.NameDesc => ZToA,
        PlaylistSortOrder.DateCreatedDesc => Newest,
        PlaylistSortOrder.DateCreatedAsc => Oldest,
        PlaylistSortOrder.DateModifiedDesc => ModifiedNewest,
        _ => AToZ
    };

    public static string GetDisplayName(GenreSortOrder sortOrder) => sortOrder switch
    {
        GenreSortOrder.NameAsc => AToZ,
        GenreSortOrder.NameDesc => ZToA,
        GenreSortOrder.SongCountDesc => MostSongs,
        _ => AToZ
    };

    public static string GetDisplayName(ArtistSortOrder sortOrder) => sortOrder switch
    {
        ArtistSortOrder.NameAsc => AToZ,
        ArtistSortOrder.NameDesc => ZToA,
        ArtistSortOrder.SongCountDesc => MostSongs,
        _ => AToZ
    };

    public static string GetDisplayName(AlbumSortOrder sortOrder) => sortOrder switch
    {
        AlbumSortOrder.AlbumTitleAsc => Album,
        AlbumSortOrder.YearDesc => YearNewest,
        AlbumSortOrder.YearAsc => YearOldest,
        AlbumSortOrder.ArtistAsc => Artist,
        _ => Artist
    };
}
