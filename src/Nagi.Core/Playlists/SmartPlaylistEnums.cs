using Nagi.Core.Library;

namespace Nagi.Core.Playlists;

public enum SmartPlaylistField
{
    Title,
    Artist,
    Album,
    Genre,
    Composer,
    Comment,
    Grouping,

    PlayCount,
    SkipCount,
    Rating,
    Year,
    TrackNumber,
    DiscNumber,
    Bpm,
    Duration,
    Bitrate,
    SampleRate,

    IsLoved,
    HasLyrics,

    DateAdded,
    LastPlayed,
    FileCreatedDate,
    FileModifiedDate
}

public enum SmartPlaylistOperator
{
    Is,
    IsNot,
    Contains,
    DoesNotContain,
    StartsWith,
    EndsWith,

    Equals,
    NotEquals,
    GreaterThan,
    LessThan,
    GreaterThanOrEqual,
    LessThanOrEqual,
    IsInRange,

    // Value is in days.
    IsInTheLast,
    IsNotInTheLast,

    IsTrue,
    IsFalse
}

public enum SmartPlaylistSortOrder
{
    TitleAsc,
    TitleDesc,
    ArtistAsc,
    ArtistDesc,
    AlbumAsc,
    AlbumDesc,
    YearAsc,
    YearDesc,
    PlayCountAsc,
    PlayCountDesc,
    LastPlayedAsc,
    LastPlayedDesc,
    DateAddedAsc,
    DateAddedDesc,
    TrackNumberAsc,
    TrackNumberDesc,
    DurationAsc,
    DurationDesc,
    BpmAsc,
    BpmDesc,
    FileCreatedDateAsc,
    FileCreatedDateDesc,
    Random
}

public static class SmartPlaylistSortOrders
{
    public static SongSortOrder ToSongSortOrder(this SmartPlaylistSortOrder sortOrder) => sortOrder switch
    {
        SmartPlaylistSortOrder.TitleAsc => SongSortOrder.TitleAsc,
        SmartPlaylistSortOrder.TitleDesc => SongSortOrder.TitleDesc,
        SmartPlaylistSortOrder.ArtistAsc => SongSortOrder.ArtistAsc,
        SmartPlaylistSortOrder.ArtistDesc => SongSortOrder.ArtistDesc,
        SmartPlaylistSortOrder.AlbumAsc => SongSortOrder.AlbumAsc,
        SmartPlaylistSortOrder.AlbumDesc => SongSortOrder.AlbumDesc,
        SmartPlaylistSortOrder.YearAsc => SongSortOrder.YearAsc,
        SmartPlaylistSortOrder.YearDesc => SongSortOrder.YearDesc,
        SmartPlaylistSortOrder.PlayCountAsc => SongSortOrder.PlayCountAsc,
        SmartPlaylistSortOrder.PlayCountDesc => SongSortOrder.PlayCountDesc,
        SmartPlaylistSortOrder.LastPlayedAsc => SongSortOrder.LastPlayedAsc,
        SmartPlaylistSortOrder.LastPlayedDesc => SongSortOrder.LastPlayedDesc,
        SmartPlaylistSortOrder.DateAddedAsc => SongSortOrder.DateAddedAsc,
        SmartPlaylistSortOrder.DateAddedDesc => SongSortOrder.DateAddedDesc,
        SmartPlaylistSortOrder.TrackNumberAsc => SongSortOrder.TrackNumberAsc,
        SmartPlaylistSortOrder.TrackNumberDesc => SongSortOrder.TrackNumberDesc,
        SmartPlaylistSortOrder.DurationAsc => SongSortOrder.DurationAsc,
        SmartPlaylistSortOrder.DurationDesc => SongSortOrder.DurationDesc,
        SmartPlaylistSortOrder.BpmAsc => SongSortOrder.BpmAsc,
        SmartPlaylistSortOrder.BpmDesc => SongSortOrder.BpmDesc,
        SmartPlaylistSortOrder.FileCreatedDateAsc => SongSortOrder.FileCreatedDateAsc,
        SmartPlaylistSortOrder.FileCreatedDateDesc => SongSortOrder.FileCreatedDateDesc,
        SmartPlaylistSortOrder.Random => SongSortOrder.Random,
        _ => SongSortOrder.TitleAsc
    };

    /// <returns><see cref="SmartPlaylistSortOrder.TitleAsc" /> for <see cref="SongSortOrder.PlaylistOrder" />, which a smart playlist doesn't have.</returns>
    public static SmartPlaylistSortOrder ToSmartPlaylistSortOrder(this SongSortOrder sortOrder) => sortOrder switch
    {
        SongSortOrder.TitleAsc => SmartPlaylistSortOrder.TitleAsc,
        SongSortOrder.TitleDesc => SmartPlaylistSortOrder.TitleDesc,
        SongSortOrder.ArtistAsc => SmartPlaylistSortOrder.ArtistAsc,
        SongSortOrder.ArtistDesc => SmartPlaylistSortOrder.ArtistDesc,
        SongSortOrder.AlbumAsc => SmartPlaylistSortOrder.AlbumAsc,
        SongSortOrder.AlbumDesc => SmartPlaylistSortOrder.AlbumDesc,
        SongSortOrder.YearAsc => SmartPlaylistSortOrder.YearAsc,
        SongSortOrder.YearDesc => SmartPlaylistSortOrder.YearDesc,
        SongSortOrder.PlayCountAsc => SmartPlaylistSortOrder.PlayCountAsc,
        SongSortOrder.PlayCountDesc => SmartPlaylistSortOrder.PlayCountDesc,
        SongSortOrder.LastPlayedAsc => SmartPlaylistSortOrder.LastPlayedAsc,
        SongSortOrder.LastPlayedDesc => SmartPlaylistSortOrder.LastPlayedDesc,
        SongSortOrder.DateAddedAsc => SmartPlaylistSortOrder.DateAddedAsc,
        SongSortOrder.DateAddedDesc => SmartPlaylistSortOrder.DateAddedDesc,
        SongSortOrder.TrackNumberAsc => SmartPlaylistSortOrder.TrackNumberAsc,
        SongSortOrder.TrackNumberDesc => SmartPlaylistSortOrder.TrackNumberDesc,
        SongSortOrder.DurationAsc => SmartPlaylistSortOrder.DurationAsc,
        SongSortOrder.DurationDesc => SmartPlaylistSortOrder.DurationDesc,
        SongSortOrder.BpmAsc => SmartPlaylistSortOrder.BpmAsc,
        SongSortOrder.BpmDesc => SmartPlaylistSortOrder.BpmDesc,
        SongSortOrder.FileCreatedDateAsc => SmartPlaylistSortOrder.FileCreatedDateAsc,
        SongSortOrder.FileCreatedDateDesc => SmartPlaylistSortOrder.FileCreatedDateDesc,
        SongSortOrder.Random => SmartPlaylistSortOrder.Random,
        _ => SmartPlaylistSortOrder.TitleAsc
    };
}
