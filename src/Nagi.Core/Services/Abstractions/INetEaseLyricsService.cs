namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Fetches synchronized lyrics from NetEase Cloud Music. Often has lyrics for Asian music
///     (J-Pop, K-Pop, anime) that LRCLIB lacks.
/// </summary>
public interface INetEaseLyricsService
{
    /// <summary>
    ///     Searches for synchronized lyrics on NetEase Cloud Music.
    /// </summary>
    /// <returns>LRC content if found; null when the lookup succeeds without a match.</returns>
    /// <remarks>Provider failures and cancellation propagate to the caller so they remain retryable.</remarks>
    Task<string?> SearchLyricsAsync(string trackName, string? artistName, CancellationToken cancellationToken = default);
}
