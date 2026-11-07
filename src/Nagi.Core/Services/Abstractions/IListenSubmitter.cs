using Nagi.Core.Library;

namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     A place listens are sent to, such as Last.fm, ListenBrainz or the music servers songs stream from. Each one
///     decides from its own settings and sign-in whether it is turned on, and does nothing when it isn't.
/// </summary>
public interface IListenSubmitter
{
    /// <summary>
    ///     A name for logs.
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Says what is playing now, when that is turned on. Failures are logged.
    /// </summary>
    Task UpdateNowPlayingAsync(Song song);

    /// <summary>
    ///     Sends the listens that reached the scrobble threshold and haven't been sent, oldest first, marking each as it
    ///     goes. A listen the destination rejects for good is dropped; after any other failure the rest wait for the
    ///     next run, in order. Runs one at a time, so a listen is never sent twice.
    /// </summary>
    Task SubmitPendingAsync(CancellationToken cancellationToken);
}
