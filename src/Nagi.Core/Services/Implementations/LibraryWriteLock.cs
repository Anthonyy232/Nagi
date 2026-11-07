using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Nagi.Core.Services.Implementations;

/// <summary>
///     Lets one bulk library write run at a time (a scan, a server import, a folder removal, a reset), and lets a
///     removal or reset cancel the scans and server syncs it would otherwise wait for. Its shutdown token stops every
///     library operation when the app closes.
/// </summary>
public sealed class LibraryWriteLock : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _serverSyncs = new();
    private readonly ILogger<LibraryWriteLock> _logger;
    private CancellationTokenSource? _activeScanCts;
    private int _disposeState;

    public LibraryWriteLock(ILogger<LibraryWriteLock> logger)
    {
        _logger = logger;
    }

    public CancellationToken ShutdownToken => _shutdownCts.Token;

    public Task WaitAsync(CancellationToken cancellationToken) => _semaphore.WaitAsync(cancellationToken);

    public void Release() => _semaphore.Release();

    /// <summary>
    ///     Marks the scan the user started, which a later scan, removal or reset cancels.
    /// </summary>
    public void SetActiveScan(CancellationTokenSource cts) => Volatile.Write(ref _activeScanCts, cts);

    public void ClearActiveScan(CancellationTokenSource cts) => Interlocked.CompareExchange(ref _activeScanCts, null, cts);

    public void CancelActiveScan(string reason)
    {
        var activeScan = Volatile.Read(ref _activeScanCts);
        if (activeScan is null || activeScan.IsCancellationRequested) return;

        _logger.LogInformation("Cancelling active library scan before {Reason}.", reason);
        TryCancel(activeScan);
    }

    /// <summary>
    ///     Removing a server cancels its sync, which would otherwise hold the removal up until the import finished.
    /// </summary>
    public void SetServerSync(Guid folderId, CancellationTokenSource cts) => _serverSyncs[folderId] = cts;

    public void ClearServerSync(Guid folderId) => _serverSyncs.TryRemove(folderId, out _);

    public void CancelServerSync(Guid folderId)
    {
        if (_serverSyncs.TryGetValue(folderId, out var sync)) TryCancel(sync);
    }

    public void CancelServerSyncs()
    {
        foreach (var sync in _serverSyncs.Values) TryCancel(sync);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;
        // The semaphore has no wait handle, so it stays usable while cancelled operations unwind.
        _shutdownCts.Cancel();
    }

    private static void TryCancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation finished while it was being cancelled.
        }
    }
}
