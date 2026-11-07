using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nagi.Presentation.Insights;

namespace Nagi.WinUI.Pages;

public sealed partial class InsightsPage : Page
{
    public InsightsPage()
    {
        InitializeComponent();
        ViewModel = App.Services!.GetRequiredService<InsightsViewModel>();
        DataContext = ViewModel;
    }

    public InsightsViewModel ViewModel { get; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadInsightsAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.CloseSeeAllCommand.Execute(null);
    }
}
