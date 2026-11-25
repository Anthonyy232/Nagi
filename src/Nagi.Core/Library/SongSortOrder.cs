namespace Nagi.Core.Library;

public enum SongSortOrder
{
    TitleAsc,
    TitleDesc,
    AlbumAsc,
    AlbumDesc,
    ArtistAsc,
    ArtistDesc,
    TrackNumberAsc,
    TrackNumberDesc,
    YearAsc,
    YearDesc,
    PlayCountAsc,
    PlayCountDesc,
    LastPlayedAsc,
    LastPlayedDesc,
    DateAddedAsc,
    DateAddedDesc,
    DurationAsc,
    DurationDesc,
    BpmAsc,
    BpmDesc,
    FileCreatedDateAsc,
    FileCreatedDateDesc,
    Random,
    PlaylistOrder,
    FileModifiedDateAsc,
    FileModifiedDateDesc,

    // By a tag the list names, as smart playlists do; other lists sort these by title.
    TagAsc,
    TagDesc
}
