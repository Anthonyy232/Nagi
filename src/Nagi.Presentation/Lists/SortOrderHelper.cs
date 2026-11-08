using Nagi.Core.Library;
using Nagi.Core.Playlists;
using CoreStrings = Nagi.Core.Resources.Strings;
using Nagi.Presentation.Common;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.Lists;

public static class SortOrderHelper
{
    public static string TitleAscLabel => ResourceFormatter.Format(Strings.Format_AlphaAsc, CoreStrings.Label_Title);
    public static string TitleDescLabel => ResourceFormatter.Format(Strings.Format_AlphaDesc, CoreStrings.Label_Title);
    public static string DateCreatedNewestLabel => ResourceFormatter.Format(Strings.Format_TemporalNewest, Strings.Label_DateCreated);
    public static string DateCreatedOldestLabel => ResourceFormatter.Format(Strings.Format_TemporalOldest, Strings.Label_DateCreated);
    public static string ModifiedNewestLabel => ResourceFormatter.Format(Strings.Format_TemporalNewest, Strings.Label_DateModified);
    public static string ModifiedOldestLabel => ResourceFormatter.Format(Strings.Format_TemporalOldest, Strings.Label_DateModified);
    public static string ArtistAscLabel => ResourceFormatter.Format(Strings.Format_AlphaAsc, CoreStrings.Label_Artist);
    public static string ArtistDescLabel => ResourceFormatter.Format(Strings.Format_AlphaDesc, CoreStrings.Label_Artist);
    public static string AlbumAscLabel => ResourceFormatter.Format(Strings.Format_AlphaAsc, CoreStrings.Label_Album);
    public static string AlbumDescLabel => ResourceFormatter.Format(Strings.Format_AlphaDesc, CoreStrings.Label_Album);
    public static string YearNewestLabel => ResourceFormatter.Format(Strings.Format_TemporalNewest, Strings.Label_Year);
    public static string YearOldestLabel => ResourceFormatter.Format(Strings.Format_TemporalOldest, Strings.Label_Year);
    public static string MostSongsLabel => ResourceFormatter.Format(Strings.Format_Most, Strings.Label_Songs);
    public static string LeastSongsLabel => ResourceFormatter.Format(Strings.Format_Least, Strings.Label_Songs);
    public static string TrackNumberAscLabel => ResourceFormatter.Format(Strings.Format_DirectionalAsc, Strings.Label_TrackNumber);
    public static string TrackNumberDescLabel => ResourceFormatter.Format(Strings.Format_DirectionalDesc, Strings.Label_TrackNumber);
    public static string PlayCountAscLabel => ResourceFormatter.Format(Strings.Format_Least, Strings.Label_PlayCount);
    public static string PlayCountDescLabel => ResourceFormatter.Format(Strings.Format_Most, Strings.Label_PlayCount);
    public static string DateAddedAscLabel => ResourceFormatter.Format(Strings.Format_TemporalOldest, Strings.Label_DateAdded);
    public static string DateAddedDescLabel => ResourceFormatter.Format(Strings.Format_TemporalNewest, Strings.Label_DateAdded);
    public static string LastPlayedAscLabel => ResourceFormatter.Format(Strings.Format_TemporalOldest, Strings.Label_LastPlayed);
    public static string LastPlayedDescLabel => ResourceFormatter.Format(Strings.Format_TemporalNewest, Strings.Label_LastPlayed);
    public static string DurationAscLabel => Strings.Format_ShortestFirst;
    public static string DurationDescLabel => Strings.Format_LongestFirst;
    public static string BpmAscLabel => ResourceFormatter.Format(Strings.Format_SlowestFirst, Strings.Label_Bpm);
    public static string BpmDescLabel => ResourceFormatter.Format(Strings.Format_FastestFirst, Strings.Label_Bpm);
    public static string PlaylistOrderLabel => Strings.Label_ManualOrder;
    public static string RandomLabel => Strings.Label_Random;

    /// <summary>The order as a sort menu lists it.</summary>
    public static string GetLabel(Enum sortOrder) => sortOrder switch
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
        SongSortOrder.PlaylistOrder => PlaylistOrderLabel,

        PlaylistSortOrder.NameDesc => TitleDescLabel,
        PlaylistSortOrder.DateCreatedDesc => DateCreatedNewestLabel,
        PlaylistSortOrder.DateCreatedAsc => DateCreatedOldestLabel,
        PlaylistSortOrder.DateModifiedDesc => ModifiedNewestLabel,
        PlaylistSortOrder.DateModifiedAsc => ModifiedOldestLabel,
        PlaylistSortOrder => TitleAscLabel,

        GenreSortOrder.NameDesc => TitleDescLabel,
        GenreSortOrder.SongCountDesc => MostSongsLabel,
        GenreSortOrder.SongCountAsc => LeastSongsLabel,
        GenreSortOrder => TitleAscLabel,

        ArtistSortOrder.NameDesc => ArtistDescLabel,
        ArtistSortOrder.SongCountDesc => MostSongsLabel,
        ArtistSortOrder.SongCountAsc => LeastSongsLabel,
        ArtistSortOrder => ArtistAscLabel,

        AlbumSortOrder.ArtistDesc => ArtistDescLabel,
        AlbumSortOrder.AlbumTitleAsc => AlbumAscLabel,
        AlbumSortOrder.AlbumTitleDesc => AlbumDescLabel,
        AlbumSortOrder.YearDesc => YearNewestLabel,
        AlbumSortOrder.YearAsc => YearOldestLabel,
        AlbumSortOrder.SongCountDesc => MostSongsLabel,
        AlbumSortOrder.SongCountAsc => LeastSongsLabel,
        AlbumSortOrder => ArtistAscLabel,

        _ => TitleAscLabel
    };

    /// <summary>The order as a list's sort button names it.</summary>
    public static string GetDisplayName(Enum sortOrder) =>
        sortOrder is SongSortOrder.PlaylistOrder ? PlaylistOrderLabel : Strings.SortByPrefix + GetLabel(sortOrder);
}
