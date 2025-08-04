using System;
using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.Services.Implementations;

/// <summary>
///     Provides a service for navigating between pages within a XAML Frame.
/// </summary>
public class NavigationService : INavigationService, IDisposable
{
    // The minimum time that must pass between navigation requests.
    private static readonly TimeSpan NavigationDebounceThreshold = TimeSpan.FromMilliseconds(500);

    private Frame? _frame;
    private bool _isDisposed;
    private DateTime _lastNavigationTime = DateTime.MinValue;
    private Type? _lastPageType;
    private object? _lastParameter;

    /// <summary>
    ///     Cleans up resources by unsubscribing from the Frame's Navigated event.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;

        if (_frame != null) _frame.Navigated -= OnFrameNavigated;

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Initializes the navigation service with the application's root frame.
    /// </summary>
    /// <param name="frame">The root frame used for navigation.</param>
    public void Initialize(Frame frame)
    {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
        _frame.Navigated += OnFrameNavigated;
    }

    /// <summary>
    ///     Navigates to the specified page type.
    /// </summary>
    /// <remarks>
    ///     This method includes checks to prevent navigation if the request is identical to the
    ///     current page and parameter, or if a navigation occurred within the debounce threshold.
    /// </remarks>
    /// <param name="pageType">The type of the page to navigate to.</param>
    /// <param name="parameter">An optional parameter to pass to the target page.</param>
    public void Navigate(Type pageType, object? parameter = null)
    {
        // Debounce rapid navigation requests to prevent unintended double-clicks
        if (DateTime.UtcNow - _lastNavigationTime < NavigationDebounceThreshold)
        {
            Debug.WriteLine($"[NavigationService] Navigation to {pageType.Name} debounced.");
            return;
        }

        if (_frame is null)
        {
            // This is a critical failure, as navigation is impossible without an initialized frame.
            Trace.TraceError("[NavigationService] Attempted to navigate before the root frame was initialized.");
            return;
        }

        // Prevent navigating to the same page with the same parameter, which is usually redundant.
        if (_lastPageType == pageType && Equals(_lastParameter, parameter))
        {
            Debug.WriteLine($"[NavigationService] Navigation to same page {pageType.Name} prevented.");
            return;
        }

        _lastNavigationTime = DateTime.UtcNow;
        _frame.Navigate(pageType, parameter);
    }

    /// <summary>
    ///     Updates the service's state after a navigation has completed.
    /// </summary>
    private void OnFrameNavigated(object sender, NavigationEventArgs e)
    {
        _lastPageType = e.SourcePageType;
        _lastParameter = e.Parameter;
    }
}