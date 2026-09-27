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
        => _ = RunAsync(action);

    public Task RunAsync(Func<CancellationToken, Task> action, bool immediate = false)
    {
        var newCts = new CancellationTokenSource();
        var token = newCts.Token;
        var old = Interlocked.Exchange(ref _cts, newCts);
        old?.Cancel();
        old?.Dispose();

        return InvokeAsync(action, token, immediate);
    }

    private async Task InvokeAsync(Func<CancellationToken, Task> action, CancellationToken token, bool immediate)
    {
        try
        {
            if (!immediate)
                await Task.Delay(_delay, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await action(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    public void Cancel()
    {
        var old = Interlocked.Exchange(ref _cts, null);
        old?.Cancel();
        old?.Dispose();
    }

    public void Dispose() => Cancel();
}
