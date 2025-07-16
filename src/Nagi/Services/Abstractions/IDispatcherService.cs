using System;

namespace Nagi.Services.Abstractions;

/// <summary>
/// Abstracts the UI thread dispatcher for safe cross-thread UI updates.
/// </summary>
public interface IDispatcherService {
    void TryEnqueue(Action action);
}