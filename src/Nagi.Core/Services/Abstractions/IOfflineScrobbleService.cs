namespace Nagi.Core.Services.Abstractions;

/// <summary>
///     Defines a service that runs in the background to process and submit
///     scrobbles that were queued while offline or due to API failures.
/// </summary>
public interface IOfflineScrobbleService
{
    /// <summary>
    ///     Starts the background processing task.
    /// </summary>
    void Start();

    /// <summary>
    ///     Triggers an immediate, on-demand check and processing of the scrobble queue.
    /// </summary>
    /// <returns>A task that completes when the processing is finished.</returns>
    Task ProcessQueueAsync(CancellationToken cancellationToken = default);
}