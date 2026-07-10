namespace Nagi.Core.Services.Data;

/// <summary>
///     Defines the available sorting orders for a list of songs.
/// </summary>
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
    PlaylistOrder
}
