using Microsoft.UI.Xaml.Controls;

namespace Nagi.WinUI.Controls;

/// <summary>
///     Content that supplies the main window's custom title bar.
/// </summary>
public interface ICustomTitleBarProvider
{
    /// <summary>
    ///     Gets the TitleBar control that serves as the custom title bar's content and drag region.
    /// </summary>
    TitleBar GetAppTitleBarElement();

}
