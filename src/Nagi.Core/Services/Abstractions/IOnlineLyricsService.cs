namespace Nagi.Core.Services.Abstractions;

public interface IOnlineLyricsService
{
    /// <summary>
    ///     Fetches synchronized lyrics for a track, falling back to plain text.
    /// </summary>
    /// <returns>Raw LRC or plain text if found; null when the lookup succeeds without a match.</returns>
    /// <remarks>Provider failures and cancellation propagate to the caller so they remain retryable.</remarks>
    Task<string?> GetLyricsAsync(string trackName, string? artistName, string? albumName, TimeSpan duration, CancellationToken cancellationToken = default);
}
