using Microsoft.UI.Dispatching;
using Nagi.Services.Abstractions;
using System;

namespace Nagi.Services.Implementations.WinUI;

public class WinUIDispatcherService : IDispatcherService {
    private readonly DispatcherQueue _dispatcherQueue;

    public WinUIDispatcherService(DispatcherQueue dispatcherQueue) {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
    }

    public void TryEnqueue(Action action) {
        _dispatcherQueue.TryEnqueue(() => action());
    }
}