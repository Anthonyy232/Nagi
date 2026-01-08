namespace Nagi.Core.Models;

/// <summary>
///     Fields that can be used in smart playlist rules.
/// </summary>
public enum SmartPlaylistField
{
    // Text Fields
    Title,
    Artist,
    Album,
    Genre,
    Composer,
    Comment,
    Grouping,

    // Numeric Fields
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

    // Boolean Fields
    IsLoved,
    HasLyrics,

    // Date Fields
    DateAdded,
    LastPlayed,
    FileCreatedDate,
    FileModifiedDate
}

/// <summary>
///     Comparison operators for smart playlist rules.
/// </summary>
public enum SmartPlaylistOperator
{
    // Text operators
    Is,
    IsNot,
    Contains,
    DoesNotContain,
    StartsWith,
    EndsWith,

    // Numeric/Date operators
    Equals,
    NotEquals,
    GreaterThan,
    LessThan,
    GreaterThanOrEqual,
    LessThanOrEqual,
    IsInRange,

    // Date-specific operators (value is in days)
    IsInTheLast,
    IsNotInTheLast,

    // Boolean operators
    IsTrue,
    IsFalse
}

/// <summary>
///     Sort order for smart playlist results.
/// </summary>
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
    Random
}
