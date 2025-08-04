using System;
using System.Threading.Tasks;

namespace Nagi.WinUI.Services.Abstractions;

/// <summary>
///     Abstracts the UI thread dispatcher for safe cross-thread UI updates.
/// </summary>
public interface IDispatcherService
{
    /// <summary>
    ///     Schedules the provided action on the UI thread.
    /// </summary>
    /// <returns>True if the action was enqueued, false otherwise.</returns>
    bool TryEnqueue(Action action);

    /// <summary>
    ///     Schedules the provided asynchronous function on the UI thread and awaits its completion.
    /// </summary>
    Task EnqueueAsync(Func<Task> function);
}