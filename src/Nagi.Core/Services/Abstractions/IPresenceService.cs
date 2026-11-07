using Nagi.Core.Library;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Shows what is playing outside Nagi, such as on the user's Discord profile. <c>PresenceManager</c> feeds it
///     playback while it is turned on, and disposes it when it is turned off.
/// </summary>
public interface IPresenceService : IAsyncDisposable
{
    /// <summary>
    ///     Whether the user has it turned on. Read again whenever a setting changes.
    /// </summary>
    bool IsEnabled { get; }

    Task InitializeAsync();

    Task OnTrackChangedAsync(Song song);

    Task OnPlaybackStateChangedAsync(bool isPlaying);

    /// <summary>
    ///     Playback stopped, or the service is about to be turned off.
    /// </summary>
    Task OnPlaybackStoppedAsync();

    Task OnTrackProgressAsync(TimeSpan progress, TimeSpan duration);
}
