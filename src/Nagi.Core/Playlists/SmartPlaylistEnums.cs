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

    /// <summary>One of <see cref="Library.Song.Tags" />, which <see cref="SmartPlaylistRule.TagName" /> names.</summary>
    Tag,

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
