using Microsoft.UI.Xaml.Controls;

namespace Nagi.Interfaces;

/// <summary>
///     Defines a contract for pages that provide a custom XAML element to be used as the application's title bar.
/// </summary>
public interface ICustomTitleBarProvider
{
    /// <summary>
    ///     Gets the Grid element that serves as the custom title bar.
    /// </summary>
    /// <returns>The Grid element for the title bar.</returns>
    Grid GetAppTitleBarElement();
}