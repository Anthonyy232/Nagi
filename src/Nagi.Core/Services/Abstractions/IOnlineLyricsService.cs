namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines a service for fetching lyrics from an online source.
/// </summary>
public interface IOnlineLyricsService
{
    /// <summary>
    ///     Fetches synchronized lyrics for a track, falling back to plain text.
    /// </summary>
    /// <param name="trackName">The title of the track.</param>
    /// <param name="artistName">The artist of the track.</param>
    /// <param name="albumName">The album regarding the track.</param>
    /// <param name="duration">The duration of the track.</param>
    /// <returns>Raw LRC or plain text if found; null when the lookup succeeds without a match.</returns>
    /// <remarks>Provider failures and cancellation propagate to the caller so they remain retryable.</remarks>
    Task<string?> GetLyricsAsync(string trackName, string? artistName, string? albumName, TimeSpan duration, CancellationToken cancellationToken = default);
}
