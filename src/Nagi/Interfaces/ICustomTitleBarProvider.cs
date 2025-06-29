// Nagi/Interfaces/ICustomTitleBarProvider.cs
using Microsoft.UI.Xaml.Controls;

namespace Nagi.Interfaces;

/// <summary>
/// Defines a contract for pages that provide custom XAML elements to be used for the application's title bar.
/// </summary>
public interface ICustomTitleBarProvider {
    /// <summary>
    /// Gets the Grid element that serves as the custom title bar's content and drag region.
    /// </summary>
    /// <returns>The Grid element for the title bar.</returns>
    Grid GetAppTitleBarElement();

    /// <summary>
    /// Gets the RowDefinition that contains the title bar element.
    /// This allows for collapsing the row when the title bar is not needed.
    /// </summary>
    /// <returns>The RowDefinition for the title bar.</returns>
    RowDefinition GetAppTitleBarRowElement();
}