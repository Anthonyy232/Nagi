using Nagi.Presentation.Services.Abstractions;

namespace Nagi.Presentation.Tests;

/// <summary>Runs dispatched callbacks immediately for view-model tests without a XAML application.</summary>
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
