using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page for configuring application settings.
/// </summary>
public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<SettingsViewModel>();
        // Set the DataContext for XAML bindings.
        DataContext = ViewModel;
    }

    /// <summary>
    ///     Gets the ViewModel associated with this page.
    /// </summary>
    public SettingsViewModel ViewModel { get; }

    /// <summary>
    ///     Loads the settings when the page is navigated to.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadSettingsAsync();
    }

    /// <summary>
    ///     Handles the page's navigated-from event.
    ///     This is the critical cleanup step that disposes the ViewModel to prevent memory leaks.
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        // This is the crucial addition to prevent memory leaks from the ViewModel.
        ViewModel.Dispose();
    }
}