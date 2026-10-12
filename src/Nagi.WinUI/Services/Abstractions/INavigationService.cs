using System;
using Microsoft.UI.Xaml.Controls;

namespace Nagi.WinUI.Services.Abstractions;

public interface INavigationService
{
    void Initialize(Frame frame);

    void Navigate(Type pageType, object? parameter = null);

    /// <summary>
    ///     Navigates to the detail page that accepts <paramref name="parameter" />, one of the
    ///     <c>*NavigationParameter</c> records in <c>Nagi.WinUI.Navigation</c>.
    /// </summary>
    void NavigateTo(object parameter);
}
