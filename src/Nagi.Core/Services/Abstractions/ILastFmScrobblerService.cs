using Nagi.Core.Models;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines the contract for a service that handles direct communication
///     with the Last.fm scrobbling and "now playing" APIs.
/// </summary>
public interface ILastFmScrobblerService
{
    /// <summary>
    ///     Submits a "Now Playing" update to Last.fm.
    /// </summary>
    /// <param name="song">The song currently playing.</param>
    /// <returns>True if the update was successful, otherwise false.</returns>
    Task<bool> UpdateNowPlayingAsync(Song song);

    /// <summary>
    ///     Submits a scrobble to Last.fm.
    /// </summary>
    /// <param name="song">The song that was played.</param>
    /// <param name="playStartTime">The UTC timestamp when the song started playing.</param>
    /// <returns>True if the scrobble was successful, otherwise false.</returns>
    Task<bool> ScrobbleAsync(Song song, DateTime playStartTime);
}