using Nagi.Core.Library;
using Nagi.Core.Listening;
using Nagi.Core.Playlists;
using CoreStrings = Nagi.Core.Resources.Strings;
using Nagi.Presentation.Common;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.Lists;

public static class SortOrderHelper
{
    private static string TitleAscLabel => ResourceFormatter.Format(Strings.Format_AlphaAsc, CoreStrings.Label_Title);
    private static string TitleDescLabel => ResourceFormatter.Format(Strings.Format_AlphaDesc, CoreStrings.Label_Title);
    private static string DateCreatedNewestLabel => ResourceFormatter.Format(Strings.Format_TemporalNewest, Strings.Label_DateCreated);
    private static string DateCreatedOldestLabel => ResourceFormatter.Format(Strings.Format_TemporalOldest, Strings.Label_DateCreated);
    private static string DateModifiedNewestLabel => ResourceFormatter.Format(Strings.Format_TemporalNewest, Strings.Label_DateModified);
    private static string DateModifiedOldestLabel => ResourceFormatter.Format(Strings.Format_TemporalOldest, Strings.Label_DateModified);
    private static string ModifiedNewestLabel => ResourceFormatter.Format(Strings.Format_TemporalNewest, Strings.Label_DateModified);
    private static string ModifiedOldestLabel => ResourceFormatter.Format(Strings.Format_TemporalOldest, Strings.Label_DateModified);
    private static string ArtistAscLabel => ResourceFormatter.Format(Strings.Format_AlphaAsc, CoreStrings.Label_Artist);
    private static string ArtistDescLabel => ResourceFormatter.Format(Strings.Format_AlphaDesc, CoreStrings.Label_Artist);
    private static string AlbumAscLabel => ResourceFormatter.Format(Strings.Format_AlphaAsc, CoreStrings.Label_Album);
    private static string AlbumDescLabel => ResourceFormatter.Format(Strings.Format_AlphaDesc, CoreStrings.Label_Album);
    private static string YearNewestLabel => ResourceFormatter.Format(Strings.Format_TemporalNewest, Strings.Label_Year);
    private static string YearOldestLabel => ResourceFormatter.Format(Strings.Format_TemporalOldest, Strings.Label_Year);
    private static string MostSongsLabel => ResourceFormatter.Format(Strings.Format_Most, Strings.Label_Songs);
    private static string LeastSongsLabel => ResourceFormatter.Format(Strings.Format_Least, Strings.Label_Songs);
    private static string TrackNumberAscLabel => ResourceFormatter.Format(Strings.Format_DirectionalAsc, Strings.Label_TrackNumber);
    private static string TrackNumberDescLabel => ResourceFormatter.Format(Strings.Format_DirectionalDesc, Strings.Label_TrackNumber);
    private static string PlayCountAscLabel => ResourceFormatter.Format(Strings.Format_Least, Strings.Label_PlayCount);
    private static string PlayCountDescLabel => ResourceFormatter.Format(Strings.Format_Most, Strings.Label_PlayCount);
    private static string DateAddedAscLabel => ResourceFormatter.Format(Strings.Format_TemporalOldest, Strings.Label_DateAdded);
    private static string DateAddedDescLabel => ResourceFormatter.Format(Strings.Format_TemporalNewest, Strings.Label_DateAdded);
    private static string LastPlayedAscLabel => ResourceFormatter.Format(Strings.Format_TemporalOldest, Strings.Label_LastPlayed);
    private static string LastPlayedDescLabel => ResourceFormatter.Format(Strings.Format_TemporalNewest, Strings.Label_LastPlayed);
    private static string DurationAscLabel => Strings.Format_ShortestFirst;
    private static string DurationDescLabel => Strings.Format_LongestFirst;
    private static string BpmAscLabel => ResourceFormatter.Format(Strings.Format_SlowestFirst, Strings.Label_Bpm);
    private static string BpmDescLabel => ResourceFormatter.Format(Strings.Format_FastestFirst, Strings.Label_Bpm);
    private static string TagAscLabel => ResourceFormatter.Format(Strings.Format_AlphaAsc, Strings.Label_CustomTag);
    private static string TagDescLabel => ResourceFormatter.Format(Strings.Format_AlphaDesc, Strings.Label_CustomTag);
    private static string PlaylistOrderLabel => Strings.Label_ManualOrder;
    private static string RandomLabel => Strings.Label_Random;

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
        SongSortOrder.FileModifiedDateAsc => DateModifiedOldestLabel,
        SongSortOrder.FileModifiedDateDesc => DateModifiedNewestLabel,
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
        SongSortOrder.TagAsc => TagAscLabel,
        SongSortOrder.TagDesc => TagDescLabel,
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

        SortMetric.PlayCount => Strings.InsightsPage_SortBy_PlayCount,
        SortMetric.Duration => Strings.InsightsPage_SortBy_Duration,

        _ => TitleAscLabel
    };

    /// <summary>The order as a list's sort button names it.</summary>
    public static string GetDisplayName(Enum sortOrder) =>
        sortOrder is SongSortOrder.PlaylistOrder ? PlaylistOrderLabel : ResourceFormatter.Format(Strings.SortBy_Format, GetLabel(sortOrder));
}
