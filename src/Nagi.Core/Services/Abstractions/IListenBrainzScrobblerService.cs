using Nagi.Core.Models;
using Nagi.Core.Services.Data;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines direct HTTP communication with the ListenBrainz submit-listens API.
/// </summary>
public interface IListenBrainzScrobblerService
{
    /// <summary>Submits a "playing_now" update.</summary>
    /// <returns>True on success, false on any failure.</returns>
    Task<bool> UpdateNowPlayingAsync(Song song);

    /// <summary>
    ///     Submits a "single" listen with the given playback start timestamp.
    /// </summary>
    /// <returns>True on success, false on any failure (including 4xx/5xx after retries).</returns>
    Task<bool> SubmitListenAsync(Song song, DateTime playStartTimeUtc);

    /// <summary>
    ///     Validates a user token against the ListenBrainz API. Used by the settings UI.
    /// </summary>
    /// <param name="token">The user token to test.</param>
    /// <param name="serverUrl">Optional override of the base URL; null uses the default.</param>
    Task<ValidateTokenResult> ValidateTokenAsync(string token, string? serverUrl = null);
}
