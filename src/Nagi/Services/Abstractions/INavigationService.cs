using System;
using Microsoft.UI.Xaml.Controls;

namespace Nagi.Services.Abstractions;

/// <summary>
/// Defines a contract for a service that manages view navigation within the application.
/// </summary>
public interface INavigationService {
    /// <summary>
    /// Initializes the service with the root frame used for navigation.
    /// </summary>
    /// <param name="frame">The application's root navigation frame.</param>
    void Initialize(Frame frame);

    /// <summary>
    /// Navigates to a page of the specified type.
    /// </summary>
    /// <param name="pageType">The type of the destination page.</param>
    /// <param name="parameter">Optional parameter to pass to the destination page.</param>
    void Navigate(Type pageType, object? parameter = null);
}