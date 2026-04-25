using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nagi.WinUI.Helpers;

/// <summary>
///     Re-triggerable delay: each <see cref="Trigger"/> call cancels the previous
///     pending invocation and schedules a new one after <see cref="_delay"/>.
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly TimeSpan _delay;
    private CancellationTokenSource? _cts;

    public Debouncer(TimeSpan delay) { _delay = delay; }

    public void Trigger(Func<CancellationToken, Task> action)
    {
        var newCts = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref _cts, newCts);
        try { old?.Cancel(); old?.Dispose(); } catch (ObjectDisposedException) { }

        var token = newCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_delay, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested)
                    await action(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    public void Dispose()
    {
        var old = Interlocked.Exchange(ref _cts, null);
        try { old?.Cancel(); old?.Dispose(); } catch (ObjectDisposedException) { }
    }
}
