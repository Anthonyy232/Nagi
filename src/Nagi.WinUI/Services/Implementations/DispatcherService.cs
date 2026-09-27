using System;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.UI.Dispatching;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     A concrete implementation of <see cref="IDispatcherService" /> that wraps the
///     application's main <see cref="DispatcherQueue" />. Includes shutdown-safe error handling.
/// </summary>
public class DispatcherService : IDispatcherService
{
    private const int RO_E_CLOSED = unchecked((int)0x80000013);
    private const int RPC_E_WRONG_THREAD = unchecked((int)0x8001010E);

    private readonly DispatcherQueue _dispatcherQueue;

    public DispatcherService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    /// <inheritdoc />
    public bool HasThreadAccess => _dispatcherQueue.HasThreadAccess;

    /// <inheritdoc />
    public bool TryEnqueue(Action action)
    {
        try
        {
            return _dispatcherQueue.TryEnqueue(() => action());
        }
        catch (Exception ex) when (ex.HResult == RO_E_CLOSED || ex.HResult == RPC_E_WRONG_THREAD)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task EnqueueAsync(Func<Task> function)
    {
        try
        {
            return _dispatcherQueue.EnqueueAsync(function);
        }
        catch (Exception ex) when (ex.HResult == RO_E_CLOSED || ex.HResult == RPC_E_WRONG_THREAD)
        {
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task<T> EnqueueAsync<T>(Func<T> function)
    {
        try
        {
            return _dispatcherQueue.EnqueueAsync(function);
        }
        catch (Exception ex) when (ex.HResult == RO_E_CLOSED || ex.HResult == RPC_E_WRONG_THREAD)
        {
            return Task.FromResult(default(T)!);
        }
    }
}
