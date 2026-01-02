namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Service for fetching synchronized lyrics from NetEase Cloud Music.
///     Used as a fallback for Asian music (J-Pop, K-Pop, Anime) when LRCLIB fails.
/// </summary>
public interface INetEaseLyricsService
{
    /// <summary>
    ///     Searches for synchronized lyrics on NetEase Cloud Music.
    /// </summary>
    /// <param name="trackName">The track name.</param>
    /// <param name="artistName">The artist name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>LRC content if found, null otherwise.</returns>
    Task<string?> SearchLyricsAsync(string trackName, string? artistName, CancellationToken cancellationToken = default);
}
