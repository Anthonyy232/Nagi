namespace Nagi.Core.Library;

// Smart playlists store these as numbers: add new values at the end.
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
    FileModifiedDateDesc
}
