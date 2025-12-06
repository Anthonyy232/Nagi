namespace Nagi.Core.Services.Data;

/// <summary>
///     Defines the available sorting orders for a list of songs.
/// </summary>
public enum SongSortOrder
{
    TitleAsc,
    TitleDesc,
    DateAddedDesc,
    DateAddedAsc,
    DateModifiedDesc,
    DateModifiedAsc,
    AlbumAsc,
    ArtistAsc,
    TrackNumberAsc
}