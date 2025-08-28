using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A page for configuring application settings.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly ILogger<SettingsPage> _logger;

    public SettingsPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<SettingsViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<SettingsPage>>();
        DataContext = ViewModel;
        _logger.LogInformation("SettingsPage initialized.");
    }

    public SettingsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _logger.LogInformation("Navigated to SettingsPage. Loading settings...");
        try
        {
            await ViewModel.LoadSettingsAsync();
            _logger.LogInformation("Settings loaded successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings.");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logger.LogInformation("Navigating away from SettingsPage. Disposing ViewModel.");
        ViewModel.Dispose();
    }
}