using System;
using System.Threading.Tasks;

namespace Nagi.Core.Services.Abstractions;

public interface IDispatcherService
{
    bool HasThreadAccess { get; }

    bool TryEnqueue(Action action);

    /// <summary>
    ///     Schedules the provided asynchronous function on the UI thread and awaits its completion.
    /// </summary>
    Task EnqueueAsync(Func<Task> function);

    Task<T> EnqueueAsync<T>(Func<T> function);
}
