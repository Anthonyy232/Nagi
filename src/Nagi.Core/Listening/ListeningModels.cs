namespace Nagi.Core.Listening;

public sealed record RecentListen(long Id, Guid SongId, string Title, string ArtistName, DateTime TimestampUtc)
{
    /// <summary>
    ///     The most listens one page of recent history holds.
    /// </summary>
    public const int MaxPageSize = 100;
}

public sealed record RediscoverySuggestion(Guid SongId, string Title, string ArtistName, DateTime LastPlayedDate);
