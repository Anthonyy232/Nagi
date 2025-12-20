namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines a service for fetching lyrics from an online source.
/// </summary>
public interface IOnlineLyricsService
{
    /// <summary>
    ///     Fetches synced lyrics for a track.
    /// </summary>
    /// <param name="trackName">The title of the track.</param>
    /// <param name="artistName">The artist of the track.</param>
    /// <param name="albumName">The album regarding the track.</param>
    /// <param name="duration">The duration of the track.</param>
    /// <returns>The raw LRC string content if found; otherwise, null.</returns>
    Task<string?> GetLyricsAsync(string trackName, string artistName, string albumName, TimeSpan duration);
}
