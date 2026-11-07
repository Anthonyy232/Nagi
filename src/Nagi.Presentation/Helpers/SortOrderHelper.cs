using Nagi.Core.Services.Data;
using CoreStrings = Nagi.Core.Resources.Strings;

namespace Nagi.Presentation.Helpers;

public static class SortOrderHelper
{
    public static string TitleAscLabel => string.Format(CoreStrings.Format_AlphaAsc, CoreStrings.Label_Title);
    public static string TitleDescLabel => string.Format(CoreStrings.Format_AlphaDesc, CoreStrings.Label_Title);
    public static string DateCreatedNewestLabel => string.Format(CoreStrings.Format_TemporalNewest, CoreStrings.Label_DateCreated);
    public static string DateCreatedOldestLabel => string.Format(CoreStrings.Format_TemporalOldest, CoreStrings.Label_DateCreated);
    public static string ModifiedNewestLabel => string.Format(CoreStrings.Format_TemporalNewest, CoreStrings.Label_DateModified);
    public static string ModifiedOldestLabel => string.Format(CoreStrings.Format_TemporalOldest, CoreStrings.Label_DateModified);
    public static string ArtistAscLabel => string.Format(CoreStrings.Format_AlphaAsc, CoreStrings.Label_Artist);
    public static string ArtistDescLabel => string.Format(CoreStrings.Format_AlphaDesc, CoreStrings.Label_Artist);
    public static string AlbumAscLabel => string.Format(CoreStrings.Format_AlphaAsc, CoreStrings.Label_Album);
    public static string AlbumDescLabel => string.Format(CoreStrings.Format_AlphaDesc, CoreStrings.Label_Album);
    public static string YearNewestLabel => string.Format(CoreStrings.Format_TemporalNewest, CoreStrings.Label_Year);
    public static string YearOldestLabel => string.Format(CoreStrings.Format_TemporalOldest, CoreStrings.Label_Year);
    public static string MostSongsLabel => string.Format(CoreStrings.Format_Most, CoreStrings.Label_Songs);
    public static string LeastSongsLabel => string.Format(CoreStrings.Format_Least, CoreStrings.Label_Songs);
    public static string TrackNumberAscLabel => string.Format(CoreStrings.Format_DirectionalAsc, CoreStrings.Label_TrackNumber);
    public static string TrackNumberDescLabel => string.Format(CoreStrings.Format_DirectionalDesc, CoreStrings.Label_TrackNumber);
    public static string PlayCountAscLabel => string.Format(CoreStrings.Format_Least, CoreStrings.Label_PlayCount);
    public static string PlayCountDescLabel => string.Format(CoreStrings.Format_Most, CoreStrings.Label_PlayCount);
    public static string DateAddedAscLabel => string.Format(CoreStrings.Format_TemporalOldest, CoreStrings.Label_DateAdded);
    public static string DateAddedDescLabel => string.Format(CoreStrings.Format_TemporalNewest, CoreStrings.Label_DateAdded);
    public static string LastPlayedAscLabel => string.Format(CoreStrings.Format_TemporalOldest, CoreStrings.Label_LastPlayed);
    public static string LastPlayedDescLabel => string.Format(CoreStrings.Format_TemporalNewest, CoreStrings.Label_LastPlayed);
    public static string DurationAscLabel => CoreStrings.Format_ShortestFirst;
    public static string DurationDescLabel => CoreStrings.Format_LongestFirst;
    public static string BpmAscLabel => string.Format(CoreStrings.Format_SlowestFirst, CoreStrings.Label_Bpm);
    public static string BpmDescLabel => string.Format(CoreStrings.Format_FastestFirst, CoreStrings.Label_Bpm);
    public static string PlaylistOrderLabel => CoreStrings.Label_ManualOrder;
    public static string RandomLabel => CoreStrings.Label_Random;

    public static string GetDisplayName(SongSortOrder sortOrder) =>
        sortOrder == SongSortOrder.PlaylistOrder ? PlaylistOrderLabel : CoreStrings.SortByPrefix + (sortOrder switch
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

    public static string GetDisplayName(PlaylistSortOrder sortOrder) => CoreStrings.SortByPrefix + (sortOrder switch
    {
        PlaylistSortOrder.NameAsc => TitleAscLabel,
        PlaylistSortOrder.NameDesc => TitleDescLabel,
        PlaylistSortOrder.DateCreatedDesc => DateCreatedNewestLabel,
        PlaylistSortOrder.DateCreatedAsc => DateCreatedOldestLabel,
        PlaylistSortOrder.DateModifiedDesc => ModifiedNewestLabel,
        PlaylistSortOrder.DateModifiedAsc => ModifiedOldestLabel,
        _ => TitleAscLabel
    });

    public static string GetDisplayName(GenreSortOrder sortOrder) => CoreStrings.SortByPrefix + (sortOrder switch
    {
        GenreSortOrder.NameAsc => TitleAscLabel,
        GenreSortOrder.NameDesc => TitleDescLabel,
        GenreSortOrder.SongCountDesc => MostSongsLabel,
        GenreSortOrder.SongCountAsc => LeastSongsLabel,
        _ => TitleAscLabel
    });

    public static string GetDisplayName(ArtistSortOrder sortOrder) => CoreStrings.SortByPrefix + (sortOrder switch
    {
        ArtistSortOrder.NameAsc => ArtistAscLabel,
        ArtistSortOrder.NameDesc => ArtistDescLabel,
        ArtistSortOrder.SongCountDesc => MostSongsLabel,
        ArtistSortOrder.SongCountAsc => LeastSongsLabel,
        _ => ArtistAscLabel
    });

    public static string GetDisplayName(AlbumSortOrder sortOrder) => CoreStrings.SortByPrefix + (sortOrder switch
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
}
