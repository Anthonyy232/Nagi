using Nagi.Core.Platform;

namespace Nagi.Core.Tests.Utils;

/// <summary>Runs dispatched callbacks at once, on the calling thread.</summary>
internal sealed class ImmediateDispatcher : IDispatcherService
{
    public bool HasThreadAccess => true;

    public bool TryEnqueue(Action action)
    {
        action();
        return true;
    }

    public Task EnqueueAsync(Func<Task> function) => function();
    public Task<T> EnqueueAsync<T>(Func<T> function) => Task.FromResult(function());
}
