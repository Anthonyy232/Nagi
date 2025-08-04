using System;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.UI.Dispatching;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     A concrete implementation of <see cref="IDispatcherService" /> that wraps the
///     application's main <see cref="DispatcherQueue" />.
/// </summary>
public class DispatcherService : IDispatcherService
{
    private readonly DispatcherQueue _dispatcherQueue;

    public DispatcherService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
    }

    /// <inheritdoc />
    public bool TryEnqueue(Action action)
    {
        return _dispatcherQueue.TryEnqueue(() => action());
    }

    /// <inheritdoc />
    public Task EnqueueAsync(Func<Task> function)
    {
        return _dispatcherQueue.EnqueueAsync(function);
    }
}