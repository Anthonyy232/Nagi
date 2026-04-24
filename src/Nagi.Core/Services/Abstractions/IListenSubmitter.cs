namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     An abstraction for "something that can submit past listens" to a scrobbling service.
///     Consumed by <see cref="IOfflineScrobbleService" /> to fan out queue processing across
///     multiple scrobbling destinations (e.g. Last.fm, ListenBrainz).
/// </summary>
public interface IListenSubmitter
{
    /// <summary>
    ///     Stable identifier for the submitter (e.g. "lastfm", "listenbrainz").
    /// </summary>
    string Id { get; }

    /// <summary>
    ///     Returns true if this submitter is currently enabled by user settings and has the
    ///     credentials/configuration needed to submit.
    /// </summary>
    Task<bool> IsEnabledAsync();

    /// <summary>
    ///     Processes this submitter's pending listen queue: queries the DB for listens that are
    ///     eligible and not yet submitted to this destination, submits each in timestamp order,
    ///     marks successes in the DB, and stops on the first failure to preserve order.
    /// </summary>
    Task ProcessPendingListensAsync(CancellationToken cancellationToken);
}
