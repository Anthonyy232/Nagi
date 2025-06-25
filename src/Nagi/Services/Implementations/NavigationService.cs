using System;
using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Nagi.Services.Abstractions;

namespace Nagi.Services.Implementations;

/// <summary>
///     A concrete implementation of INavigationService for WinUI 3 applications.
/// </summary>
public class NavigationService : INavigationService
{
    private Frame? _frame;

    /// <summary>
    ///     Initializes the navigation service with the application's root frame.
    ///     This must be called once after the main window's frame is created.
    /// </summary>
    /// <param name="frame">The root frame of the application used for navigation.</param>
    public void Initialize(Frame frame)
    {
        _frame = frame;
    }

    /// <summary>
    ///     Navigates to the specified page type.
    /// </summary>
    /// <param name="pageType">The type of the page to navigate to.</param>
    /// <param name="parameter">An optional parameter to pass to the target page.</param>
    public void Navigate(Type pageType, object? parameter = null)
    {
        if (_frame != null)
            _frame.Navigate(pageType, parameter);
        else
            Debug.WriteLine("[NavigationService] ERROR: Navigation frame has not been initialized.");
    }
}