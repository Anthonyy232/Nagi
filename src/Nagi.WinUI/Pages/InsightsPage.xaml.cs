using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.WinUI.ViewModels;

namespace Nagi.WinUI.Pages;

/// <summary>
///     A dashboard page that displays listening insights and statistics.
/// </summary>
public sealed partial class InsightsPage : Page
{
    private readonly ILogger<InsightsPage> _logger;

    public InsightsPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<InsightsViewModel>();
        _logger = App.Services!.GetRequiredService<ILogger<InsightsPage>>();
        DataContext = ViewModel;
    }

    public InsightsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        try
        {
            base.OnNavigatedTo(e);
            _logger.LogDebug("Navigated to InsightsPage.");
            await ViewModel.LoadInsightsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed during InsightsPage navigation.");
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _logger.LogDebug("Navigating away from InsightsPage.");
    }
}
