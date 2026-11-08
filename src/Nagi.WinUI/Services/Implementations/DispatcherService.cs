using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.UI.Dispatching;
using Nagi.Core.Platform;

namespace Nagi.WinUI.Services.Implementations;

public class DispatcherService : IDispatcherService
{
    private readonly DispatcherQueue _dispatcherQueue;

    public DispatcherService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    public bool HasThreadAccess => _dispatcherQueue.HasThreadAccess;

    public bool TryEnqueue(Action action) => _dispatcherQueue.TryEnqueue(() => action());

    public Task EnqueueAsync(Func<Task> function) => _dispatcherQueue.EnqueueAsync(function);

    public Task<T> EnqueueAsync<T>(Func<T> function) => _dispatcherQueue.EnqueueAsync(function);
}
