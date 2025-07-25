using System;
using Microsoft.UI.Dispatching;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

public class DispatcherService : IDispatcherService {
    private readonly DispatcherQueue _dispatcherQueue;

    public DispatcherService(DispatcherQueue dispatcherQueue) {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
    }

    public void TryEnqueue(Action action) {
        _dispatcherQueue.TryEnqueue(() => action());
    }
}