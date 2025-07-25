using System;
using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
/// A concrete implementation of <see cref="INavigationService"/> for WinUI 3 applications.
/// </summary>
public class NavigationService : INavigationService {
    private Frame? _frame;

    public void Initialize(Frame frame) {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
    }

    public void Navigate(Type pageType, object? parameter = null) {
        if (_frame is null) {
            // This is a critical configuration error that should not happen in a properly configured app.
            Trace.TraceError("[{0}] Attempted to navigate before the root frame was initialized.", nameof(NavigationService));
            return;
        }

        _frame.Navigate(pageType, parameter);
    }
}