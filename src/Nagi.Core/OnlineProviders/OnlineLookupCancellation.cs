namespace Nagi.Core.OnlineProviders;

/// <summary>
///     Stops the online lookups in flight when the user turns online fetching off. A lookup takes its token before it
///     reads the setting, so it either sees the setting off or is cancelled.
/// </summary>
internal sealed class OnlineLookupCancellation
{
    private readonly object _lock = new();
    private CancellationTokenSource _running = new();

    public CancellationToken Token
    {
        get
        {
            lock (_lock) return _running.Token;
        }
    }

    public void CancelRunning()
    {
        CancellationTokenSource running;
        lock (_lock)
        {
            running = _running;
            _running = new CancellationTokenSource();
        }

        running.Cancel();
    }
}
