namespace Nagi.Core.Platform;

public interface IDispatcherService
{
    bool HasThreadAccess { get; }

    /// <summary>Returns false instead of throwing once the UI thread has shut down.</summary>
    bool TryEnqueue(Action action);

    /// <summary>The task faults if the UI thread has shut down, so callers that can run at exit must catch.</summary>
    Task EnqueueAsync(Func<Task> function);

    /// <inheritdoc cref="EnqueueAsync(Func{Task})" />
    Task<T> EnqueueAsync<T>(Func<T> function);
}
