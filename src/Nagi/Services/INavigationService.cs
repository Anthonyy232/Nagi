using System;
using Microsoft.UI.Xaml.Controls;

namespace Nagi.Services;

/// <summary>
///     Defines a contract for a service that handles view navigation.
/// </summary>
public interface INavigationService
{
    /// <summary>
    ///     Initializes the navigation service with the application's root frame.
    ///     This must be called once after the main window's frame is created.
    /// </summary>
    /// <param name="frame">The root frame of the application used for navigation.</param>
    void Initialize(Frame frame);

    /// <summary>
    ///     Navigates to the specified page type.
    /// </summary>
    /// <param name="pageType">The type of the page to navigate to.</param>
    /// <param name="parameter">An optional parameter to pass to the target page.</param>
    void Navigate(Type pageType, object? parameter = null);
}