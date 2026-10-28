using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Helpers;

public static class SortOrderHelper
{
    public const string LibrarySortOrderKey = "SortOrder_Library";
    public const string AlbumsSortOrderKey = "SortOrder_Albums";
    public const string ArtistsSortOrderKey = "SortOrder_Artists";
    public const string GenresSortOrderKey = "SortOrder_Genres";
    public const string PlaylistsSortOrderKey = "SortOrder_Playlists";
    public const string FolderViewSortOrderKey = "SortOrder_FolderView";
    public const string AlbumViewSortOrderKey = "SortOrder_AlbumView";
    public const string ArtistViewSortOrderKey = "SortOrder_ArtistView";
    public const string GenreViewSortOrderKey = "SortOrder_GenreView";

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

    public static string GetDisplayName(SongSortOrder sortOrder) =>
        sortOrder == SongSortOrder.PlaylistOrder ? PlaylistOrderLabel : Resources.Strings.SortByPrefix + (sortOrder switch
        {
            SongSortOrder.TitleAsc => TitleAscLabel,
            SongSortOrder.TitleDesc => TitleDescLabel,
            SongSortOrder.AlbumAsc => AlbumAscLabel,
            SongSortOrder.AlbumDesc => AlbumDescLabel,
            SongSortOrder.YearAsc => YearOldestLabel,
            SongSortOrder.YearDesc => YearNewestLabel,
            SongSortOrder.FileCreatedDateAsc => DateCreatedOldestLabel,
            SongSortOrder.FileCreatedDateDesc => DateCreatedNewestLabel,
            SongSortOrder.ArtistAsc => ArtistAscLabel,
            SongSortOrder.ArtistDesc => ArtistDescLabel,
            SongSortOrder.TrackNumberAsc => TrackNumberAscLabel,
            SongSortOrder.TrackNumberDesc => TrackNumberDescLabel,
            SongSortOrder.PlayCountAsc => PlayCountAscLabel,
            SongSortOrder.PlayCountDesc => PlayCountDescLabel,
            SongSortOrder.LastPlayedAsc => LastPlayedAscLabel,
            SongSortOrder.LastPlayedDesc => LastPlayedDescLabel,
            SongSortOrder.DateAddedAsc => DateAddedAscLabel,
            SongSortOrder.DateAddedDesc => DateAddedDescLabel,
            SongSortOrder.DurationAsc => DurationAscLabel,
            SongSortOrder.DurationDesc => DurationDescLabel,
            SongSortOrder.BpmAsc => BpmAscLabel,
            SongSortOrder.BpmDesc => BpmDescLabel,
            SongSortOrder.Random => RandomLabel,
            _ => TitleAscLabel
        });

    public static string GetDisplayName(PlaylistSortOrder sortOrder) => Resources.Strings.SortByPrefix + (sortOrder switch
    {
        PlaylistSortOrder.NameAsc => TitleAscLabel,
        PlaylistSortOrder.NameDesc => TitleDescLabel,
        PlaylistSortOrder.DateCreatedDesc => DateCreatedNewestLabel,
        PlaylistSortOrder.DateCreatedAsc => DateCreatedOldestLabel,
        PlaylistSortOrder.DateModifiedDesc => ModifiedNewestLabel,
        PlaylistSortOrder.DateModifiedAsc => ModifiedOldestLabel,
        _ => TitleAscLabel
    });

    public static string GetDisplayName(GenreSortOrder sortOrder) => Resources.Strings.SortByPrefix + (sortOrder switch
    {
        GenreSortOrder.NameAsc => TitleAscLabel,
        GenreSortOrder.NameDesc => TitleDescLabel,
        GenreSortOrder.SongCountDesc => MostSongsLabel,
        GenreSortOrder.SongCountAsc => LeastSongsLabel,
        _ => TitleAscLabel
    });

    public static string GetDisplayName(ArtistSortOrder sortOrder) => Resources.Strings.SortByPrefix + (sortOrder switch
    {
        ArtistSortOrder.NameAsc => TitleAscLabel,
        ArtistSortOrder.NameDesc => TitleDescLabel,
        ArtistSortOrder.SongCountDesc => MostSongsLabel,
        ArtistSortOrder.SongCountAsc => LeastSongsLabel,
        _ => TitleAscLabel
    });

    public static string GetDisplayName(AlbumSortOrder sortOrder) => Resources.Strings.SortByPrefix + (sortOrder switch
    {
        AlbumSortOrder.ArtistAsc => ArtistAscLabel,
        AlbumSortOrder.ArtistDesc => ArtistDescLabel,
        AlbumSortOrder.AlbumTitleAsc => AlbumAscLabel,
        AlbumSortOrder.AlbumTitleDesc => AlbumDescLabel,
        AlbumSortOrder.YearDesc => YearNewestLabel,
        AlbumSortOrder.YearAsc => YearOldestLabel,
        AlbumSortOrder.SongCountDesc => MostSongsLabel,
        AlbumSortOrder.SongCountAsc => LeastSongsLabel,
        _ => ArtistAscLabel
    });

    /// <summary>
    ///     Maps a <see cref="SongSortOrder" /> to the equivalent <see cref="SmartPlaylistSortOrder" /> by matching
    ///     enum member names. Falls back to <see cref="SmartPlaylistSortOrder.TitleAsc" /> for members without a
    ///     counterpart (e.g. <see cref="SongSortOrder.PlaylistOrder" />).
    /// </summary>
    public static SmartPlaylistSortOrder MapToSmartPlaylistSortOrder(SongSortOrder sortOrder) =>
        Enum.TryParse<SmartPlaylistSortOrder>(sortOrder.ToString(), out var mapped)
            ? mapped
            : SmartPlaylistSortOrder.TitleAsc;

    /// <summary>
    ///     Maps a <see cref="SmartPlaylistSortOrder" /> to the equivalent <see cref="SongSortOrder" /> by matching
    ///     enum member names. Falls back to <see cref="SongSortOrder.TitleAsc" /> when no counterpart exists.
    /// </summary>
    public static SongSortOrder MapToSongSortOrder(SmartPlaylistSortOrder sortOrder) =>
        Enum.TryParse<SongSortOrder>(sortOrder.ToString(), out var mapped)
            ? mapped
            : SongSortOrder.TitleAsc;
}
