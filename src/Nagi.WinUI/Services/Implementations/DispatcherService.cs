using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.UI.Dispatching;
using Nagi.Core.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

public class DispatcherService : IDispatcherService
{
    private const int RO_E_CLOSED = unchecked((int)0x80000013);
    private const int RPC_E_WRONG_THREAD = unchecked((int)0x8001010E);

    private readonly DispatcherQueue _dispatcherQueue;

    public DispatcherService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    public bool HasThreadAccess => _dispatcherQueue.HasThreadAccess;

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
