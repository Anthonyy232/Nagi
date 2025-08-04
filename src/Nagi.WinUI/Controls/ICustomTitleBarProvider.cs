using Microsoft.UI.Xaml.Controls;

namespace Nagi.WinUI.Controls;

/// <summary>
///     Defines a contract for pages that provide custom XAML elements to be used
///     for the application's title bar.
/// </summary>
public interface ICustomTitleBarProvider
{
    /// <summary>
    ///     Gets the TitleBar control that serves as the custom title bar's content and drag region.
    /// </summary>
    /// <returns>The TitleBar control for the current page.</returns>
    TitleBar GetAppTitleBarElement();

    /// <summary>
    ///     Gets the RowDefinition that contains the title bar element.
    ///     This allows for collapsing the row when the title bar is not needed, such as on the onboarding page.
    /// </summary>
    /// <returns>The RowDefinition for the title bar.</returns>
    RowDefinition GetAppTitleBarRowElement();
}