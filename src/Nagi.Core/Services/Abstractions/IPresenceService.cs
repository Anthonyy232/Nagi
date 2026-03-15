using Nagi.Core.Models;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines a standard interface for services that report the application's
///     current playback status to external platforms, such as Discord or Last.fm.
///     This interface supports asynchronous disposal of resources.
/// </summary>
public interface IPresenceService : IAsyncDisposable
{
    /// <summary>
    ///     Gets the unique, non-localized name of the service (e.g., "Discord", "Last.fm").
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Initializes the service, connecting to external clients or APIs.
    ///     This is typically called once when the service is activated.
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    ///     Handles the event when the currently playing track changes or playback begins.
    /// </summary>
    /// <param name="song">The new song that is playing.</param>
    /// <param name="listenHistoryId">The unique ID for this specific listening session from the database.</param>
    Task OnTrackChangedAsync(Song song, long listenHistoryId);

    /// <summary>
    ///     Handles the event when playback is paused or resumed.
    /// </summary>
    /// <param name="isPlaying">True if playback is active, false if paused.</param>
    Task OnPlaybackStateChangedAsync(bool isPlaying);

    /// <summary>
    ///     Handles the event when playback is stopped and the queue is cleared.
    /// </summary>
    Task OnPlaybackStoppedAsync();

    /// <summary>
    ///     Handles periodic updates on the progress of the current track.
    /// </summary>
    /// <param name="progress">The current position in the track.</param>
    /// <param name="duration">The total duration of the track.</param>
    Task OnTrackProgressAsync(TimeSpan progress, TimeSpan duration);

    /// <summary>
    ///     Called exactly once per listening session when the playback engine determines the track
    ///     has crossed the scrobble eligibility threshold.
    ///     Presence services that submit to scrobbling APIs (e.g. Last.fm) should perform their
    ///     submission here rather than re-implementing the threshold check in
    ///     <see cref="OnTrackProgressAsync" />.
    /// </summary>
    /// <param name="song">The song that is now eligible.</param>
    /// <param name="listenHistoryId">The listen session ID to associate with the submission.</param>
    Task OnTrackEligibleForScrobblingAsync(Song song, long listenHistoryId);
}